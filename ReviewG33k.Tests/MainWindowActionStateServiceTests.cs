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
public sealed class MainWindowActionStateServiceTests
{
    [Test]
    public void ConstructorWhenValidationServiceIsNullThrows()
    {
        Assert.That(() => new MainWindowActionStateService(null), Throws.ArgumentNullException);
    }

    [Test]
    public void BuildSnapshotWhenInputsAreValidIncludesExpectedActionFlags()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var repositoryRoot = Path.Combine(tempRoot, "repo-root");
            var localRepository = Path.Combine(tempRoot, "local-repo");
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(localRepository);
            Directory.CreateDirectory(Path.Combine(localRepository, ".git"));
            var solutionPath = Path.Combine(repositoryRoot, "App.sln");
            File.WriteAllText(solutionPath, "Microsoft Visual Studio Solution File");

            var service = new MainWindowActionStateService(new MainWindowInputValidationService());
            var snapshot = service.BuildSnapshot(
                repositoryRootPath: repositoryRoot,
                localRepositoryPath: localRepository,
                localBaseBranch: string.Empty,
                pullRequestUrl: "https://bitbucket.example.com/projects/PROJ/repos/repo/pull-requests/19",
                isAnyLocalReviewMode: true,
                isLocalUncommittedReviewMode: true,
                previewPullRequestIsOpen: true,
                previewPullRequestState: "OPEN",
                latestSolutionPath: null,
                latestReviewWorktreePath: null,
                canCancelCurrentOperation: true,
                isCancellationRequested: false);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.HasValidPullRequestInput, Is.True);
                Assert.That(snapshot.HasValidPullRequestPrepareInputs, Is.True);
                Assert.That(snapshot.HasValidLocalPrepareInputs, Is.True);
                Assert.That(snapshot.HasAvailableSolution, Is.True);
                Assert.That(snapshot.ResolvedSolutionPath, Is.EqualTo(solutionPath));
                Assert.That(snapshot.CanReviewCurrentPullRequest, Is.True);
                Assert.That(snapshot.CanCancelCurrentOperation, Is.True);
            });
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Test]
    public void BuildSnapshotWhenCommittedModeHasNoBaseBranchMarksLocalInputsInvalid()
    {
        var tempRoot = CreateTempRoot();
        try
        {
            var localRepository = Path.Combine(tempRoot, "local-repo");
            Directory.CreateDirectory(localRepository);
            Directory.CreateDirectory(Path.Combine(localRepository, ".git"));

            var service = new MainWindowActionStateService(new MainWindowInputValidationService());
            var snapshot = service.BuildSnapshot(
                repositoryRootPath: tempRoot,
                localRepositoryPath: localRepository,
                localBaseBranch: " ",
                pullRequestUrl: " ",
                isAnyLocalReviewMode: true,
                isLocalUncommittedReviewMode: false,
                previewPullRequestIsOpen: false,
                previewPullRequestState: "DECLINED",
                latestSolutionPath: null,
                latestReviewWorktreePath: null,
                canCancelCurrentOperation: false,
                isCancellationRequested: false);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.HasValidLocalPrepareInputs, Is.False);
                Assert.That(snapshot.CanReviewCurrentPullRequest, Is.False);
            });
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Test]
    public void BuildSnapshotWhenPullRequestIsMergedKeepsReviewEnabledForFallback()
    {
        var service = new MainWindowActionStateService(new MainWindowInputValidationService());
        var snapshot = service.BuildSnapshot(
            repositoryRootPath: "C:/repo",
            localRepositoryPath: "C:/repo",
            localBaseBranch: "main",
            pullRequestUrl: "https://bitbucket.example.com/projects/PROJ/repos/repo/pull-requests/19",
            isAnyLocalReviewMode: false,
            isLocalUncommittedReviewMode: false,
            previewPullRequestIsOpen: false,
            previewPullRequestState: "MERGED",
            latestSolutionPath: null,
            latestReviewWorktreePath: null,
            canCancelCurrentOperation: false,
            isCancellationRequested: false);

        Assert.That(snapshot.CanReviewCurrentPullRequest, Is.True);
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
            Directory.Delete(tempRoot, recursive: true);
    }
}
