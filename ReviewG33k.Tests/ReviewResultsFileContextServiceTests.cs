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

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class ReviewResultsFileContextServiceTests
{
    [Test]
    public void TryBuildPreviewWhenFindingCanBeResolvedBuildsExpectedText()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var filePath = Path.Combine(tempRoot, "src", "Sample.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            File.WriteAllText(filePath, "line1\nline2\nline3\nline4\nline5\n");
            var finding = new CodeSmellFinding(
                CodeReviewFindingSeverity.Important,
                "sample-rule",
                "src/Sample.cs",
                3,
                "Issue");
            var service = new ReviewResultsFileContextService();

            var success = service.TryBuildPreview(
                finding,
                _ => filePath,
                previewLinesBefore: 1,
                previewLinesAfter: 1,
                out var previewData,
                out var failureReason);

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.True);
                Assert.That(failureReason, Is.Null);
                Assert.That(previewData.Header, Is.EqualTo("Preview: Sample.cs"));
                Assert.That(previewData.PreviewFileName, Is.EqualTo("Sample.cs"));
                Assert.That(previewData.Text, Does.Contain("  2: line2"));
                Assert.That(previewData.Text, Does.Contain("> 3: line3"));
                Assert.That(previewData.Text, Does.Contain("  4: line4"));
            });
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Test]
    public void TryBuildPreviewWhenFindingCannotBeResolvedReturnsFailure()
    {
        var service = new ReviewResultsFileContextService();
        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Important,
            "sample-rule",
            "src/Missing.cs",
            1,
            "Issue");

        var success = service.TryBuildPreview(
            finding,
            _ => null,
            previewLinesBefore: 2,
            previewLinesAfter: 2,
            out _,
            out var failureReason);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.False);
            Assert.That(failureReason, Is.EqualTo("Could not resolve file for 'src/Missing.cs'."));
        });
    }

    [Test]
    public void TryBuildCodexPromptWhenRepoCanBeResolvedBuildsPrompt()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var repoRoot = Path.Combine(tempRoot, "Repo");
            var sourcePath = Path.Combine(repoRoot, "src", "Sample.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath));
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
            File.WriteAllText(sourcePath, "alpha\nbeta\ngamma\n");
            var finding = new CodeSmellFinding(
                CodeReviewFindingSeverity.Important,
                "sample-rule",
                "src/Sample.cs",
                2,
                "Fix this.");
            var service = new ReviewResultsFileContextService();

            var success = service.TryBuildCodexPrompt(
                finding,
                _ => sourcePath,
                promptLinesBefore: 1,
                promptLinesAfter: 1,
                out var promptText,
                out var failureReason);

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.True);
                Assert.That(failureReason, Is.Null);
                Assert.That(promptText, Does.Contain($"Repository path: {repoRoot}"));
                Assert.That(promptText, Does.Contain("File: src/Sample.cs:2"));
                Assert.That(promptText, Does.Contain("Issue: Fix this."));
                Assert.That(promptText, Does.Contain("> 2: beta"));
            });
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Test]
    public void TryBuildCodexPromptWhenRepoCannotBeResolvedReturnsFailure()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var sourcePath = Path.Combine(tempRoot, "src", "Sample.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath));
            File.WriteAllText(sourcePath, "alpha\n");
            var finding = new CodeSmellFinding(
                CodeReviewFindingSeverity.Important,
                "sample-rule",
                "src/Sample.cs",
                1,
                "Issue");
            var service = new ReviewResultsFileContextService();

            var success = service.TryBuildCodexPrompt(
                finding,
                _ => sourcePath,
                promptLinesBefore: 1,
                promptLinesAfter: 1,
                out _,
                out var failureReason);

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.False);
                Assert.That(failureReason, Is.EqualTo("Could not detect repository root from the selected file."));
            });
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ReviewG33kPreview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempRoot(string tempRoot)
    {
        if (Directory.Exists(tempRoot))
            Directory.Delete(tempRoot, recursive: true);
    }
}
