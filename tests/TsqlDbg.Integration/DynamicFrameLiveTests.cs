using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §11.6 (A58): dynamic-SQL step-into — `EXEC sp_executesql` / `EXEC(@str)` push a DYNAMIC
// FRAME over the runtime text instead of being stepped over as one opaque statement.
//
// WHY THESE ARE LIVE, AND WHY THE FIDELITY HARNESS CANNOT REPLACE THEM. The §20.3 harness drives
// `RunToEndAsync`, which never steps INTO anything — so it is structurally blind to this entire
// feature, exactly as it was blind to A44 (continue-through-GO), A46 (console writes) and A54
// (implicit-return stop). A dynamic frame that silently fell back to step-over would leave every
// fidelity test green. So each test here does two things at once:
//
//   1. asserts a dynamic frame was ACTUALLY pushed (`SawDynamicFrame`) — without this the
//      comparisons below would pass vacuously, which is the whole trap;
//   2. compares the stepped-INTO run against a NATIVE run of the same script, byte for byte.
//
// (2) is the real contract: stepping into dynamic SQL must not change what the script does. The
// engine facts these rest on (33a–33f) were probed live before any code was written and are
// recorded in docs/engine-facts.md.
public sealed class DynamicFrameLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed class NullSink : ITraceSink
    {
        public void Event(string category, string message) { }
    }

    private sealed record DebuggerRun(string Rendered, bool SawDynamicFrame, int MaxDepth, List<string> Messages);

    private static string RequireConnection()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");
        return raw!;
    }

    // Every result set, flattened to text — the comparison surface. Values are rendered
    // invariantly so a native NULL and a debugger NULL are the same three characters.
    private static string Render(IEnumerable<ResultSet> sets)
    {
        var sb = new StringBuilder();
        foreach (var set in sets)
        {
            sb.Append("cols: ").AppendLine(string.Join(" | ", set.Columns));
            foreach (var row in set.Rows)
            {
                sb.AppendLine(string.Join(" | ", row.Select(v =>
                    v is null or DBNull ? "NULL" : Convert.ToString(v, CultureInfo.InvariantCulture))));
            }

            sb.AppendLine("--");
        }

        return sb.ToString();
    }

    // The oracle: the script as the server itself runs it, in one batch. Rolled back so the two
    // runs see the same starting world.
    private static async Task<string> RunNativeAsync(string script)
    {
        var csb = new SqlConnectionStringBuilder(RequireConnection());
        await using var connection = new SqlConnection(csb.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        var sets = new List<ResultSet>();
        await using (var command = new SqlCommand(script, connection, transaction))
        {
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                if (reader.FieldCount == 0)
                {
                    continue;
                }

                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var rows = new List<IReadOnlyList<object?>>();
                while (await reader.ReadAsync())
                {
                    var row = new object?[reader.FieldCount];
                    reader.GetValues(row!);
                    rows.Add(row);
                }

                sets.Add(new ResultSet(columns, rows));
            }
            while (await reader.NextResultAsync());
        }

        await transaction.RollbackAsync();
        return Render(sets);
    }

    // The same script through the debugger, stepping INTO everything. StepKind.Into is safe to use
    // unconditionally: the interpreter only intercepts it on an EXEC statement unit (§11.1), so on
    // every other statement it behaves exactly like a plain step.
    private static async Task<DebuggerRun> RunSteppingIntoAsync(string script)
    {
        var csb = new SqlConnectionStringBuilder(RequireConnection());
        var options = new SessionOptions(csb.DataSource, csb.InitialCatalog, LaunchMode.Script, null, null, script);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var nonce = SqlConnectionStringFactory.NewNonce();

        await using var connection = new SqlConnection(SqlConnectionStringFactory.Build(options, target, nonce));
        await connection.OpenAsync();
        var executor = new SqlStatementExecutor(connection, options.CommandTimeoutSeconds);
        var session = new Session(options, executor, new NullSink(), nonce);

        var sets = new List<ResultSet>();
        var messages = new List<string>();
        var sawDynamicFrame = false;
        var maxDepth = 1;
        try
        {
            await session.InitializeAsync();
            var guard = 0;
            while (!session.IsCompleted && !session.IsBroken)
            {
                if (++guard > 400)
                {
                    throw new InvalidOperationException("dynamic-frame live run did not converge");
                }

                var (stepSets, stepMessages) = await session.StepAsync(StepKind.Into);
                sets.AddRange(stepSets);
                messages.AddRange(stepMessages);

                maxDepth = Math.Max(maxDepth, session.Frames.Count);
                if (session.TopFrame?.Module.IsDynamic == true)
                {
                    sawDynamicFrame = true;
                }
            }
        }
        finally
        {
            await session.TeardownAsync();
            await executor.DisposeAsync();
        }

        Assert.False(session.IsBroken, "the session broke: " + string.Join(" / ", messages));
        return new DebuggerRun(Render(sets), sawDynamicFrame, maxDepth, messages);
    }

    private static async Task AssertSteppedIntoAndFaithfulAsync(string script)
    {
        var native = await RunNativeAsync(script);
        var run = await RunSteppingIntoAsync(script);

        // The guard against a vacuous pass: if the debugger had quietly stepped OVER the dynamic
        // SQL (the pre-A58 behaviour) the render comparison below would still match perfectly.
        Assert.True(run.SawDynamicFrame,
            "no dynamic frame was ever pushed — the debugger stepped OVER the dynamic SQL, so the " +
            "fidelity comparison below would pass vacuously. Messages: " + string.Join(" / ", run.Messages));
        Assert.Equal(2, run.MaxDepth);
        Assert.Equal(native, run.Rendered);
    }

    // ---- sp_executesql: typed parameters, OUTPUT copy-back (facts 33f / §11.5) --------

    [SkippableFact]
    public async Task StepIntoSpExecuteSql_TypedParamsAndOutput_MatchesNative()
    {
        const string script = """
            DECLARE @in int = 5, @out int, @label nvarchar(20);
            EXEC sp_executesql
                 N'SET @b = @a * 10;
            SET @t = N''from-dynamic'';',
                 N'@a int, @b int OUTPUT, @t nvarchar(20) OUTPUT',
                 @a = @in, @b = @out OUTPUT, @t = @label OUTPUT;
            SELECT @out AS out_value, @label AS out_label;
            """;
        await AssertSteppedIntoAndFaithfulAsync(script);
    }

    // ---- the everyday pattern: BUILD the string, then execute it (§11.6) --------------

    [SkippableFact]
    public async Task StepIntoSpExecuteSql_StatementBuiltByConcatenationIntoAVariable_MatchesNative()
    {
        // A T-SQL EXECUTE argument must be a constant or a VARIABLE — `EXEC sp_executesql N'…' +
        // @col` is a syntax error natively (verified live; docs/engine-facts.md fact 33 riders).
        // So the real-world shape builds the text first and passes the variable, and THAT is what
        // must step into: a sysname-typed name concatenated into an nvarchar statement.
        const string script = """
            DECLARE @tbl sysname = N'sys.objects', @out int, @sql nvarchar(400);
            SET @sql = N'SELECT @r = COUNT(*) FROM ' + @tbl + N' WHERE type = ''S'';';
            EXEC sp_executesql @sql, N'@r int OUTPUT', @r = @out OUTPUT;
            SELECT @out AS system_table_count;
            """;
        await AssertSteppedIntoAndFaithfulAsync(script);
    }

    [SkippableFact]
    public async Task StepIntoExecString_ConcatenatedOperands_MatchesNative()
    {
        // EXEC(@a + @b) IS legal — the engine's own `+ ...n` grammar — and ScriptDom hands it to
        // us as ExecutableStringList.Strings, a LIST, which §11.6 concatenates into the frame's
        // text. (This is the one place a dynamic statement may be built inline.)
        const string script = """
            DECLARE @head nvarchar(100) = N'SELECT 6 * 7', @tail nvarchar(100) = N' AS product;';
            EXEC (@head + @tail);
            SELECT 'done' AS marker;
            """;
        await AssertSteppedIntoAndFaithfulAsync(script);
    }

    // ---- fact 33b: the dynamic batch's own #temp dies at its exit; the caller's is visible ----

    [SkippableFact]
    public async Task StepIntoDynamicSql_ChildTempTableDiesAtPop_CallerTempStaysVisible()
    {
        const string script = """
            CREATE TABLE #caller(i int);
            INSERT #caller VALUES (7);
            EXEC sp_executesql N'CREATE TABLE #child(j int);
            INSERT #child SELECT i * 2 FROM #caller;
            SELECT j AS from_child FROM #child;';
            SELECT OBJECT_ID('tempdb..#child') AS child_after, (SELECT i FROM #caller) AS caller_after;
            """;
        await AssertSteppedIntoAndFaithfulAsync(script);
    }

    // ---- fact 33c: ERROR_PROCEDURE() is NULL inside a dynamic batch, ERROR_LINE() is ------
    // ---- relative to the dynamic TEXT. This pins §10.2's SynthesizeProcedure fix: a -------
    // ---- dynamic frame must NOT be named the way a stepped-into procedure frame is. -------

    [SkippableFact]
    public async Task StepIntoDynamicSql_ErrorContextInsideTheDynamicBatch_MatchesNative()
    {
        const string script = """
            DECLARE @sql nvarchar(400) = N'BEGIN TRY
            SELECT 1/0 AS boom;
            END TRY
            BEGIN CATCH
            SELECT ERROR_NUMBER() AS n, ERROR_LINE() AS l, ISNULL(ERROR_PROCEDURE(), N''<null>'') AS p;
            END CATCH';
            EXEC sp_executesql @sql;
            SELECT 'after' AS marker;
            """;
        await AssertSteppedIntoAndFaithfulAsync(script);
    }

    // ---- EXEC(@str): same machinery, no parameter list --------------------------------

    [SkippableFact]
    public async Task StepIntoExecString_MatchesNative()
    {
        const string script = """
            DECLARE @sql nvarchar(200) = N'DECLARE @local int = 3;
            SELECT @local * 14 AS product;';
            EXEC (@sql);
            SELECT 'done' AS marker;
            """;
        await AssertSteppedIntoAndFaithfulAsync(script);
    }

    // ---- §14 boost INSIDE a dynamic frame ---------------------------------------------

    private sealed class RecordingSink : ITraceSink
    {
        public List<(string Category, string Message)> Events { get; } = new();
        public void Event(string category, string message) => Events.Add((category, message));
    }

    [SkippableFact]
    public async Task BoostedContinue_FromInsideADynamicFrame_MatchesNative()
    {
        // The one interaction the other tests do not reach: §14 boost composing the statements of
        // a DYNAMIC frame. Boost slices its composed batch out of the frame's FullScript — which,
        // for a dynamic frame, is the runtime string rather than a catalog definition. That the
        // §7.4 span-patch engine is text-keyed rather than file-keyed is precisely what makes this
        // work, so it deserves an explicit pin rather than an argument.
        //
        // Enter the dynamic frame by stepping, THEN continue: boost is attempted before each step,
        // so a boosted `continue` from the caller would compose the EXEC away and never step in.
        const string script = """
            DECLARE @sql nvarchar(400) = N'DECLARE @i int = 0, @sum int = 0;
            WHILE @i < 5
            BEGIN
                SET @i = @i + 1;
                SET @sum = @sum + @i;
            END
            SELECT @sum AS total;';
            EXEC sp_executesql @sql;
            SELECT 'after' AS marker;
            """;

        var native = await RunNativeAsync(script);

        var csb = new SqlConnectionStringBuilder(RequireConnection());
        var options = new SessionOptions(
            csb.DataSource, csb.InitialCatalog, LaunchMode.Script, null, null, script, Boost: true);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var nonce = SqlConnectionStringFactory.NewNonce();

        await using var connection = new SqlConnection(SqlConnectionStringFactory.Build(options, target, nonce));
        await connection.OpenAsync();
        var executor = new SqlStatementExecutor(connection, options.CommandTimeoutSeconds);
        var trace = new RecordingSink();
        var session = new Session(options, executor, trace, nonce);

        var sets = new List<ResultSet>();
        try
        {
            await session.InitializeAsync();

            // Step INTO until we are inside the dynamic frame.
            var guard = 0;
            while (session.TopFrame?.Module.IsDynamic != true && !session.IsCompleted && !session.IsBroken)
            {
                Assert.True(++guard < 20, "never entered the dynamic frame");
                var (stepSets, _) = await session.StepAsync(StepKind.Into);
                sets.AddRange(stepSets);
            }

            Assert.True(session.TopFrame!.Module.IsDynamic);          // inside the dynamic SQL

            // Now run to the end WITH boost — the WHILE loop being boosted is the dynamic frame's own.
            var result = await session.RunToEndAsync();
            sets.AddRange(result.Execution.ResultSets);
        }
        finally
        {
            await session.TeardownAsync();
            await executor.DisposeAsync();
        }

        Assert.False(session.IsBroken);
        // Non-hollow (the §6.2 rule): prove boost ACTUALLY engaged, or this test would be a plain
        // continue wearing a boost label.
        Assert.Contains(trace.Events, e => e.Category.StartsWith("boost."));
        Assert.Equal(native, Render(sets));                           // 1+2+3+4+5 = 15, byte-identical
    }

    // ---- §11.6 refusals: the shape is ineligible → step over, and the SERVER raises -------
    // ---- its own error. The debugger must never "succeed" where production fails. --------

    [SkippableFact]
    public async Task SpExecuteSql_VarcharStatement_StepsOver_AndTheServerRaises214()
    {
        // Natively this is msg 214 (sp_executesql demands ntext/nchar/nvarchar). Stepping INTO it
        // would run the text happily and hide the bug — so §11.6 refuses, and the native EXEC
        // surfaces the engine's own 214 through the §10.3 pipeline.
        const string script = """
            DECLARE @sql varchar(100) = 'SELECT 1 AS one;';
            EXEC sp_executesql @sql;
            """;
        var run = await RunSteppingIntoAsync(script);

        Assert.False(run.SawDynamicFrame);                        // refused — no frame pushed
        Assert.Contains(run.Messages, m => m.Contains("not provably nvarchar"));
        Assert.Contains(run.Messages, m => m.Contains("214"));    // the engine's own error, not a synthetic one
    }

    [SkippableFact]
    public async Task DynamicSql_ContainingGo_StepsOver_AndTheServerRaisesASyntaxError()
    {
        // GO is not a T-SQL statement — inside dynamic SQL it is msg 102. ScriptDom would split it
        // into batches, so §11.6 refuses rather than inventing semantics the engine lacks.
        const string script = """
            DECLARE @sql nvarchar(200) = N'PRINT 1
            GO
            PRINT 2';
            EXEC sp_executesql @sql;
            """;
        var run = await RunSteppingIntoAsync(script);

        Assert.False(run.SawDynamicFrame);
        Assert.Contains(run.Messages, m => m.Contains("GO batch separator"));
        Assert.Contains(run.Messages, m => m.Contains("102"));
    }
}
