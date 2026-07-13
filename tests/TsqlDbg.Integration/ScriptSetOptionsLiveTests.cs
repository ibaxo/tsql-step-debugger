using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Execution;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// A49 (§5.4/§11.2): in the ad-hoc SCRIPT frame (frame 0, not a module) a runtime
// SET QUOTED_IDENTIFIER / ANSI_NULLS genuinely takes effect for the FOLLOWING statements
// and carries across GO (fact 32d) — unlike a module frame, whose parse-time options are
// pinned at CREATE time (fact 16, covered by P19/P16, both PROCEDURE mode). Ivan's report:
// `SET ANSI_NULLS OFF` in a script did nothing because every composed batch re-pinned the
// frame's frozen default. Only observable LIVE — the divergence is in the per-statement
// composed batch sent to a real server; RunToEndAsync/the fidelity harness can't see it
// (same lesson as A44/A47/A48). Proven by reading @@OPTIONS through a stepped SELECT's own
// result set (which A50 now also surfaces to the Debug Console).
public sealed class ScriptSetOptionsLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private sealed class NullSink : ITraceSink
    {
        public void Event(string category, string message) { }
    }

    private static async Task<List<ResultSet>> DriveScriptAsync(string script, StepKind stepKind = StepKind.Over)
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var options = new SessionOptions(csb.DataSource, csb.InitialCatalog, LaunchMode.Script, null, null, script);
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var nonce = SqlConnectionStringFactory.NewNonce();

        await using var connection = new SqlConnection(SqlConnectionStringFactory.Build(options, target, nonce));
        await connection.OpenAsync();
        var executor = new SqlStatementExecutor(connection, options.CommandTimeoutSeconds);
        var session = new Session(options, executor, new NullSink(), nonce);
        var sets = new List<ResultSet>();
        try
        {
            await session.InitializeAsync();
            var guard = 0;
            while (!session.IsCompleted && !session.IsBroken)
            {
                if (++guard > 200)
                {
                    throw new InvalidOperationException("script SET-option live run did not converge");
                }

                var (stepSets, _) = await session.StepAsync(stepKind);
                sets.AddRange(stepSets);
            }
        }
        finally
        {
            await session.TeardownAsync();     // rolls back — self-cleaning (incl. any CREATE)
            await executor.DisposeAsync();
        }

        return sets;
    }

    // Some set has ALL the named columns AND a row whose values match (case-insensitive).
    private static bool HasRow(List<ResultSet> sets, params (string Col, string Val)[] expected)
    {
        foreach (var s in sets)
        {
            var positions = expected
                .Select(e => (e.Val, Pos: IndexOf(s.Columns, e.Col)))
                .ToArray();
            if (positions.Any(p => p.Pos < 0))
            {
                continue;
            }

            if (s.Rows.Any(row => positions.All(p =>
                    string.Equals(row[p.Pos]?.ToString(), p.Val, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    [SkippableFact]
    public async Task Frame0_SetAnsiNullsAndQuotedIdentifierOff_TakeEffectForFollowingSelect()
    {
        // Ivan's exact shape (fact09 lines 25-29): SET both OFF, then read @@OPTIONS. Before
        // A49 the SELECT saw ON/ON (frozen frame default re-pinned every composed batch); now
        // it faithfully sees OFF/OFF. RED->green for the fix.
        var sets = await DriveScriptAsync(
            "SET QUOTED_IDENTIFIER OFF;\n" +
            "SET ANSI_NULLS OFF;\n" +
            "SELECT\n" +
            "    CASE WHEN (32 & @@OPTIONS) = 32 THEN 'ON' ELSE 'OFF' END AS ANSI_NULLS,\n" +
            "    CASE WHEN (256 & @@OPTIONS) = 256 THEN 'ON' ELSE 'OFF' END AS QUOTED_IDENTIFIER;\n");

        Assert.True(
            HasRow(sets, ("ANSI_NULLS", "OFF"), ("QUOTED_IDENTIFIER", "OFF")),
            "a script-frame SET ANSI_NULLS/QUOTED_IDENTIFIER OFF must be reflected in @@OPTIONS of the following SELECT");
    }

    [SkippableFact]
    public async Task Frame0_SetAnsiNullsOff_PersistsAcrossGoBoundary()
    {
        // fact 32d + A49: SET options carry across GO (EnterBatchAsync inherits outgoing.SetEnv),
        // so the change made in batch 1 is still in effect for batch 2's SELECT.
        var sets = await DriveScriptAsync(
            "SET ANSI_NULLS OFF;\n" +
            "GO\n" +
            "SELECT CASE WHEN (32 & @@OPTIONS) = 32 THEN 'ON' ELSE 'OFF' END AS ANSI_NULLS;\n");

        Assert.True(
            HasRow(sets, ("ANSI_NULLS", "OFF")),
            "SET ANSI_NULLS OFF in batch 1 must still be OFF for batch 2 after the GO boundary");
    }

    [SkippableFact]
    public async Task ScriptFrame_QuotedIdentifierOn_IsTheDefault_WhenNeverSet()
    {
        // Control: with no SET, the script frame keeps the connection defaults (ON/ON) — the
        // A49 tracking only MOVES the pin on an actual SET, it doesn't change the baseline.
        var sets = await DriveScriptAsync(
            "SELECT\n" +
            "    CASE WHEN (32 & @@OPTIONS) = 32 THEN 'ON' ELSE 'OFF' END AS ANSI_NULLS,\n" +
            "    CASE WHEN (256 & @@OPTIONS) = 256 THEN 'ON' ELSE 'OFF' END AS QUOTED_IDENTIFIER;\n");

        Assert.True(
            HasRow(sets, ("ANSI_NULLS", "ON"), ("QUOTED_IDENTIFIER", "ON")),
            "an untouched script frame keeps the connection default ON/ON");
    }

    [SkippableFact]
    public async Task OtherRuntimeSetOptions_AlreadyTakeEffect_WithoutA49SpecialHandling()
    {
        // The corollary that scopes A49: QUOTED_IDENTIFIER/ANSI_NULLS were the ONLY options the
        // composed-batch preamble re-pins, so they were the only ones a runtime SET couldn't
        // move. Every OTHER runtime option persists on the connection between the debugger's
        // (healthy) direct language batches with no special handling — proven here for a value
        // option (DATEFIRST) and an on/off option (ANSI_WARNINGS, @@OPTIONS bit 8).
        var sets = await DriveScriptAsync(
            "SET DATEFIRST 1;\n" +
            "SET ANSI_WARNINGS OFF;\n" +
            "SELECT\n" +
            "    @@DATEFIRST AS df,\n" +
            "    CASE WHEN (8 & @@OPTIONS) = 8 THEN 'ON' ELSE 'OFF' END AS ANSI_WARNINGS;\n");

        Assert.True(HasRow(sets, ("df", "1")), "SET DATEFIRST persists between the debugger's frame-0 batches");
        Assert.True(HasRow(sets, ("ANSI_WARNINGS", "OFF")), "SET ANSI_WARNINGS OFF takes effect and persists");
    }

    [SkippableFact]
    public async Task ProcScopedIsolation_ReflectedInsideTheProc_AndRevertedAfterExit()
    {
        // Ivan's step-in/step-out expectation, in SCRIPT mode: a proc sets SERIALIZABLE; that
        // is observable while it runs and reverts at module exit (fact 9). The push/pop
        // restore MECHANISM itself is pinned by P16 (procedure mode, Into+Over); here we prove
        // the SCRIPT-mode EXEC path is faithful too. The CREATE runs bare (A48).
        var sets = await DriveScriptAsync(
            "CREATE OR ALTER PROCEDURE dbo.p_a49_iso AS\n" +
            "BEGIN\n" +
            "    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;\n" +
            "    SELECT CASE transaction_isolation_level WHEN 4 THEN 'SERIALIZABLE' ELSE 'OTHER' END AS iso_inside\n" +
            "    FROM sys.dm_exec_sessions WHERE session_id = @@SPID;\n" +
            "END\n" +
            "GO\n" +
            "EXEC dbo.p_a49_iso;\n" +
            "GO\n" +
            "SELECT CASE transaction_isolation_level WHEN 2 THEN 'READ COMMITTED' WHEN 4 THEN 'SERIALIZABLE' ELSE 'OTHER' END AS iso_after\n" +
            "FROM sys.dm_exec_sessions WHERE session_id = @@SPID;\n");

        Assert.True(HasRow(sets, ("iso_inside", "SERIALIZABLE")), "the proc's isolation change is observable while it runs");
        Assert.True(HasRow(sets, ("iso_after", "READ COMMITTED")), "fact 9: proc-scoped isolation reverts at module exit");
    }

    [SkippableFact]
    public async Task ValueSetOptions_OfEveryAstShape_ExecuteAndPersist()
    {
        // A53 reclassifies SetCommandStatement (DATEFIRST/LOCK_TIMEOUT/DEADLOCK_PRIORITY) and
        // SetTextSizeStatement/SetRowCountStatement to SetOption — confirm they still execute
        // (as SetOption leaves) and persist across the debugger's frame-0 batches.
        var sets = await DriveScriptAsync(
            "SET DATEFIRST 2;\n" +
            "SET LOCK_TIMEOUT 3000;\n" +
            "SET DEADLOCK_PRIORITY LOW;\n" +
            "SET TEXTSIZE 4096;\n" +
            "SELECT @@DATEFIRST AS df, @@LOCK_TIMEOUT AS lt, @@TEXTSIZE AS ts;\n");

        Assert.True(HasRow(sets, ("df", "2")), "DATEFIRST (GeneralSetCommand) executes and persists");
        Assert.True(HasRow(sets, ("lt", "3000")), "LOCK_TIMEOUT (GeneralSetCommand) executes and persists");
        Assert.True(HasRow(sets, ("ts", "4096")), "TEXTSIZE (SetTextSizeStatement) executes and persists");
    }

    [SkippableFact]
    public async Task ValueSetOption_ChangedInsideAProc_IsRevertedOnStepOut()
    {
        // A53 §11.2, the value-option analogue of P16's isolation case. Stepping INTO the proc
        // runs its body as separate stepped batches — there is no EXEC proc-scope, so the ENGINE
        // does NOT auto-revert DATEFIRST at module exit; the debugger's pop RestoreStatements
        // must. Pre-A53 DATEFIRST was untracked (SuSubKind.Other) → it would leak out as 3.
        var sets = await DriveScriptAsync(
            "CREATE OR ALTER PROCEDURE dbo.p_a53_df AS\n" +
            "BEGIN\n" +
            "    SET DATEFIRST 3;\n" +
            "    SELECT @@DATEFIRST AS df_inside;\n" +
            "END\n" +
            "GO\n" +
            "EXEC dbo.p_a53_df;\n" +
            "GO\n" +
            "SELECT @@DATEFIRST AS df_after;\n",
            StepKind.Into);

        Assert.True(HasRow(sets, ("df_inside", "3")), "the proc's DATEFIRST change is observable inside it");
        Assert.True(HasRow(sets, ("df_after", "7")), "A53: DATEFIRST is restored to the caller's value on step-out");
    }
}
