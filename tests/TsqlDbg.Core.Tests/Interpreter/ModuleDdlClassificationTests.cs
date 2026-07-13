using Microsoft.SqlServer.TransactSql.ScriptDom;
using TsqlDbg.Core.Interpreter;
using TsqlDbg.Core.Parsing;
using Xunit;

namespace TsqlDbg.Core.Tests.Interpreter;

// DESIGN §5.4 (A48): module-creating DDL (CREATE/ALTER PROCEDURE/FUNCTION/VIEW/TRIGGER) is
// classified SuSubKind.ModuleDdl so the driver executes it BARE (never the §7.1 oracle TRY,
// which parse-errors a CREATE OR ALTER — msg 156 near 'OR'). Non-module DDL (CREATE TABLE,
// CREATE INDEX) and other leaves must NOT be swept in. Pairs with ModuleDdlLiveTests (which
// proves the bare execution end-to-end against a real server).
public sealed class ModuleDdlClassificationTests
{
    private static ClassificationResult ClassifyFirst(string sql)
    {
        var frag = ScriptParser.Parse(sql, initialQuotedIdentifiers: true, compatLevel: 150, out var errors);
        Assert.Empty(errors);
        var stmt = ((TSqlScript)frag).Batches.SelectMany(b => b.Statements).First();
        return SuClassifier.Classify(stmt);
    }

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("ALTER PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.x AS SELECT 1")]
    [InlineData("CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END")]
    [InlineData("CREATE OR ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END")]
    [InlineData("CREATE VIEW dbo.v AS SELECT 1 AS a")]
    [InlineData("CREATE OR ALTER VIEW dbo.v AS SELECT 1 AS a")]
    [InlineData("CREATE TRIGGER dbo.tr ON dbo.t AFTER INSERT AS SELECT 1")]
    [InlineData("CREATE OR ALTER TRIGGER dbo.tr ON dbo.t AFTER INSERT AS SELECT 1")]
    public void ModuleCreatingDdl_IsClassifiedModuleDdl(string sql)
    {
        var c = ClassifyFirst(sql);
        Assert.Equal(SuKind.Executable, c.Kind);
        Assert.Equal(SuSubKind.ModuleDdl, c.SubKind);
        Assert.Null(c.RequiredMilestone);   // executed whole by the server, never gated
    }

    [Theory]
    [InlineData("CREATE TABLE dbo.t (a int)")]     // real table DDL — not must-be-first module DDL
    [InlineData("CREATE TABLE #t (a int)")]        // temp-table DDL has its own §9 registry subkind
    [InlineData("CREATE INDEX ix ON dbo.t(a)")]
    [InlineData("SELECT 1")]
    [InlineData("EXEC dbo.x")]
    public void NonModuleStatements_AreNotModuleDdl(string sql)
    {
        Assert.NotEqual(SuSubKind.ModuleDdl, ClassifyFirst(sql).SubKind);
    }
}
