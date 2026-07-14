using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Xunit;

namespace TsqlDbg.Integration;

// A59 over the REAL DAP wire. The fidelity harness (RunToEnd) can see none of this: it never
// opens a Variables pane, never sets a value, never browses a temp object — the A44/A45/A56
// lesson, and user-defined types land squarely in that blind spot, because a UDT variable is
// something the user LOOKS at. These tests drive the adapter over stdio exactly as VS Code
// does and assert what the panes actually show.
public sealed class UserTypeAdapterLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    private const string Setup = @"
IF TYPE_ID('dbo.ad_Name') IS NULL EXEC('CREATE TYPE dbo.ad_Name FROM nvarchar(50) NOT NULL');
IF TYPE_ID('dbo.ad_Rows') IS NULL EXEC('CREATE TYPE dbo.ad_Rows AS TABLE (id int IDENTITY(1,1), nm nvarchar(30) NOT NULL)');
IF TYPE_ID('dbo.ad_Sql') IS NULL EXEC('CREATE TYPE dbo.ad_Sql FROM nvarchar(200)');
";

    // The Locals pane must show an alias-typed variable, at its DECLARED type name — the user
    // wrote dbo.ad_Name, so that is what they must see, not the base type the debugger stores
    // it as (§8.1's split is an implementation detail and must stay one).
    // And Set Value must WORK on it: before A59 the §8.3 safe-literal-form test read the
    // declared type, so 'dbo.ad_Name' failed the whitelist and every alias variable was
    // read-only — a refusal the fidelity harness could never have seen.
    [SkippableFact]
    public async Task LocalsPane_ShowsAliasVariableAtItsDeclaredType_AndSetValueWorks()
    {
        var (csb, _) = Require();
        await ExecuteAsync(csb.ConnectionString, Setup);

        await using var dap = await LaunchScriptAsync(csb, "usertype-alias",
            "DECLARE @Name dbo.ad_Name = N'Ada';\nSET @Name = @Name + N'!';\nSELECT @Name;\n");

        await StepAsync(dap);                                   // DECLARE
        var locals = await LocalsAsync(dap);
        var name = Assert.Single(locals, v => v!["name"]!.GetValue<string>() == "@Name")!;

        Assert.Equal("Ada", name["value"]!.GetValue<string>());
        Assert.Equal("dbo.ad_Name", name["type"]!.GetValue<string>());   // the DECLARED type

        // Set Value on an alias-typed local (§8.3 now tests the STORAGE type's literal form).
        var set = await dap.SendRequestAsync("setVariable", new JsonObject
        {
            ["variablesReference"] = await LocalsScopeReferenceAsync(dap),
            ["name"] = "@Name",
            ["value"] = "N'Grace'",
        });
        Assert.Equal("Grace", set["body"]!["value"]!.GetValue<string>());

        await StepAsync(dap);                                   // SET @Name = @Name + N'!'
        var after = await LocalsAsync(dap);
        var edited = Assert.Single(after, v => v!["name"]!.GetValue<string>() == "@Name")!;
        Assert.Equal("Grace!", edited["value"]!.GetValue<string>());     // the edit took effect

        await dap.DisposeAsync();
    }

    // A table-type variable is not a scalar, so it must NOT appear in Locals (§8.2) — it is a
    // table, and it appears under Temp Tables with its rows browsable, exactly like a
    // `DECLARE @t TABLE(…)` variable (§9/§12.2).
    [SkippableFact]
    public async Task TempTablesScope_ShowsTableTypeVariable_AndLocalsDoesNot()
    {
        var (csb, _) = Require();
        await ExecuteAsync(csb.ConnectionString, Setup);

        await using var dap = await LaunchScriptAsync(csb, "usertype-table",
            "DECLARE @t dbo.ad_Rows;\nINSERT INTO @t (nm) VALUES (N'a'), (N'b');\nSELECT COUNT(*) FROM @t;\n");

        await StepAsync(dap);                                   // DECLARE @t (a no-op stop)
        await StepAsync(dap);                                   // INSERT: two rows

        var locals = await LocalsAsync(dap);
        Assert.DoesNotContain(locals, v => v!["name"]!.GetValue<string>() == "@t");

        var scopes = await ScopesAsync(dap);
        var tempScope = Assert.Single(scopes, s => s!["name"]!.GetValue<string>().Contains("Temp", StringComparison.OrdinalIgnoreCase))!;
        var temps = await VariablesAsync(dap, tempScope["variablesReference"]!.GetValue<int>());

        var t = Assert.Single(temps, v => v!["name"]!.GetValue<string>() == "@t")!;
        Assert.Contains("2", t["value"]!.GetValue<string>());   // 2 rows, browsable
        Assert.True(t["variablesReference"]!.GetValue<int>() > 0);

        await dap.DisposeAsync();
    }

    // A59 review F7: A58's step-into gate demands a provably-nvarchar dynamic-SQL text, and it
    // read the DECLARED type — so `dbo.ad_Sql` (an alias OF nvarchar(200)) failed the test and
    // step-into silently degraded to step-over. Safe, but wrong, and invisible to fidelity: a
    // stepped-OVER sp_executesql produces the identical result set. Only the stack shows it.
    [SkippableFact]
    public async Task StepInto_DynamicSqlHeldInAnAliasTypedVariable_PushesTheDynamicFrame()
    {
        var (csb, _) = Require();
        await ExecuteAsync(csb.ConnectionString, Setup);

        await using var dap = await LaunchScriptAsync(csb, "usertype-dynsql",
            "DECLARE @sql dbo.ad_Sql = N'SELECT 42 AS Answer;';\nEXEC sp_executesql @sql;\n");

        await StepAsync(dap);                                   // DECLARE @sql

        await dap.SendRequestAsync("stepIn", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("stopped");

        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var stack = frames["body"]!["stackFrames"]!.AsArray();

        Assert.True(stack.Count >= 2, "step-into did not push a dynamic frame (A58) — it stepped over.");

        await dap.DisposeAsync();
    }

    private static (SqlConnectionStringBuilder Csb, string Raw) Require()
    {
        var raw = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(raw),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");
        return (new SqlConnectionStringBuilder(raw), raw!);
    }

    private static async Task<DapStdioHarness> LaunchScriptAsync(
        SqlConnectionStringBuilder csb, string name, string scriptText)
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"a59-{name}.jsonl");
        if (File.Exists(tracePath))
        {
            File.Delete(tracePath);
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"a59-{name}.sql");
        await File.WriteAllTextAsync(scriptPath, scriptText);

        var dap = DapStdioHarness.Launch(tracePath);
        await dap.SendRequestAsync("initialize", new JsonObject { ["clientID"] = name, ["adapterID"] = "tsqldbg" });
        await dap.SendRequestAsync("launch", new JsonObject
        {
            ["server"] = csb.DataSource,
            ["database"] = csb.InitialCatalog,
            ["mode"] = "script",
            ["script"] = scriptPath,
            ["targetsFile"] = Path.Combine(AppContext.BaseDirectory, "targets.json"),
            ["stopOnEntry"] = true,
        });
        await dap.WaitForEventAsync("initialized");
        await dap.SendRequestAsync("configurationDone", new JsonObject());
        await dap.WaitForEventAsync("stopped", e => e["body"]!["reason"]!.GetValue<string>() == "entry");
        return dap;
    }

    private static async Task StepAsync(DapStdioHarness dap)
    {
        await dap.SendRequestAsync("next", new JsonObject { ["threadId"] = 1 });
        await dap.WaitForEventAsync("stopped");
    }

    private static async Task<JsonArray> ScopesAsync(DapStdioHarness dap)
    {
        var frames = await dap.SendRequestAsync("stackTrace", new JsonObject { ["threadId"] = 1 });
        var frameId = frames["body"]!["stackFrames"]!.AsArray()[0]!["id"]!.GetValue<int>();
        var scopes = await dap.SendRequestAsync("scopes", new JsonObject { ["frameId"] = frameId });
        return scopes["body"]!["scopes"]!.AsArray();
    }

    private static async Task<int> LocalsScopeReferenceAsync(DapStdioHarness dap)
    {
        var scopes = await ScopesAsync(dap);
        var locals = Assert.Single(scopes, s => s!["name"]!.GetValue<string>().Contains("Local", StringComparison.OrdinalIgnoreCase))!;
        return locals["variablesReference"]!.GetValue<int>();
    }

    private static async Task<JsonArray> LocalsAsync(DapStdioHarness dap)
        => await VariablesAsync(dap, await LocalsScopeReferenceAsync(dap));

    private static async Task<JsonArray> VariablesAsync(DapStdioHarness dap, int variablesReference)
    {
        var variables = await dap.SendRequestAsync("variables", new JsonObject
        {
            ["variablesReference"] = variablesReference,
        });
        return variables["body"]!["variables"]!.AsArray();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
