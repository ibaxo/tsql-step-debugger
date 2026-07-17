using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Rewrite;

namespace TsqlDbg.Core.State;

// DESIGN §11.7 (C11/A65): the staging model for an INSERT…EXEC capture.
//
// Native INSERT…EXEC BUFFERS the callee's result stream and materializes it into the target
// only when the callee completes successfully (verified live: a mid-capture callee error
// leaves the target empty while the callee's own committed-into-the-open-tran side effects
// survive; and a callee that reads the target mid-execution sees only pre-existing rows).
// The debugger runs the callee statement-by-statement, so to reproduce that it STAGES each
// captured result-returning statement into a per-frame #temp, then FLUSHES the stage into the
// real target at the callee's successful pop (and simply discards the stage on an abnormal pop).
//
// The stage's schema is derived from the target with sys.dm_exec_describe_first_result_set —
// the same server-as-oracle move A59 uses for user types (fact 34d): system_type_name and
// collation_name come back already formatted, so the debugger performs no client-side type
// mapping. The DMF resolves a session #temp target on SQL Server 2019+ (verified live).

/// <summary>One target column, as the server describes the capture projection (§11.7).</summary>
public sealed record CaptureStageColumn(
    string Name,
    string SystemTypeName,   // already formatted: nvarchar(50), decimal(18,2), varchar(max)
    string? Collation,       // char columns only; null otherwise
    bool IsIdentity,
    bool IsComputed,
    bool IsRowVersion,       // system_type_name = 'timestamp' — auto-generated, never staged
    bool IsAssemblyType);    // CLR/assembly UDT — refused (A59 precedent)

/// <summary>
/// DESIGN §11.7 (C11/A65): the resolved staging plan for one INSERT…EXEC capture, or a refusal.
/// A refusal (<see cref="RefusalReason"/> set, <see cref="Plan"/> null) steps the capture OVER,
/// where native performs it as one batch — faithful (conservative-closed).
/// </summary>
public sealed record CaptureStageResolution(CaptureStagePlan? Plan, string? RefusalReason)
{
    public static CaptureStageResolution Refuse(string reason) => new(null, reason);
    public static CaptureStageResolution Accept(CaptureStagePlan plan) => new(plan, null);
}

/// <summary>
/// DESIGN §11.7 (C11/A65): everything the session needs to stage-and-flush one capture.
/// </summary>
public sealed record CaptureStagePlan(
    string StageName,          // #__dbgcap{nonce}_{ordinal}
    string StageCreateDdl,     // CREATE TABLE #stage (seq bigint IDENTITY, <col> … NULL, …)
    string StageInsertTarget,  // "[#stage] ([a], [b])" — the wrap prefixes: INSERT INTO <this> <stmt>
    string FlushCoreSql,       // "INSERT INTO <target> ([a],[b]) SELECT [a],[b] FROM [#stage] ORDER BY [seq]"
    bool TargetHasIdentity);   // F6: a zero-row flush into an identity target must not move the caller's chain

public static class CaptureStagePlanner
{
    // The describe columns the plan needs. is_hidden filters the browse-mode key columns the DMF
    // can add for a rowversion/uniquifier; the projection SELECT never asks for those, but the
    // filter is defence-in-depth.
    private const string DescribeProjection =
        "column_ordinal, name, system_type_name, collation_name, " +
        "is_identity_column, is_computed_column, is_hidden, assembly_qualified_type_name";

    /// <summary>
    /// The describe round-trip's query. <paramref name="projectionSelect"/> is the exact
    /// <c>SELECT … FROM &lt;physical target&gt;</c> whose result columns the stage must mirror —
    /// the explicit column list (an INSERT that named columns) or <c>*</c> (it did not).
    /// </summary>
    public static string BuildDescribeQuery(string projectionSelect) =>
        $"SELECT {DescribeProjection} FROM " +
        $"sys.dm_exec_describe_first_result_set(N'{Quote(projectionSelect)}', NULL, 0) " +
        "ORDER BY column_ordinal;";

    /// <summary>Reads <see cref="BuildDescribeQuery"/>'s single result set into column descriptors,
    /// in ordinal order. An empty result (the DMF could not describe the projection — a permission
    /// gap, an unresolvable target) yields an empty list, which the caller treats as a refusal.</summary>
    public static IReadOnlyList<CaptureStageColumn> ParseDescribe(ResultSet? resultSet)
    {
        var columns = new List<CaptureStageColumn>();
        if (resultSet is null)
        {
            return columns;
        }

        foreach (var row in resultSet.Rows)
        {
            // column_ordinal[0], name[1], system_type_name[2], collation_name[3],
            // is_identity_column[4], is_computed_column[5], is_hidden[6], assembly_qualified_type_name[7]
            if (row[1] is null || row[2] is null)
            {
                continue;
            }

            if (row[6] is not null && Convert.ToBoolean(row[6]))
            {
                continue;                                   // a browse-mode hidden key column
            }

            var collation = row[3]?.ToString();
            var systemType = row[2]!.ToString()!;
            columns.Add(new CaptureStageColumn(
                Name: row[1]!.ToString()!,
                SystemTypeName: systemType,
                Collation: string.IsNullOrEmpty(collation) ? null : collation,
                IsIdentity: row[4] is not null && Convert.ToBoolean(row[4]),
                IsComputed: row[5] is not null && Convert.ToBoolean(row[5]),
                IsRowVersion: string.Equals(systemType, "timestamp", StringComparison.OrdinalIgnoreCase),
                IsAssemblyType: row[7] is not null && !string.IsNullOrEmpty(row[7]!.ToString())));
        }

        return columns;
    }

