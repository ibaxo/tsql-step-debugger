using System.Reflection;
using TsqlDbg.Core.Sessions;

namespace TsqlDbg.Mcp.Tests;

// M11 gate (CRITICAL, DESIGN §24.1(1)): the agent surface MUST NOT be able to supply a raw
// connection-string fragment or a connection-level flag. SqlConnectionStringFactory appends
// such fragments LAST — after the typed, allowlisted DataSource — so an agent value like
// "Data Source=EVILSQL" would override the allowlisted server, defeating default-deny AND
// redirecting the TSQLDBG_SQL_PASSWORD to an attacker-chosen host. These tests pin that the
// override surface is gone: SessionArgs carries no such input, and every SessionOptions it
// produces leaves the connection-string overrides at their inert Core defaults.
public sealed class ConnectionOverrideGuardTests
{
    [Fact]
    public void SessionArgs_DoesNotExposeRawConnectionStringOverrides()
    {
        var propertyNames = typeof(SessionArgs)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A raw fragment or a connection-level flag as an agent argument is the CRITICAL vector.
        Assert.DoesNotContain("ConnectionOptions", propertyNames);
        Assert.DoesNotContain("Encrypt", propertyNames);
    }

    [Fact]
    public void ToSessionOptions_LeavesConnectionOverridesAtInertDefaults()
    {
        var options = new SessionArgs("ALLOWEDSRV", "DB", Script: "SELECT 1;").ToSessionOptions();

        // No agent-supplied raw fragment reaches the connection string, and encryption is not
        // forced on from the agent surface — a target's own targets.json `options` (operator-
        // controlled, trusted) is the only place those come from.
        Assert.Null(options.ConnectionOptions);
        Assert.False(options.Encrypt);

        // The server the connection targets is exactly the allowlisted one — nothing overrides it.
        Assert.Equal("ALLOWEDSRV", options.Server);
    }
}
