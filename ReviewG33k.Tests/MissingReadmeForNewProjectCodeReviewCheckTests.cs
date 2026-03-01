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
using ReviewG33k.Services.Checks;

namespace ReviewG33k.Tests;

public sealed class MissingReadmeForNewProjectCodeReviewCheckTests
{
    [Test]
    public void AnalyzeWhenAddedProjectHasNoReadmeReportsHint()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var projectFullPath = Path.Combine(tempRoot, "ProjectA", "ProjectA.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(projectFullPath));
            File.WriteAllText(projectFullPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var report = AnalyzeSingleFile("A", "ProjectA/ProjectA.csproj", projectFullPath);

            Assert.That(report.Findings, Has.Count.EqualTo(1));
            Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Hint));
            Assert.That(report.Findings[0].RuleId, Is.EqualTo(CodeReviewRuleIds.MissingReadmeForNewProject));
            Assert.That(report.Findings[0].Message, Does.Contain("README.md"));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Test]
    public void AnalyzeWhenAddedProjectFolderAlreadyHasReadmeDoesNotReport()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var projectDirectory = Path.Combine(tempRoot, "ProjectB");
            var projectFullPath = Path.Combine(projectDirectory, "ProjectB.csproj");

            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(projectFullPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(projectDirectory, "README.md"), "# Project B");

            var report = AnalyzeSingleFile("A", "ProjectB/ProjectB.csproj", projectFullPath);

            Assert.That(report.Findings, Is.Empty);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Test]
    public void AnalyzeWhenProjectFileIsNotNewDoesNotReport()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var projectFullPath = Path.Combine(tempRoot, "ProjectC", "ProjectC.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(projectFullPath));
            File.WriteAllText(projectFullPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var report = AnalyzeSingleFile("M", "ProjectC/ProjectC.csproj", projectFullPath);

            Assert.That(report.Findings, Is.Empty);
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private static CodeSmellReport AnalyzeSingleFile(string status, string path, string fullPath)
    {
        var source = File.ReadAllText(fullPath);
        var normalizedSource = source.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedSource.Split('\n');

        var changedFile = new CodeReviewChangedFile(
            status,
            path,
            fullPath,
            normalizedSource,
            lines,
            new HashSet<int>(Enumerable.Range(1, lines.Length)));

        var context = new CodeReviewAnalysisContext(
            [changedFile],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var report = new CodeSmellReport();
        var check = new MissingReadmeForNewProjectCodeReviewCheck();
        check.Analyze(context, report);
        return report;
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ReviewG33kTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempRoot(string tempRoot)
    {
        if (Directory.Exists(tempRoot))
            Directory.Delete(tempRoot, true);
    }
}
