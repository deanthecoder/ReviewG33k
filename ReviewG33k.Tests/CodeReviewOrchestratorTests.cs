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
using ReviewG33k.Models;
using ReviewG33k.Services;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class CodeReviewOrchestratorTests
{
    [Test]
    public async Task PrepareReviewAsyncWhenRepositoryIsClonedSyncsAndUpdatesSubmodules()
    {
        using var tempRoot = new TempDirectory();
        var commands = new List<(string WorkingDirectory, string CommandText)>();
        var orchestrator = new CodeReviewOrchestrator((workingDirectory, _, arguments) =>
        {
            var commandText = string.Join(" ", arguments);
            commands.Add((workingDirectory, commandText));

            if (commandText.StartsWith("clone ", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(arguments[^1]);
                Directory.CreateDirectory(Path.Combine(arguments[^1], ".git"));
            }

            if (commandText.StartsWith("worktree add ", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(arguments[^2]);
                Directory.CreateDirectory(Path.Combine(arguments[^2], ".git"));
            }

            return Task.FromResult(Success(commandText));
        });
        var pullRequest = CreatePullRequest();

        var result = await orchestrator.PrepareReviewAsync(
            tempRoot.FullName,
            pullRequest,
            [],
            _ => { },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(commands.Select(command => command.CommandText), Does.Contain($"clone {pullRequest.CloneUrl} {result.LocalRepositoryPath}"));
            Assert.That(commands.Select(command => command.CommandText), Does.Contain("submodule sync --recursive"));
            Assert.That(commands.Select(command => command.CommandText), Does.Contain("submodule update --init --recursive"));
            Assert.That(commands.Count(command => command.CommandText == "submodule sync --recursive"), Is.EqualTo(2));
            Assert.That(commands.Count(command => command.CommandText == "submodule update --init --recursive"), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task PrepareReviewAsyncWhenExistingWorktreeIsUpToDateStillUpdatesSubmodules()
    {
        using var tempRoot = new TempDirectory();
        var repoDirectory = tempRoot.GetDir("sample-repo");
        repoDirectory.Create();
        repoDirectory.GetDir(".git").Create();

        var reviewFolder = tempRoot.GetDir("CodeReview/sample-repo/PR-42");
        reviewFolder.Create();
        reviewFolder.GetDir(".git").Create();

        var commands = new List<(string WorkingDirectory, string CommandText)>();
        var orchestrator = new CodeReviewOrchestrator((workingDirectory, _, arguments) =>
        {
            var commandText = string.Join(" ", arguments);
            commands.Add((workingDirectory, commandText));

            if (commandText == "config --get remote.origin.url")
                return Task.FromResult(new GitCommandResult(0, "https://bitbucket.example.com/scm/proj/sample-repo.git", string.Empty, commandText));

            if (commandText == "rev-parse --verify HEAD" || commandText == "rev-parse --verify refs/remotes/origin/pr/42")
                return Task.FromResult(new GitCommandResult(0, "abc123", string.Empty, commandText));

            return Task.FromResult(Success(commandText));
        });

        await orchestrator.PrepareReviewAsync(
            tempRoot.FullName,
            CreatePullRequest(),
            [],
            _ => { },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(commands.Any(command => command.CommandText.StartsWith("clone ", StringComparison.Ordinal)), Is.False);
            Assert.That(commands.Any(command => command.CommandText.StartsWith("worktree add ", StringComparison.Ordinal)), Is.False);
            Assert.That(commands.Count(command => command.WorkingDirectory == reviewFolder.FullName && command.CommandText == "submodule sync --recursive"), Is.EqualTo(1));
            Assert.That(commands.Count(command => command.WorkingDirectory == reviewFolder.FullName && command.CommandText == "submodule update --init --recursive"), Is.EqualTo(1));
        });
    }

    private static BitbucketPullRequestReference CreatePullRequest() =>
        new(
            "bitbucket.example.com",
            "PROJ",
            "sample-repo",
            42,
            "https://bitbucket.example.com/projects/PROJ/repos/sample-repo/pull-requests/42");

    private static GitCommandResult Success(string commandText) =>
        new(0, string.Empty, string.Empty, commandText);
}
