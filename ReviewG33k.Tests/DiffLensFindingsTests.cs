// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using ReviewG33k.Services;
using ReviewG33k.Services.DiffLens;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class DiffLensFindingsTests
{
    [Test]
    public void WorktreePathsMapToWorkingRepositoryAndOtherRepositoriesAreExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), "working-repo");
        var scanned = Path.Combine(Path.GetTempPath(), "scan-worktree");
        var report = DiffLensReviewSnapshot.Create(root, "main", scanned,
        [
            Finding(Path.Combine(scanned, "src", "File.cs")),
            Finding("tests/File.cs"),
            Finding(Path.Combine(Path.GetTempPath(), "elsewhere", "File.cs")),
            new CodeSmellFinding(CodeReviewFindingSeverity.Ok, "ok", "ok.cs", 1, "OK")
        ]);
        Assert.That(report.Findings.Select(finding => finding.FilePath), Is.EqualTo(new[] { "src/File.cs", "tests/File.cs" }));
        Assert.That(report.Matches(root + Path.DirectorySeparatorChar, "main"), Is.True);
        Assert.That(report.Matches(scanned, "main"), Is.False);
        Assert.That(report.Matches(root, "other-branch"), Is.False);
    }

    [TestCase(0)]
    [TestCase(3)]
    public void FindingsMapToWorkingLinesAndCollapsedContextWithoutUsingDeletedLineNumbers(int detail)
    {
        var baseline = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i}"));
        var cell = new DiffLensCell(new DiffLensEntry("file.cs", null, null));
        cell.Update(baseline, baseline.Replace("line 25", "replacement"));
        var visible = cell.FindingRow(25, detail);
        Assert.That(cell.Rows(detail)[visible].NewLine, Is.EqualTo(25));
        Assert.That(cell.Rows(detail)[cell.FindingRow(3, detail)].HiddenLines, Is.GreaterThan(0));
        Assert.That(cell.Rows(detail)[cell.FindingRow(48, detail)].HiddenLines, Is.GreaterThan(0));
        Assert.That(cell.FindingRow(200, detail), Is.EqualTo(-1));
        Assert.That(cell.FindingRow(0, detail), Is.EqualTo(-1));
    }

    private static CodeSmellFinding Finding(string path) => new(CodeReviewFindingSeverity.Important, "rule", path, 25, "Message");
}
