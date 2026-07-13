using Microsoft.Data.SqlClient;
using TsqlDbg.Adapter;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;
using TsqlDbg.Core.Tracing;
using Xunit;

namespace TsqlDbg.Integration;

// DESIGN §4/§16 (A41, M10) — SQL-login authentication, end-to-end against a REAL server.
// Gated on TSQLDBG_TEST_SQL_CONN, a SQL-AUTH connection string (User ID + Password); skips
// cleanly when unset (never fake a pass — CLAUDE.md). Verified live 2026-07-12 against the
// local login `i`. Proves: (1) a real debug session connects AS the SQL login and runs; (2)
// the password never appears in the trace (caveat C27 — it flows only as the transient
// OpenAsync/Build param, never in SessionOptions/args/trace); (3) the metadata CLI
// (--list-databases / --test-connection) works under SQL auth and never prints the password;
// (4) a wrong password is actually rejected (the login is enforced, not silently integrated).
//
// The Core connection-string factory's SQL-auth shaping is separately covered offline by
// SqlConnectionStringFactoryTests; these are the live end-to-end half.
[Collection("SqlAuthSerial")]
public sealed class SqlAuthLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_SQL_CONN";
    private const string PasswordEnvVar = "TSQLDBG_SQL_PASSWORD";

    private sealed record SqlConn(string Server, string Database, string User, string Password, bool TrustCert);

    private sealed class RecordingSink : ITraceSink
    {
        public List<(string Category, string Message)> Events { get; } = new();
        public void Event(string category, string message) => Events.Add((category, message));
    }

    private static SqlConn Require()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping SQL-auth live tests (never fake a pass — CLAUDE.md). " +
            "Set it to a SQL-login connection string, e.g. " +
            "\"Server=localhost;Database=TsqlDbgScratch;User ID=i;Password=123456;TrustServerCertificate=true\".");

        var csb = new SqlConnectionStringBuilder(raw);
        Assert.False(string.IsNullOrEmpty(csb.UserID), $"{ConnEnvVar} must be a SQL-auth string (User ID + Password).");
        return new SqlConn(csb.DataSource, csb.InitialCatalog, csb.UserID, csb.Password, csb.TrustServerCertificate);
    }

    // The transient password param + profile options — built the same way the adapter/Connection
    // Manager would (Encrypt Optional + TrustServerCertificate for a dev/local instance).
    private static (SessionOptions Options, TargetEntry Target) BuildSqlAuth(SqlConn c, string? scriptText)
    {
        var options = new SessionOptions(
            c.Server, c.Database, LaunchMode.Script,
            Procedure: null, Args: null, ScriptText: scriptText,
            AuthType: AuthType.Sql, SqlUser: c.User,
            ConnectionOptions: c.TrustCert ? "TrustServerCertificate=True" : null);
        var target = new TargetEntry(c.Server, "test", AllowWrites: false, Options: null);
        return (options, target);
    }

    [SkippableFact]
    public async Task SqlAuthSession_ConnectsAsTheSqlLogin_AndRuns()
    {
        var c = Require();
        var (options, target) = BuildSqlAuth(c, "SELECT SUSER_SNAME() AS who;");

        // Password ONLY as the transient param (never in SessionOptions — see the record above).
        var result = await SessionHost.RunAsync(options, target, password: c.Password);

        var rows = result.Execution.ResultSets.SelectMany(rs => rs.Rows).ToList();
        var who = Assert.Single(rows)[0] as string;
        Assert.Equal(c.User, who);   // connected AS the SQL login, not the Windows account
    }

    [SkippableFact]
    public async Task SqlAuthSession_PasswordNeverAppearsInTrace()
    {
        var c = Require();
        Skip.If(string.IsNullOrEmpty(c.Password), "The SQL-auth connection string has no password to check for.");
        var (options, target) = BuildSqlAuth(c, "SELECT 1 AS one;");

        var trace = new RecordingSink();
        await SessionHost.RunAsync(options, target, trace, password: c.Password);

        Assert.DoesNotContain(trace.Events, e => e.Message.Contains(c.Password, StringComparison.Ordinal));
        // And the connection.open line carries only server/database/nonce (C27 invariant).
        var open = Assert.Single(trace.Events, e => e.Category == "connection.open");
        Assert.DoesNotContain("User ID", open.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", open.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MetadataCli_ListDatabases_SqlAuth_ReturnsDatabases_NeverPrintsSecret()
    {
        var c = Require();
        var args = MetadataArgs("--list-databases", c);

        var output = await RunCliAsync("list-databases", args, c.Password);

        Assert.Contains("\"databases\"", output);
        Assert.Contains(c.Database, output);                       // the target DB is accessible to the login
        Assert.DoesNotContain(c.Password, output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task MetadataCli_TestConnection_SqlAuth_Ok_NeverPrintsSecret()
    {
        var c = Require();
        var args = MetadataArgs("--test-connection", c);

        var output = await RunCliAsync("test-connection", args, c.Password);

        Assert.Contains("\"ok\":true", output);
        Assert.DoesNotContain(c.Password, output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task MetadataCli_TestConnection_SqlAuth_WrongPassword_IsRejected()
    {
        var c = Require();
        Skip.If(string.IsNullOrEmpty(c.Password), "Needs a real password to construct a wrong one.");
        var args = MetadataArgs("--test-connection", c);

        // A deliberately wrong password must fail — proving the login is actually enforced
        // (not silently falling back to integrated auth), and the error never leaks the secret.
        var output = await RunCliAsync("test-connection", args, c.Password + "_wrong");

        Assert.Contains("\"ok\":false", output);
        Assert.DoesNotContain(c.Password, output, StringComparison.Ordinal);
        Assert.DoesNotContain(c.Password + "_wrong", output, StringComparison.Ordinal);
    }

    private static string[] MetadataArgs(string mode, SqlConn c)
    {
        var args = new List<string> { mode, c.Server, "--database", c.Database, "--sql-user", c.User };
        if (c.TrustCert)
        {
            args.Add("--options");
            args.Add("TrustServerCertificate=True");
        }

        return args.ToArray();
    }

    // Invoke the in-process metadata CLI the way the adapter's Program.cs does — password via
    // the TSQLDBG_SQL_PASSWORD env var (never an arg), JSON captured from stdout. Restores
    // Console/env in a finally. Serialized against the other tests in this class via the
    // collection attribute, and no other test touches Console.Out / TSQLDBG_SQL_PASSWORD.
    private static async Task<string> RunCliAsync(string mode, string[] args, string? password)
    {
        var priorOut = Console.Out;
        var priorPw = Environment.GetEnvironmentVariable(PasswordEnvVar);
        var buffer = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(PasswordEnvVar, password);
            Console.SetOut(buffer);
            await AdapterMetadataCli.RunAsync(mode, args);
        }
        finally
        {
            Console.SetOut(priorOut);
            Environment.SetEnvironmentVariable(PasswordEnvVar, priorPw);
        }

        return buffer.ToString();
    }
}

// Serialize the SQL-auth tests: they mutate the process-wide Console.Out and the
// TSQLDBG_SQL_PASSWORD env var, so they must not run concurrently with each other.
[CollectionDefinition("SqlAuthSerial", DisableParallelization = true)]
public sealed class SqlAuthSerialCollection
{
}
