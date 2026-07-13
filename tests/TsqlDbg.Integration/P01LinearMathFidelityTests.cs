using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §20.3 fidelity harness (M0 slice: run-to-end only, no step/boost passes yet
// — those land with the milestones that implement stepping/boost) + §20.4 corpus
// fixture p01_linear_math. DESIGN §22 M0 accept criterion: "p01 output matches
// native."
//
// CLAUDE.md working rule: "Integration/fidelity: dotnet test tests/TsqlDbg.Integration
// (skips cleanly when TSQLDBG_TEST_CONN is unset; never fake a pass)." Uses
// Xunit.SkippableFact so an unset env var reports Skipped, not a silently-passing
// no-op Fact.
//
// Auth note: TSQLDBG_TEST_CONN's Data Source/Initial Catalog are reused, but
// SqlConnectionStringFactory (DESIGN §4) always builds "Integrated Security=SSPI" —
// SQL auth is explicitly out of scope until a later milestone (§4: "SQL auth optional
// later"). TSQLDBG_TEST_CONN must therefore point at a server reachable via the
// current Windows identity.
public sealed class P01LinearMathFidelityTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ForLinearMathProc()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database);

        Assert.Equal(native, debugged);
    }

    // M7 matrix cell (design note §6.1/§6.2): mechanical pass-2 addition -- this
    // fixture is EXEC-free, so Into is trajectory-identical to Over; asserted (runs
    // the full pipeline and can genuinely fail), not assumed.
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_SteppedInto()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database, StepKind.Into);

        Assert.Equal(native, debugged);
    }

    // M6 item 6 (B9): fidelity pass 3 — continue with boost on. Assertions identical
    // to the pass-1 method above (this fixture has no IF/WHILE, so boost never fires;
    // it is the refusal-equivalence baseline).
    [SkippableFact]
    public async Task DebuggerRun_MatchesNativeRun_ContinueBoost()
    {
        var rawConnectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(rawConnectionString),
            $"{ConnEnvVar} is not set; skipping fidelity test (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(rawConnectionString);
        var server = csb.DataSource;
        var database = csb.InitialCatalog;

        await DeployFixtureAsync(rawConnectionString!);

        var native = await RunNativeAsync(rawConnectionString!);
        var debugged = await RunThroughDebuggerAsync(server, database, boost: true);

        Assert.Equal(native, debugged);
    }

    private static async Task DeployFixtureAsync(string connectionString)
    {
        var sqlPath = Path.Combine(AppContext.BaseDirectory, "corpus", "p01_linear_math.sql");
        var script = await File.ReadAllTextAsync(sqlPath);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync();
    }

    // DESIGN §20.3 step 2: "Native run: BEGIN TRAN; EXEC …; SELECT <observable
    // projection>; ROLLBACK."
    private static async Task<(int Sum, int Product)> RunNativeAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tran = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var command = connection.CreateCommand();
        command.Transaction = tran;
        command.CommandText = "EXEC dbo.p01_linear_math @A = 3, @B = 4;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var result = (reader.GetInt32(0), reader.GetInt32(1));
        await reader.DisposeAsync();

        await tran.RollbackAsync();
        return result;
    }

    // DESIGN §20.3 step 3: "Debugger run: same call through Core ..."
    private static async Task<(int Sum, int Product)> RunThroughDebuggerAsync(
        string server, string database, StepKind stepKind = StepKind.Over, bool boost = false)
    {
        var target = new TargetEntry(server, "test", AllowWrites: false, Options: null);
        var options = new SessionOptions(
            server,
            database,
            LaunchMode.Procedure,
            "dbo.p01_linear_math",
            new Dictionary<string, string> { ["@A"] = "3", ["@B"] = "4" },
            ScriptText: null,
            Boost: boost);

        var result = await SessionHost.RunAsync(options, target, stepKind: stepKind);

        var resultSet = Assert.Single(result.Execution.ResultSets);
        var row = Assert.Single(resultSet.Rows);
        return ((int)row[0]!, (int)row[1]!);
    }
}
