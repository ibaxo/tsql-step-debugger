using TsqlDbg.Core.Sessions;
using Xunit;

namespace TsqlDbg.Core.Tests.Sessions;

// DESIGN §5.2 (M7 sourceMap hash-compare). Each test gets its own throwaway temp
// directory tree (created + torn down per test) since ExpandGlob is real file I/O.
public sealed class SourceMapResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsqldbg-sourcemap-tests-" + Guid.NewGuid().ToString("N"));

    public SourceMapResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "procs"));
        Directory.CreateDirectory(Path.Combine(_root, "procs", "nested"));
        File.WriteAllText(Path.Combine(_root, "procs", "top.sql"), "top");
        File.WriteAllText(Path.Combine(_root, "procs", "other.txt"), "not sql");
        File.WriteAllText(Path.Combine(_root, "procs", "nested", "deep.sql"), "deep");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ExactFile_NoWildcard_Exists_ReturnsIt()
    {
        var path = Path.Combine(_root, "procs", "top.sql");
        Assert.Equal(new[] { path }, SourceMapResolver.ExpandGlob(path));
    }

    [Fact]
    public void ExactFile_NoWildcard_Missing_ReturnsEmpty()
    {
        var path = Path.Combine(_root, "procs", "missing.sql");
        Assert.Empty(SourceMapResolver.ExpandGlob(path));
    }

    [Fact]
    public void SingleStar_NonRecursive_MatchesTopDirectoryOnly()
    {
        var pattern = Path.Combine(_root, "procs", "*.sql");
        var matches = SourceMapResolver.ExpandGlob(pattern);

        Assert.Single(matches);
        Assert.Equal(Path.Combine(_root, "procs", "top.sql"), matches[0]);
    }

    [Fact]
    public void DoubleStar_Recursive_MatchesNestedFilesToo()
    {
        var pattern = Path.Combine(_root, "procs", "**", "*.sql");
        var matches = SourceMapResolver.ExpandGlob(pattern);

        Assert.Equal(2, matches.Count);
        Assert.Contains(Path.Combine(_root, "procs", "top.sql"), matches);
        Assert.Contains(Path.Combine(_root, "procs", "nested", "deep.sql"), matches);
    }

    [Fact]
    public void NonExistentBaseDirectory_ReturnsEmpty_NotAnException()
    {
        var pattern = Path.Combine(_root, "no-such-dir", "*.sql");
        Assert.Empty(SourceMapResolver.ExpandGlob(pattern));
    }

    [Fact]
    public void BlankPattern_ReturnsEmpty()
    {
        Assert.Empty(SourceMapResolver.ExpandGlob(""));
        Assert.Empty(SourceMapResolver.ExpandGlob("   "));
    }
}
