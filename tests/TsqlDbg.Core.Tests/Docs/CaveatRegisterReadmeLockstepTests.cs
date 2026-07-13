using System.Text.RegularExpressions;
using Xunit;

namespace TsqlDbg.Core.Tests.Docs;

// M7 hardening (docs/archive/reviews/m7-hardening-design-notes-fable.md §4/D4): DESIGN.md §21's
// own header says "every entry needs a README line" -- the D4 audit found README
// carrying only 5 of the (then) 25 rows. This test parses BOTH documents directly off
// disk and asserts EXACT two-way coverage between the §21 register's row ids and
// README's caveat-bullet ids, so the register and README can never silently drift apart
// again -- the D5/A21 lockstep-pin pattern (a unit test asserting two independently
// maintained artifacts agree with each other), applied to documentation instead of code.
public sealed class CaveatRegisterReadmeLockstepTests
{
    private static readonly Regex RegisterRowId = new(@"^\|\s*(C\d+)\s*\|", RegexOptions.Multiline);
    private static readonly Regex ReadmeBulletId = new(@"^-\s+\*\*(C\d+)\*\*", RegexOptions.Multiline);

    [Fact]
    public void EveryDesignRegisterRow_HasAReadmeLine_AndEveryReadmeLine_IsARealRegisterRow()
    {
        var repoRoot = FindRepoRoot();
        var designPath = Path.Combine(repoRoot, "docs", "DESIGN.md");
        // The caveat-register README mirror lives in docs/README.md (the project/developer
        // reference); the top-level README.md is the user-facing extension copy.
        var readmePath = Path.Combine(repoRoot, "docs", "README.md");

        // docs/ is a maintainer-local, unpublished tree (see .gitignore) -- DESIGN.md and its
        // caveat mirror are not shipped in the public repo. When this test runs from a checkout
        // that doesn't carry docs/ (a fresh clone of the published repo), there is nothing to
        // cross-check, so skip cleanly -- the same way the integration harness skips when
        // TSQLDBG_TEST_CONN is unset, never faking a pass. Whenever docs/ IS present (the
        // maintainer's working tree), the full two-way lockstep below is enforced unchanged.
        if (!File.Exists(designPath) || !File.Exists(readmePath))
        {
            return;
        }

        var designText = File.ReadAllText(designPath);
        var readmeText = File.ReadAllText(readmePath);

        var registerIds = ExtractIds(ExtractSection(designText, "## 21. Caveat register", "## 22."), RegisterRowId);
        var readmeIds = ExtractIds(ExtractSection(readmeText, "## Caveats", "## Development"), ReadmeBulletId);

        // Non-hollow guard (the p04 lesson): a broken section-boundary marker or a regex
        // that silently matches zero rows on BOTH sides would make the set-equality
        // assertions below pass vacuously true. DESIGN.md §21 has 26 rows (C1-C26) as of
        // this pin; assert comfortably below that so a future ADDITION doesn't need this
        // test touched, while a parser regression (near-zero matches) still fails loudly.
        Assert.True(registerIds.Count >= 20,
            $"Expected to parse at least 20 rows out of DESIGN.md's §21 register; got " +
            $"{registerIds.Count}. The section-boundary markers or the row regex may no " +
            "longer match this document -- fix the parser, not this assertion.");
        Assert.True(readmeIds.Count >= 20,
            $"Expected to parse at least 20 caveat ids out of docs/README.md's Caveats " +
            $"section; got {readmeIds.Count}. The section-boundary markers or the " +
            "bullet regex may no longer match this document -- fix the parser, not this assertion.");

        var missingFromReadme = registerIds.Except(readmeIds).OrderBy(IdNumber).ToList();
        var missingFromRegister = readmeIds.Except(registerIds).OrderBy(IdNumber).ToList();

        Assert.True(missingFromReadme.Count == 0,
            "DESIGN.md §21 caveat ids with no corresponding docs/README.md line: " +
            string.Join(", ", missingFromReadme));
        Assert.True(missingFromRegister.Count == 0,
            "docs/README.md caveat ids that are not real DESIGN.md §21 register rows " +
            "(typo, or a stale line left after a register row was removed?): " +
            string.Join(", ", missingFromRegister));

        // Neither list should name the same id twice -- a duplicated row/line would
        // otherwise hide behind the set-based comparisons above.
        AssertNoDuplicates(registerIds, "DESIGN.md §21");
        AssertNoDuplicates(readmeIds, "docs/README.md's Caveats section");
    }

    private static string ExtractSection(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find the section start marker '{startMarker}'.");

        var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find the section end marker '{endMarker}' after '{startMarker}'.");

        return text.Substring(start, end - start);
    }

    private static List<string> ExtractIds(string sectionText, Regex idPattern)
    {
        var ids = new List<string>();
        foreach (Match match in idPattern.Matches(sectionText))
        {
            ids.Add(match.Groups[1].Value);
        }

        return ids;
    }

    private static void AssertNoDuplicates(List<string> ids, string sourceDescription)
    {
        var duplicates = ids
            .GroupBy(id => id)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"{sourceDescription} lists a caveat id more than once: {string.Join(", ", duplicates)}");
    }

    private static int IdNumber(string id) => int.Parse(id.Substring(1));

    // Same walk-up-to-the-checkout-root approach as any test that needs a real repo
    // file rather than a copied test asset -- docs/DESIGN.md and docs/README.md live under
    // the repo root, several directories above this test project's build output.
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TsqlDbg.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (TsqlDbg.sln) walking up from " +
            AppContext.BaseDirectory + " -- this pin reads docs/DESIGN.md and docs/README.md " +
            "directly off disk and needs the checkout present, not just the test binaries.");
    }
}
