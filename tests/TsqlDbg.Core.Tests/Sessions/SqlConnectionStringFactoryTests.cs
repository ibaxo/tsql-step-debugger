using Microsoft.Data.SqlClient;
using TsqlDbg.Core.Sessions;
using TsqlDbg.Core.Targets;

namespace TsqlDbg.Core.Tests.Sessions;

// DESIGN §4: exact connection-string ingredients.
public sealed class SqlConnectionStringFactoryTests
{
    private static SessionOptions Options() => new(
        Server: "DEVSQL01",
        Database: "SalesDb",
        Mode: LaunchMode.Script,
        Procedure: null,
        Args: null,
        ScriptText: "SELECT 1;");

    [Fact]
    public void Build_IncludesCoreIngredients()
    {
        var target = new TargetEntry("DEVSQL01", "dev", true, null);

        var connectionString = SqlConnectionStringFactory.Build(Options(), target, "abcd1234");
        var parsed = new SqlConnectionStringBuilder(connectionString);

        Assert.Equal("DEVSQL01", parsed.DataSource);
        Assert.Equal("SalesDb", parsed.InitialCatalog);
        Assert.True(parsed.IntegratedSecurity);
        Assert.Equal("T-SQL Step Debugger [abcd1234]", parsed.ApplicationName);
        Assert.False(parsed.MultipleActiveResultSets);
        Assert.Equal(SqlConnectionEncryptOption.Optional, parsed.Encrypt);
    }

    [Fact]
    public void Build_AppendsTargetOptionsFragment()
    {
        var target = new TargetEntry("DEVSQL01", "dev", true, "TrustServerCertificate=True");

        var connectionString = SqlConnectionStringFactory.Build(Options(), target, "abcd1234");

        Assert.Contains("TrustServerCertificate=True", connectionString);
    }

    [Fact]
    public void Build_NoOptionsFragment_DoesNotAppendTrailingSemicolon()
    {
        var target = new TargetEntry("DEVSQL01", "dev", true, null);

        var connectionString = SqlConnectionStringFactory.Build(Options(), target, "abcd1234");

        Assert.False(connectionString.TrimEnd().EndsWith(";;"));
    }

    [Fact]
    public void NewNonce_Is8Characters()
    {
        var nonce = SqlConnectionStringFactory.NewNonce();
        Assert.Equal(8, nonce.Length);
    }

    // DESIGN §4/§16 (A41): SQL auth — UserID + Password, Integrated Security off. The
    // password is a transient parameter (the adapter's env channel), never in SessionOptions.
    [Fact]
    public void Build_SqlAuth_SetsUserAndPassword_IntegratedSecurityOff()
    {
        var target = new TargetEntry("DEVSQL01", "dev", false, null);
        var options = Options() with { AuthType = AuthType.Sql, SqlUser = "appuser" };

        var connectionString = SqlConnectionStringFactory.Build(options, target, "abcd1234", "s3cr3t!");
        var parsed = new SqlConnectionStringBuilder(connectionString);

        Assert.False(parsed.IntegratedSecurity);
        Assert.Equal("appuser", parsed.UserID);
        Assert.Equal("s3cr3t!", parsed.Password);
    }

    [Fact]
    public void Build_Integrated_HasNoSqlCredentials()
    {
        var target = new TargetEntry("DEVSQL01", "dev", true, null);

        var parsed = new SqlConnectionStringBuilder(SqlConnectionStringFactory.Build(Options(), target, "abcd1234"));

        Assert.True(parsed.IntegratedSecurity);
        Assert.Equal(string.Empty, parsed.UserID);
        Assert.Equal(string.Empty, parsed.Password);
    }

    [Fact]
    public void Build_EncryptTrue_SetsMandatory()
    {
        var target = new TargetEntry("DEVSQL01", "dev", true, null);
        var options = Options() with { Encrypt = true };

        var parsed = new SqlConnectionStringBuilder(SqlConnectionStringFactory.Build(options, target, "abcd1234"));

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, parsed.Encrypt);
    }

    [Fact]
    public void Build_AppendsConnectionOptionsFragment()
    {
        var target = new TargetEntry("DEVSQL01", "dev", true, null);
        var options = Options() with { ConnectionOptions = "TrustServerCertificate=True" };

        var connectionString = SqlConnectionStringFactory.Build(options, target, "abcd1234");

        Assert.Contains("TrustServerCertificate=True", connectionString);
    }
}
