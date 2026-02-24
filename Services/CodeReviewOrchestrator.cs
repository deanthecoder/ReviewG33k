// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ReviewG33k.Models;

namespace ReviewG33k.Services;

public sealed class CodeReviewOrchestrator
{
    private const string CodeReviewFolderName = "CodeReview";
    private const string CodeReviewMarkerFileName = ".reviewg33k";
    private readonly GitCommandRunner m_gitCommandRunner;

    public CodeReviewOrchestrator(GitCommandRunner gitCommandRunner)
    {
        m_gitCommandRunner = gitCommandRunner ?? throw new ArgumentNullException(nameof(gitCommandRunner));
    }

    public async Task<PrepareReviewResult> PrepareReviewAsync(
        string repositoryRoot,
        BitbucketPullRequestReference pullRequest,
        IReadOnlyCollection<string> changedPaths,
        Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            throw new InvalidOperationException("Repository root folder is required.");

        if (!Directory.Exists(repositoryRoot))
            throw new DirectoryNotFoundException($"Repository root folder does not exist: {repositoryRoot}");

        var localRepository = await FindOrCloneRepositoryAsync(repositoryRoot, pullRequest, log);

        var reviewRef = $"refs/remotes/origin/pr/{pullRequest.PullRequestId}";
        var reviewBranch = $"review/pr-{pullRequest.PullRequestId}";
        var fetchSpec = $"+refs/pull-requests/{pullRequest.PullRequestId}/from:{reviewRef}";

        log($"Fetching PR #{pullRequest.PullRequestId} from origin...");
        var fetchResult = await m_gitCommandRunner.RunAsync(localRepository, "fetch", "--prune", "origin", fetchSpec);
        EnsureSuccess(fetchResult, "Failed to fetch pull request from origin.");

        EnsureCodeReviewMarker(repositoryRoot);

        var reviewFolder = Path.Combine(repositoryRoot, CodeReviewFolderName, pullRequest.RepoSlug, $"PR-{pullRequest.PullRequestId}");
        await RemoveWorktreeIfPresentAsync(localRepository, reviewFolder, log);

        Directory.CreateDirectory(Path.GetDirectoryName(reviewFolder));

        log($"Creating worktree copy at '{reviewFolder}' on local branch '{reviewBranch}'...");
        var addResult = await m_gitCommandRunner.RunAsync(localRepository, "worktree", "add", "--force", "-B", reviewBranch, reviewFolder, reviewRef);
        EnsureSuccess(addResult, "Failed to create review worktree.");

        var solutionPath = FindBestSolutionFile(reviewFolder, changedPaths);

        return new PrepareReviewResult(localRepository, reviewFolder, solutionPath);
    }

    public async Task ClearCodeReviewFolderAsync(string repositoryRoot, Action<string> log, bool logWhenMissing = true)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            throw new DirectoryNotFoundException("Repository root folder does not exist.");

        var codeReviewRoot = GetCodeReviewRoot(repositoryRoot);
        if (!Directory.Exists(codeReviewRoot))
        {
            if (logWhenMissing)
                log("CodeReview folder does not exist, nothing to clear.");
            return;
        }

        if (!HasCodeReviewMarker(codeReviewRoot))
        {
            log($"Safety check: missing '{CodeReviewMarkerFileName}' marker in '{codeReviewRoot}'. Skipping cleanup.");
            return;
        }

        log("Removing worktrees registered under CodeReview...");
        foreach (var repositoryPath in EnumerateTopLevelGitRepositories(repositoryRoot))
        {
            var worktreeList = await m_gitCommandRunner.RunAsync(repositoryPath, "worktree", "list", "--porcelain");
            if (!worktreeList.IsSuccess)
                continue;

            foreach (var worktreePath in ParseWorktreePaths(worktreeList.StandardOutput))
            {
                if (!IsChildPathOf(worktreePath, codeReviewRoot))
                    continue;

                var removeResult = await m_gitCommandRunner.RunAsync(repositoryPath, "worktree", "remove", "--force", worktreePath);
                if (!removeResult.IsSuccess)
                    log($"Warning: could not remove registered worktree '{worktreePath}'. {removeResult.GetCombinedOutput()}");
            }

            _ = await m_gitCommandRunner.RunAsync(repositoryPath, "worktree", "prune");
        }

        if (Directory.Exists(codeReviewRoot))
            Directory.Delete(codeReviewRoot, true);

