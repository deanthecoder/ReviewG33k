// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using DTC.Core;
using DTC.Core.Extensions;
using ReviewG33k.Services;

namespace ReviewG33k.Tests;

public sealed class GitWhitespaceInsensitiveDiffTests
{
    [Test]
    public async Task GitWorkingTreeChangedFileSourceWhenOnlyLineEndingsDifferIncludesFile()
    {
        using var tempRoot = new TempDirectory();
        var git = new GitCommandRunner();
        var sourceFile = await CreateRepositoryWithTrackedSourceFileAsync(tempRoot, git);

        File.WriteAllText(sourceFile.FullName, CreateCrLfSourceWithTrailingWhitespace());

        var source = new GitWorkingTreeChangedFileSource(git, tempRoot.FullName);
        var result = await source.LoadAsync();

        Assert.That(result.Files, Has.Count.EqualTo(1));
        Assert.That(result.Files[0].Path, Is.EqualTo("src/App/Worker.cs"));
        Assert.That(result.Files[0].BaselineText, Is.Not.Null);
    }

    [Test]
    public async Task GitBranchComparisonChangedFileSourceWhenOnlyLineEndingsDifferIncludesFile()
    {
        using var tempRoot = new TempDirectory();
        var git = new GitCommandRunner();
        var sourceFile = await CreateRepositoryWithTrackedSourceFileAsync(tempRoot, git);

        Assert.That((await git.RunAsync(tempRoot.FullName, "checkout", "-b", "feature/whitespace")).IsSuccess, Is.True);
        File.WriteAllText(sourceFile.FullName, CreateCrLfSourceWithTrailingWhitespace());
        Assert.That((await git.RunAsync(tempRoot.FullName, "add", ".")).IsSuccess, Is.True);
        Assert.That((await git.RunAsync(tempRoot.FullName, "commit", "-m", "Whitespace only")).IsSuccess, Is.True);

        var source = new GitBranchComparisonChangedFileSource(git, tempRoot.FullName, "main", fetchTargetBranch: false);
        var result = await source.LoadAsync();

        Assert.That(result.Files, Has.Count.EqualTo(1));
        Assert.That(result.Files[0].Path, Is.EqualTo("src/App/Worker.cs"));
        Assert.That(result.Files[0].BaselineText, Is.Not.Null);
    }

    [Test]
    public async Task GitSingleCommitChangedFileSourceWhenOnlyLineEndingsDifferIncludesFile()
    {
        using var tempRoot = new TempDirectory();
        var git = new GitCommandRunner();
        var sourceFile = await CreateRepositoryWithTrackedSourceFileAsync(tempRoot, git);

        File.WriteAllText(sourceFile.FullName, CreateCrLfSourceWithTrailingWhitespace());
        Assert.That((await git.RunAsync(tempRoot.FullName, "add", ".")).IsSuccess, Is.True);
        Assert.That((await git.RunAsync(tempRoot.FullName, "commit", "-m", "Whitespace only")).IsSuccess, Is.True);
        var headResult = await git.RunAsync(tempRoot.FullName, "rev-parse", "HEAD");
        Assert.That(headResult.IsSuccess, Is.True);

        var source = new GitSingleCommitChangedFileSource(git, tempRoot.FullName, headResult.StandardOutput.Trim());
        var result = await source.LoadAsync();

        Assert.That(result.Files, Has.Count.EqualTo(1));
        Assert.That(result.Files[0].Path, Is.EqualTo("src/App/Worker.cs"));
        Assert.That(result.Files[0].BaselineText, Is.Not.Null);
    }

    private static async Task<FileInfo> CreateRepositoryWithTrackedSourceFileAsync(TempDirectory tempRoot, GitCommandRunner git)
    {
        tempRoot.GetDir("src/App").Create();
        var sourceFile = tempRoot.GetFile("src/App/Worker.cs");

        Assert.That((await git.RunAsync(tempRoot.FullName, "init")).IsSuccess, Is.True);
        Assert.That((await git.RunAsync(tempRoot.FullName, "checkout", "-b", "main")).IsSuccess, Is.True);
        Assert.That((await git.RunAsync(tempRoot.FullName, "config", "user.email", "reviewg33k@example.com")).IsSuccess, Is.True);
        Assert.That((await git.RunAsync(tempRoot.FullName, "config", "user.name", "ReviewG33k Tests")).IsSuccess, Is.True);
        Assert.That((await git.RunAsync(tempRoot.FullName, "config", "core.autocrlf", "false")).IsSuccess, Is.True);

        File.WriteAllText(sourceFile.FullName, CreateLfSource());
        Assert.That((await git.RunAsync(tempRoot.FullName, "add", ".")).IsSuccess, Is.True);
        Assert.That((await git.RunAsync(tempRoot.FullName, "commit", "-m", "Initial")).IsSuccess, Is.True);

        return sourceFile;
    }

    private static string CreateLfSource() =>
        "public sealed class Worker\n" +
        "{\n" +
        "    public void Run()\n" +
        "    {\n" +
        "    }\n" +
        "}\n";

    private static string CreateCrLfSourceWithTrailingWhitespace() =>
        "public sealed class Worker  \r\n" +
        "{\r\n" +
        "    public void Run()\r\n" +
        "    {\r\n" +
        "    }\r\n" +
        "}\r\n";
}