    /// <summary>
    /// Assembles the staging plan from the described projection, or refuses (→ step-over).
    ///
    /// <paramref name="hasExplicitColumnList"/> distinguishes the two native column-resolution
    /// modes (both verified live):
    ///   • explicit list — the described columns ARE the target columns; a listed identity/
    ///     computed/rowversion column is refused (native either errors or auto-generates it,
    ///     neither of which a stage→flush of a supplied value models);
    ///   • no list — native maps the stream to the target's INSERTABLE columns, auto-skipping
    ///     identity/computed/rowversion, so those are filtered out of the projection here and
    ///     the remaining columns are what both the stage and the flush use.
    /// A CLR/assembly-typed projection column is refused in either mode (A59 precedent).
    /// </summary>
    public static CaptureStageResolution BuildPlan(
        string stageName,
        string seqColumnName,
        string targetPhysicalSql,
        string targetColumnListSql,     // "([a], [b])" for the flush INTO, or "" when no explicit list
        bool hasExplicitColumnList,
        IReadOnlyList<CaptureStageColumn> described)
    {
        if (described.Count == 0)
        {
            return CaptureStageResolution.Refuse(
                "the server could not describe the INSERT target's columns");
        }

        if (described.Any(c => c.IsAssemblyType))
        {
            return CaptureStageResolution.Refuse(
                "the INSERT target has a CLR/assembly-typed column");
        }

        IReadOnlyList<CaptureStageColumn> projection;
        string flushColumnList;
        if (hasExplicitColumnList)
        {
            if (described.FirstOrDefault(c => c.IsIdentity || c.IsComputed || c.IsRowVersion) is { } generated)
            {
                return CaptureStageResolution.Refuse(
                    $"the INSERT column list names the auto-generated column '{generated.Name}'");
            }

            projection = described;
            flushColumnList = targetColumnListSql;          // the source's own list, byte-exact
        }
        else
        {
            projection = described
                .Where(c => !c.IsIdentity && !c.IsComputed && !c.IsRowVersion)
                .ToList();
            if (projection.Count == 0)
            {
                return CaptureStageResolution.Refuse(
                    "the INSERT target has no insertable columns");
            }

            flushColumnList = " (" + string.Join(", ", projection.Select(c => Bracket(c.Name))) + ")";
        }

        // F5 (Fable §10 review): a duplicate projection column (`INSERT #t (a, a) EXEC …`, native
        // msg 264 — statement-level) is described WITHOUT error by the DMF (two columns both named
        // `a`), and would build a stage `CREATE TABLE` with two `[a]` columns → msg 2705, an uncaught
        // executor exception on the push path. Refuse → step-over, where the engine raises its 264.
        if (projection.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            return CaptureStageResolution.Refuse(
                "the INSERT column list names a column more than once");
        }

        var targetHasIdentity = described.Any(c => c.IsIdentity);
        var stageBracket = Bracket(stageName);
        var columnNames = string.Join(", ", projection.Select(c => Bracket(c.Name)));

        var stageColumns = new List<string>(projection.Count + 1)
        {
            // The seq column pins the flush's row order to stream order — the only thing that makes
            // identity assignment at the target contiguous-and-in-order (C28's guarantee, §9).
            $"{Bracket(seqColumnName)} bigint IDENTITY(1,1) NOT NULL",
        };
        foreach (var column in projection)
        {
            // Stage columns are typed as the target's (so a stream-time CONVERT fault fires at the
            // stage INSERT — native's stream timing) but ALWAYS nullable and constraint-free (so a
            // NOT NULL / CHECK violation fires at the FLUSH — native's materialization timing).
            var collate = column.Collation is { } c ? $" COLLATE {c}" : string.Empty;
            stageColumns.Add($"{Bracket(column.Name)} {column.SystemTypeName}{collate} NULL");
        }

        var createDdl = $"CREATE TABLE {stageBracket} (\n    {string.Join(",\n    ", stageColumns)}\n);";
        var stageInsertTarget = $"{stageBracket} ({columnNames})";
        var flushCore =
            $"INSERT INTO {targetPhysicalSql}{flushColumnList} " +
            $"SELECT {columnNames} FROM {stageBracket} ORDER BY {Bracket(seqColumnName)}";

        return CaptureStageResolution.Accept(
            new CaptureStagePlan(stageName, createDdl, stageInsertTarget, flushCore, targetHasIdentity));
    }

    private static string Bracket(string identifier) => RewriteContext.BracketIdentifier(identifier);

    private static string Quote(string value) => value.Replace("'", "''");
}