        log("CodeReview folder cleared.");
    }

    private static string GetCodeReviewRoot(string repositoryRoot) =>
        Path.Combine(repositoryRoot, CodeReviewFolderName);

    private static string GetCodeReviewMarkerPath(string codeReviewRoot) =>
        Path.Combine(codeReviewRoot, CodeReviewMarkerFileName);

    private static bool HasCodeReviewMarker(string codeReviewRoot) =>
        File.Exists(GetCodeReviewMarkerPath(codeReviewRoot));

    private static void EnsureCodeReviewMarker(string repositoryRoot)
    {
        var codeReviewRoot = GetCodeReviewRoot(repositoryRoot);
        Directory.CreateDirectory(codeReviewRoot);

        var markerPath = GetCodeReviewMarkerPath(codeReviewRoot);
        if (File.Exists(markerPath))
            return;

        File.WriteAllText(
            markerPath,
            "This folder is managed by ReviewG33k. Cleanup operations are allowed only when this marker exists.");
    }

    private async Task<string> FindOrCloneRepositoryAsync(string repositoryRoot, BitbucketPullRequestReference pullRequest, Action<string> log)
    {
        var localRepository = await FindMatchingRepositoryAsync(repositoryRoot, pullRequest);
        if (localRepository != null)
        {
            log($"Using local repository: {localRepository}");
            return localRepository;
        }

        var targetPath = GetAvailableRepositoryPath(Path.Combine(repositoryRoot, pullRequest.RepoSlug));

        log($"Local repository not found. Cloning '{pullRequest.CloneUrl}'...");
        var cloneResult = await m_gitCommandRunner.RunAsync(repositoryRoot, "clone", pullRequest.CloneUrl, targetPath);
        EnsureSuccess(cloneResult, "Failed to clone repository.");

        log($"Clone complete: {targetPath}");
        return targetPath;
    }

    private async Task<string> FindMatchingRepositoryAsync(string repositoryRoot, BitbucketPullRequestReference pullRequest)
    {
        var expectedRepo = pullRequest.RepoSlug;
        var expectedProject = pullRequest.ProjectKey;
        var expectedHost = pullRequest.Host;

        string nameOnlyMatch = null;

        foreach (var candidate in EnumerateTopLevelGitRepositories(repositoryRoot))
        {
            var folderName = Path.GetFileName(candidate);
            if (folderName.Equals(expectedRepo, StringComparison.OrdinalIgnoreCase) && nameOnlyMatch == null)
                nameOnlyMatch = candidate;

            var remoteUrlResult = await m_gitCommandRunner.RunAsync(candidate, "config", "--get", "remote.origin.url");
            if (!remoteUrlResult.IsSuccess)
                continue;

            var remoteUrl = remoteUrlResult.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(remoteUrl))
                continue;

            if (TryParseRemoteIdentity(remoteUrl, out var remoteHost, out var remoteProject, out var remoteRepo) &&
                remoteRepo.Equals(expectedRepo, StringComparison.OrdinalIgnoreCase) &&
                remoteProject.Equals(expectedProject, StringComparison.OrdinalIgnoreCase) &&
                remoteHost.Equals(expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return nameOnlyMatch;
    }

    private static bool TryParseRemoteIdentity(string remoteUrl, out string host, out string project, out string repo)
    {
        host = string.Empty;
        project = string.Empty;
        repo = string.Empty;

        if (string.IsNullOrWhiteSpace(remoteUrl))
            return false;

        remoteUrl = remoteUrl.Trim();

        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length >= 3 && segments[0].Equals("scm", StringComparison.OrdinalIgnoreCase))
            {
                project = segments[1];
                repo = RemoveGitSuffix(segments[2]);
                return true;
            }

            if (segments.Length >= 4 &&
                segments[0].Equals("projects", StringComparison.OrdinalIgnoreCase) &&
                segments[2].Equals("repos", StringComparison.OrdinalIgnoreCase))
            {
                project = segments[1];
                repo = RemoveGitSuffix(segments[3]);
                return true;
            }

            if (segments.Length >= 2)
            {
                project = segments[^2];
                repo = RemoveGitSuffix(segments[^1]);
                return true;
            }

            return false;
        }

        var sshMatch = Regex.Match(
            remoteUrl,
            "^(?:.+@)?(?<host>[^:]+):(?<project>[^/]+)/(?<repo>.+)$",
            RegexOptions.IgnoreCase);

        if (!sshMatch.Success)
            return false;

        host = sshMatch.Groups["host"].Value;
        project = sshMatch.Groups["project"].Value;
        repo = RemoveGitSuffix(sshMatch.Groups["repo"].Value);

        return !string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(project) && !string.IsNullOrWhiteSpace(repo);
    }

    private static string RemoveGitSuffix(string value)
    {
        var result = value.Trim('/').Trim();
        if (result.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            result = result[..^4];

        return result;
    }

    private async Task RemoveWorktreeIfPresentAsync(string localRepository, string reviewFolder, Action<string> log)
    {
        if (Directory.Exists(reviewFolder))
        {
            log($"Removing existing review folder at '{reviewFolder}'...");
            var removeResult = await m_gitCommandRunner.RunAsync(localRepository, "worktree", "remove", "--force", reviewFolder);
            if (!removeResult.IsSuccess)
                log($"Warning: git worktree remove reported an issue. {removeResult.GetCombinedOutput()}");

            // git worktree remove often deletes the folder itself.
            if (Directory.Exists(reviewFolder))
                Directory.Delete(reviewFolder, true);
        }
    }

    private static IEnumerable<string> ParseWorktreePaths(string output)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("worktree ", StringComparison.OrdinalIgnoreCase))
                yield return line["worktree ".Length..].Trim();
        }
    }

    private static IEnumerable<string> EnumerateTopLevelGitRepositories(string repositoryRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(repositoryRoot))
        {
            if (Path.GetFileName(directory).Equals(CodeReviewFolderName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsGitRepository(directory))
                continue;

            yield return directory;
        }
    }

    private static bool IsGitRepository(string directory) =>
        Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    private static string GetAvailableRepositoryPath(string preferredPath)
    {
        if (!Directory.Exists(preferredPath))
            return preferredPath;

        var parent = Path.GetDirectoryName(preferredPath) ?? throw new InvalidOperationException("Invalid repository path.");
        var folderName = Path.GetFileName(preferredPath);

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(parent, $"{folderName}-{index}");
            if (!Directory.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Could not find an available folder name for clone target.");
    }

    private static string FindTopLevelSolutionFile(string rootFolder)
    {
        var solutions = Directory
            .EnumerateFiles(rootFolder, "*.sln", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                Depth = GetDepth(Path.GetRelativePath(rootFolder, path))
            })
            .OrderBy(o => o.Depth)
            .ThenBy(o => o.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return solutions.FirstOrDefault()?.Path;
    }

    private static string FindBestSolutionFile(string rootFolder, IReadOnlyCollection<string> changedPaths)
    {
        if (changedPaths != null && changedPaths.Count > 0)
        {
            var candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var changedPath in changedPaths)
            {
                var closest = FindClosestSolutionForPath(rootFolder, changedPath);
                if (closest == null)
                    continue;

                candidates.TryGetValue(closest, out var currentScore);
                candidates[closest] = currentScore + 1;
            }

            if (candidates.Count > 0)
            {
                return candidates
                    .OrderByDescending(entry => entry.Value)
                    .ThenByDescending(entry => GetDepth(Path.GetRelativePath(rootFolder, entry.Key)))
                    .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .First()
                    .Key;
            }
        }

        return FindTopLevelSolutionFile(rootFolder);
    }

    private static string FindClosestSolutionForPath(string rootFolder, string changedPath)
    {
        if (string.IsNullOrWhiteSpace(changedPath))
            return null;

        var normalizedRelativePath = NormalizeRelativePath(changedPath);
        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
            return null;

        var fullPath = Path.GetFullPath(Path.Combine(rootFolder, normalizedRelativePath));
        if (!IsChildPathOf(fullPath, rootFolder))
            return null;

        var currentDirectory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(currentDirectory))
            return null;

        while (!string.IsNullOrWhiteSpace(currentDirectory) && IsChildPathOf(currentDirectory, rootFolder))
        {
            var matches = Directory
                .EnumerateFiles(currentDirectory, "*.sln", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matches.Length > 0)
                return matches[0];

            if (AreSamePath(currentDirectory, rootFolder))
                break;

            currentDirectory = Path.GetDirectoryName(currentDirectory);
        }

        return null;
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        normalized = normalized.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool AreSamePath(string firstPath, string secondPath)
    {
        static string NormalizeFullPath(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(NormalizeFullPath(firstPath), NormalizeFullPath(secondPath), GetPathComparison());
    }

    private static int GetDepth(string relativePath)
    {
        var depth = 0;
        foreach (var character in relativePath)
        {
            if (character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar)
                depth++;
        }

        return depth;
    }

    private static bool IsChildPathOf(string childPath, string parentPath)
    {
        var child = AppendDirectorySeparator(Path.GetFullPath(childPath));
        var parent = AppendDirectorySeparator(Path.GetFullPath(parentPath));

        return child.StartsWith(parent, GetPathComparison());
    }

    private static string AppendDirectorySeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static StringComparison GetPathComparison() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void EnsureSuccess(GitCommandResult result, string message)
    {
        if (result.IsSuccess)
            return;

        throw new InvalidOperationException($"{message}{Environment.NewLine}git {result.CommandText}{Environment.NewLine}{result.GetCombinedOutput()}");
    }
}

public sealed class PrepareReviewResult
{
    public PrepareReviewResult(string localRepositoryPath, string reviewWorktreePath, string solutionPath)
    {
        LocalRepositoryPath = localRepositoryPath;
        ReviewWorktreePath = reviewWorktreePath;
        SolutionPath = solutionPath;
    }

    public string LocalRepositoryPath { get; }

    public string ReviewWorktreePath { get; }

    public string SolutionPath { get; }
}
