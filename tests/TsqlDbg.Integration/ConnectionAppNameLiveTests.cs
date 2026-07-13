using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §4: the connection's Application Name (surfaced by SQL Server as program_name) is a
// human-readable "T-SQL Step Debugger [{nonce}]" so a debug session is recognizable in sp_who2 /
// sys.dm_exec_sessions when diagnosing locks, with the nonce distinguishing concurrent sessions.
// Only confirmable LIVE — the server reflects the connection-string Application Name back as
// program_name.
public sealed class ConnectionAppNameLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableFact]
    public async Task ProgramName_IsHumanReadable_WithNonce()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        var csb = new SqlConnectionStringBuilder(raw);
        var options = new SessionOptions(csb.DataSource, csb.InitialCatalog, LaunchMode.Script, null, null, "SELECT 1;");
        var target = new TargetEntry(csb.DataSource, "test", AllowWrites: false, Options: null);
        var nonce = SqlConnectionStringFactory.NewNonce();

        await using var connection = new SqlConnection(SqlConnectionStringFactory.Build(options, target, nonce));
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT program_name FROM sys.dm_exec_sessions WHERE session_id = @@SPID;";
        var programName = (string?)await cmd.ExecuteScalarAsync();

        Assert.Equal($"T-SQL Step Debugger [{nonce}]", programName);
    }
}
