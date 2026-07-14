using Microsoft.Data.SqlClient;

namespace TsqlDbg.Integration;

/// <summary>
/// Deploys a §20.4 corpus fixture. Most fixtures are a single `CREATE OR ALTER PROCEDURE`
/// batch and are deployed inline by their own test; the A59 fixtures are the first that
/// MUST span batches — `CREATE TYPE` and `CREATE PROCEDURE` cannot share one (a module DDL
/// has to be first in its batch, A48/fact 22), and a type has to exist before the procedure
/// that declares it compiles. `GO` is a client-tool separator, not T-SQL (fact 32e), so the
/// splitting is done here, exactly as sqlcmd would.
/// </summary>
internal static class CorpusDeployer
{
    public static async Task DeployAsync(string connectionString, string fixtureFileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "corpus", fixtureFileName);
        await DeployScriptAsync(connectionString, await File.ReadAllTextAsync(path));
    }

    /// <summary>The same GO-splitting deployment for a test's own inline setup script — a
    /// live test whose fixtures span batches (type, then the function/procedure declaring it)
    /// needs exactly this and nothing more.</summary>
    public static async Task DeployScriptAsync(string connectionString, string script)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var batch in SplitOnGo(script))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> SplitOnGo(string script)
    {
        var batch = new List<string>();
        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (batch.Count > 0)
                {
                    yield return string.Join('\n', batch);
                    batch.Clear();
                }

                continue;
            }

            batch.Add(line);
        }

        if (batch.Any(l => !string.IsNullOrWhiteSpace(l)))
        {
            yield return string.Join('\n', batch);
        }
    }
}
