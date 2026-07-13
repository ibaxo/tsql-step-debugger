using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Batches;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Rewrite;
using Xunit;

namespace TsqlDbg.Integration;

// M6 §14/A21 builder-level LIVE shape check (Fable lane item 2): one boosted batch,
// composed by the real builder, executed raw against SQL Server. This is NOT fidelity
// pass 3 (Sonnet item 6) — it pins the T-SQL LEGALITY of the composition's sharp
// edges before the session layer exists on top:
//   - the `;;` adjacency where a marker's leading semicolon follows a statement's own;
//   - the prologue's `IF … INSERT …; ELSE UPDATE …;` shape;
//   - a marker with no trailing semicolon flowing into the next original statement;
//   - markers actually firing (final pos = the last body marker) and the postamble's
//     live rc/err capture reading native post-loop values (fact 27's V-invariant,
//     now through the REAL composed text).
public sealed class BoostCompositionLiveTests
{
    private const string ConnEnvVar = "TSQLDBG_TEST_CONN";

    [SkippableFact]
    public async Task BoostedWhileBatch_ExecutesLive_MarkersFire_ControlRowIsNative()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnEnvVar);
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            $"{ConnEnvVar} is not set; skipping live probe (never fake a pass — CLAUDE.md).");

        // Compose exactly as the session will: frame 0, one int variable, a two-
        // statement loop body (assigning SET + INSERT into a real work table).
        var ctx = new RewriteContext("t26a");
        var script = "WHILE @i < 3\nBEGIN\n    SET @i = @i + 1;\n    INSERT #boostwork VALUES (@i);\nEND";
        var parser = new TSql150Parser(initialQuotedIdentifiers: true);   // ScriptParser's default (DESIGN §2)
        var fragment = (TSqlScript)parser.Parse(new StringReader(script), out var errors);
        Assert.Empty(errors);
        var body = fragment.Batches[0].Statements;
        var cursor = ExecutionCursor.Create(body, script);
        var frame = new Frame(0, ModuleIdentity.Script(), cursor, SetOptionEnvironment.Default);
        var declareScript = (TSqlScript)parser.Parse(new StringReader("DECLARE @i int;"), out _);
        var declare = (DeclareVariableStatement)declareScript.Batches[0].Statements[0];
        frame.Variables.Register(new VariableDeclaration("@i", "int", null, declare.Declarations[0]));

        var controlNode = cursor.Index.All[0];
        var markers = BoostSubtreeMarkers.Compute(controlNode.Fragment);
        var batch = ComposedBatchBuilder.BuildForBoostedSubtree(
            frame, RewriteEngine.CreateDefault(), ctx, controlNode, script, ShadowValues.Initial(),
            seq: 7, markers);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // Session-init equivalents: the F1 boost-table seed, the frame state table,
        // and the work table (all per-session #temps).
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                ComposedBatchBuilder.BuildBoostSessionInit() +
                "CREATE TABLE #__dbg_s0([i] int NULL);\n" +
                "INSERT INTO #__dbg_s0 ([i]) VALUES (0);\n" +
                "CREATE TABLE #boostwork(v int);\n" +
                "BEGIN TRANSACTION;";
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = batch.Text;
        int? ok = null, rc = null;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                if (!await reader.ReadAsync())
                {
                    continue;
                }

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.GetName(i) == "__dbg_ctl")
                    {
                        ok = Convert.ToInt32(reader["ok"]);
                        rc = reader["rc"] is DBNull ? null : Convert.ToInt32(reader["rc"]);
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        Assert.Equal(1, ok);        // the batch parsed and ran — `;;`, `; ELSE`, and unterminated markers are all legal
        Assert.Equal(0, rc);        // fact 27 V-invariant through the real text: the final false predicate reset

        await using (var verify = connection.CreateCommand())
        {
            verify.CommandText = """
                SELECT (SELECT seq FROM #__dbg_boost) AS seq,
                       (SELECT pos FROM #__dbg_boost) AS pos,
                       (SELECT COUNT(*) FROM #boostwork) AS rows_written,
                       (SELECT [i] FROM #__dbg_s0) AS state_i;
                """;
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(7, Convert.ToInt32(reader["seq"]));            // prologue reset ran with our seq
            Assert.Equal(1, Convert.ToInt32(reader["pos"]));            // last marker fired = after the body's 2nd child
            Assert.Equal(3, Convert.ToInt32(reader["rows_written"]));   // three native iterations
            Assert.Equal(3, Convert.ToInt32(reader["state_i"]));        // guarded state write persisted the final @i
        }

        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.CommandText = "ROLLBACK;";
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
