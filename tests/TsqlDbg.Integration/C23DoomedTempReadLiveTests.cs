using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §21 caveat C23, object-existence face — the machine-checked pin required by
// the M4-gate ruling amendment (docs/archive/reviews/m4-c23-doom-temp-severity-fable.md §5).
// C23's object-existence divergence is a RUN-SHAPE divergence (missing result set,
// caller CATCH entered where native never enters it, or whole-session termination),
// which §20.3.1.6 manifests cannot exempt — manifests exclude fields, not control
// flow — so no green fidelity fixture can exist for it. Instead, this probe (the
// Fact7 live-probe pattern) asserts the DIVERGENCE ITSELF as the documented,
// by-design behavior. If either test here starts failing, the engine or the §10
// routing changed underneath C23 — triage per CLAUDE.md escalation trigger (a).
//
// A14 (same review doc §4.3, ratified 2026-07-06 and built): composition now
// pre-flights doomed-era references that resolved through the §9 registry — RunToEnd
// emits the C23 diagnostic as a console note and proceeds (run shape unchanged), and
// a resulting terminal fault carries the C23 citation + the ORIGINAL table name
// alongside the engine 208. The Caveat-C23 assertions below pin that ratified
// surface (this is the assertion revision A14's ratification called for, not a
// weakening — the pre-A14 assertions all still hold and remain below).
public sealed class C23DoomedTempReadLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    // Doom-era read of a #temp the transaction itself created, with NO enclosing TRY
    // in any frame past the read's own (same-scope, hence useless) one.
    private const string Frame0Fixture = """
        CREATE OR ALTER PROCEDURE dbo.c23_probe_frame0 AS
        BEGIN
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            CREATE TABLE #shared (id int IDENTITY(1,1) PRIMARY KEY, val int);
            INSERT INTO #shared (val) VALUES (1), (2), (3);

            DECLARE @Dummy int;
            BEGIN TRY
                SET @Dummy = 1 / 0;              -- dooms the transaction (XACT_ABORT ON)
            END TRY
            BEGIN CATCH
                BEGIN TRY
                    SELECT SUM(val) FROM #shared;  -- doom-era read of the transaction's OWN prior write
                END TRY
                BEGIN CATCH
                    -- native: never reached (the read succeeds). Debugger: ALSO never
                    -- reached — the 208 is compile-class, same-scope-uncatchable (§10.1).
                END CATCH
                IF XACT_STATE() = -1 ROLLBACK;
            END CATCH
        END
        """;

    [SkippableFact]
    public async Task Frame0_NativeReadsOwnWrite_DebuggerSessionTerminates()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(connectionString), $"{ConnEnvVar} not set.");
        await DeployAsync(connectionString!, Frame0Fixture);

        // Native, §20.3 step 2 shape (explicit BEGIN TRANSACTION as text — see P05's
        // header for why that is the faithful comparison basis for doom).
        object? nativeSum = null;
        Exception? nativeError = null;
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            try
            {
                await ExecAsync(connection, "BEGIN TRANSACTION;");
                await using var exec = connection.CreateCommand();
                exec.CommandText = "EXEC dbo.c23_probe_frame0;";
                await using var reader = await exec.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    nativeSum = reader.GetValue(0);
                }
            }
            catch (Exception ex)
            {
                nativeError = ex;
            }
            finally
            {
                await ExecAsync(connection, "IF @@TRANCOUNT > 0 ROLLBACK;");
            }
        }

        Exception? debuggerError = null;
        try
        {
            await SessionHost.RunAsync(
                MakeOptions(connectionString!, "dbo.c23_probe_frame0", out var target), target);
        }
        catch (Exception ex)
        {
            debuggerError = ex;
        }

        Assert.Null(nativeError);                       // native: no error anywhere
        Assert.Equal(6, Convert.ToInt32(nativeSum));    // native: doomed read of own write WORKS
        var fault = Assert.IsType<SessionFaultException>(debuggerError);
        Assert.Contains("208", fault.Message);          // real engine 208 on the renamed table
        Assert.Contains("#shared", fault.Message);
        Assert.Contains("§10.3 batch-aborting class", fault.Message);
        // A14: the terminal fault now names the caveat and the ORIGINAL table, so the
        // death is self-explaining instead of a bare renamed-object 208.
        Assert.Contains("Caveat C23", fault.Message);
        Assert.Contains("forced rollback", fault.Message);
    }

    // Same doom-era read in a CALLEE whose CALLER wraps the EXEC in TRY/CATCH: the
    // §10.1 propagate walk routes the 208 to the caller's CATCH (fact 23-F), so the
    // session SURVIVES — but native raises no error at all, so the caller CATCH runs
    // where native never enters it, and the SUM result set is missing.
    private const string CalleeFixture = """
        CREATE OR ALTER PROCEDURE dbo.c23_probe_callee AS
        BEGIN
            SET NOCOUNT ON;
            SET XACT_ABORT ON;
            CREATE TABLE #shared2 (id int IDENTITY(1,1) PRIMARY KEY, val int);
            INSERT INTO #shared2 (val) VALUES (1), (2), (3);
            DECLARE @Dummy int;
            BEGIN TRY
                SET @Dummy = 1 / 0;
            END TRY
            BEGIN CATCH
                SELECT SUM(val) FROM #shared2;   -- doom-era read; real 208 under the debugger
            END CATCH
            -- doom exit deliberately left to the harness cleanup: a callee ROLLBACK
            -- raises native 266 at EXEC exit and puts the caller in its CATCH natively
            -- too, blurring the control-flow comparison (observed live).
        END
        """;

    private const string CallerFixture = """
        CREATE OR ALTER PROCEDURE dbo.c23_probe_caller AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @Caught int = 0;
            BEGIN TRY
                EXEC dbo.c23_probe_callee;
            END TRY
            BEGIN CATCH
                SET @Caught = ERROR_NUMBER();
                IF XACT_STATE() = -1 ROLLBACK;
            END CATCH
            SELECT @Caught AS caught;
        END
        """;

    [SkippableFact]
    public async Task CallerCatch_Native0_Debugger208_AndSumResultSetMissing()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(connectionString), $"{ConnEnvVar} not set.");
        await DeployAsync(connectionString!, CalleeFixture);
        await DeployAsync(connectionString!, CallerFixture);

        object? nativeSum = null;
        object? nativeCaught = null;
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await ExecAsync(connection, "BEGIN TRANSACTION;");
            try
            {
                await using var exec = connection.CreateCommand();
                exec.CommandText = "EXEC dbo.c23_probe_caller;";
                await using var reader = await exec.ExecuteReaderAsync();
                await reader.ReadAsync();
                nativeSum = reader.GetValue(0);
                await reader.NextResultAsync();
                await reader.ReadAsync();
                nativeCaught = reader.GetValue(0);
            }
            catch (SqlException ex) when (nativeCaught is not null && ex.Number is 3998 or 266)
            {
                // Expected: nothing in the native run rolls the doomed transaction
                // back (the caller CATCH never runs), so the batch end raises the
                // fact-22 3998 epilogue AFTER both result sets streamed.
            }
            finally
            {
                await ExecAsync(connection, "IF @@TRANCOUNT > 0 ROLLBACK;");
            }
        }

        // Step INTO so the callee is a real debugger frame and the §10.1 propagate
        // walk actually crosses frames (a step-over EXEC is one opaque native call).
        SessionResult? debugged = null;
        Exception? debuggerError = null;
        try
        {
            debugged = await SessionHost.RunAsync(
                MakeOptions(connectionString!, "dbo.c23_probe_caller", out var target), target,
                trace: null, StepKind.Into);
        }
        catch (Exception ex)
        {
            debuggerError = ex;
        }

        Assert.Equal(6, Convert.ToInt32(nativeSum));
        Assert.Equal(0, Convert.ToInt32(nativeCaught));   // native caller CATCH never entered

        Assert.Null(debuggerError);                       // session SURVIVES (routed, not terminal)
        var finalSet = debugged!.Execution.ResultSets[^1];
        Assert.Equal(208, Convert.ToInt32(finalSet.Rows[^1][0]!));  // caller CATCH ran, read 208
        Assert.Single(debugged.Execution.ResultSets);     // the SUM set exists natively only
        // A14: RunToEnd emitted the pre-flight diagnostic as a console note and
        // PROCEEDED — run shape unchanged (the assertions above are the pre-A14 ones,
        // untouched), with the C23 explanation now in the run's message stream.
        Assert.Contains(debugged.Execution.Messages, m => m.Contains("Caveat C23") && m.Contains("#shared2"));
    }

    private static SessionOptions MakeOptions(string connectionString, string procedure, out TargetEntry target)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);
        target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        return new SessionOptions(
            csb.DataSource, csb.InitialCatalog, LaunchMode.Procedure, procedure,
            new Dictionary<string, string>(), ScriptText: null);
    }

    private static async Task DeployAsync(string connectionString, string fixture)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, fixture);
    }

    private static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
