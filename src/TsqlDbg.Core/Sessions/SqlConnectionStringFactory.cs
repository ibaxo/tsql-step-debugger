using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Targets;

namespace TsqlDbg.Core.Sessions;

// DESIGN §4 (A41): "Server, Database, integrated (SSPI) by default or SQL auth (UserID +
// Password, the password supplied out-of-band via the adapter's TSQLDBG_SQL_PASSWORD env),
// Application Name=T-SQL Step Debugger [{8-char nonce}], Encrypt default Optional (Microsoft.Data.SqlClient
// defaults to Mandatory since v4 — override; profile encrypt:true forces Mandatory), plus the
// target's + profile's raw options fragments (e.g. TrustServerCertificate=True).
// MultipleActiveResultSets=False." The assembled string (which may contain a password) is
// NEVER traced (§4).
// Split out from Session so the string it produces can be asserted on without opening
// a real connection.
public static class SqlConnectionStringFactory
{
    public static string Build(SessionOptions options, TargetEntry target, string nonce, string? password = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = options.Server,
            InitialCatalog = options.Database,
            // Integrated (SSPI) by default; SQL auth adds UserID/Password below (A41). Set
            // here (not after) so the integrated-path string is byte-identical to before.
            IntegratedSecurity = options.AuthType != AuthType.Sql,
            // A human-readable program_name so the session is recognizable in sp_who2 /
            // sys.dm_exec_sessions when diagnosing locks; the nonce keeps each session
            // distinguishable (which debug session holds the lock).
            ApplicationName = $"T-SQL Step Debugger [{nonce}]",
            Encrypt = options.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            MultipleActiveResultSets = false,
        };

        if (options.AuthType == AuthType.Sql)
        {
            // Password arrives out-of-band (adapter TSQLDBG_SQL_PASSWORD env, §16) — never
            // from SessionOptions. The assembled string is never traced (§4).
            builder.UserID = options.SqlUser ?? string.Empty;
            builder.Password = password ?? string.Empty;
        }

        var connectionString = builder.ConnectionString;

        // Raw fragments appended last so they can override the typed defaults: the target's
        // options (targets.json) then the launch/profile ConnectionOptions (e.g.
        // "TrustServerCertificate=True"). Order: target first, profile last.
        foreach (var fragment in new[] { target.Options, options.ConnectionOptions })
        {
            if (!string.IsNullOrWhiteSpace(fragment))
            {
                connectionString += ";" + fragment;
            }
        }

        return connectionString;
    }

    // DESIGN §4: the nonce shows in "Application Name=T-SQL Step Debugger [{8-char nonce}]" and
    // also namespaces every __dbg identifier once the composed-batch pipeline exists (CLAUDE.md
    // working rule 4: "All __dbg identifiers carry the session nonce").
    public static string NewNonce()
    {
        return Guid.NewGuid().ToString("N")[..8];
    }
}
