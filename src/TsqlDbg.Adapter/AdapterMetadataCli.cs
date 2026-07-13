using System.Text.Json;
using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;

namespace TsqlDbg.Adapter;

// DESIGN §4/§16 (A41, M10): metadata CLI for the Connection Manager. Connect (integrated via
// SSPI, or SQL via the TSQLDBG_SQL_PASSWORD env var — never an arg), run ONE metadata query,
// print JSON to stdout, exit. No debug session, no DAP, no trace. The connection string and
// password are NEVER printed. This is what lets the extension offer the database dropdown +
// Test connection for BOTH auth types (a pure-Node extension can't do integrated/SSPI).
//
//   TsqlDbg.Adapter --list-databases <server> [--database <db>] [--sql-user <u>] [--options <frag>] [--encrypt]
//   TsqlDbg.Adapter --test-connection <server> [ ... same ... ]
//
// Output: {"databases":[...]} / {"ok":true} on success (exit 0); {"ok":false,"error":"..."}
// on failure (exit 1). Auth is SQL when --sql-user is present, else integrated.
public static class AdapterMetadataCli
{
    public static string? DetectMode(string[] args)
    {
        foreach (var a in args)
        {
            if (a == "--list-databases")
            {
                return "list-databases";
            }

            if (a == "--test-connection")
            {
                return "test-connection";
            }
        }

        return null;
    }

    public static async Task<int> RunAsync(string mode, string[] args)
    {
        try
        {
            var server = ArgValue(args, "--list-databases") ?? ArgValue(args, "--test-connection");
            if (string.IsNullOrWhiteSpace(server))
            {
                return Fail("no server specified");
            }

            var sqlUser = ArgValue(args, "--sql-user");
            var authType = sqlUser is null ? AuthType.Integrated : AuthType.Sql;
            var password = authType == AuthType.Sql
                ? Environment.GetEnvironmentVariable("TSQLDBG_SQL_PASSWORD")
                : null;

            var options = new SessionOptions(
                Server: server,
                Database: ArgValue(args, "--database") ?? "master",
                Mode: LaunchMode.Script,
                Procedure: null,
                Args: null,
                ScriptText: null,
                AuthType: authType,
                SqlUser: sqlUser,
                Encrypt: HasFlag(args, "--encrypt"),
                ConnectionOptions: ArgValue(args, "--options"));
            var target = new TargetEntry(server, "unknown", AllowWrites: false, Options: null);
            var connectionString = SqlConnectionStringFactory.Build(
                options, target, SqlConnectionStringFactory.NewNonce(), password);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            if (mode == "test-connection")
            {
                await using var probe = connection.CreateCommand();
                probe.CommandText = "SELECT 1;";
                await probe.ExecuteScalarAsync().ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true }));
                return 0;
            }

            var names = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                // Accessible, online databases only (HAS_DBACCESS excludes 0/NULL).
                command.CommandText =
                    "SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 ORDER BY name;";
                await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    names.Add(reader.GetString(0));
                }
            }

            Console.WriteLine(JsonSerializer.Serialize(new { databases = names }));
            return 0;
        }
        catch (Exception ex)
        {
            // Only the message — never the connection string or password.
            return Fail(ex.Message);
        }
    }

    private static int Fail(string error)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error }));
        return 1;
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
}
