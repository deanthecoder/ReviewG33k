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
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReviewG33k.Services.DiffLens;

/// <summary>
/// Loads current files against HEAD or the merge base with a target branch.
/// </summary>
/// <remarks>
/// Uses HEAD for uncommitted changes, resolves branch refs without fetching, and preserves rename paths.
/// </remarks>
internal sealed class DiffLensFileLoader
{
    private readonly GitCommandRunner m_git = new();

    public async Task<DiffLensFile> LoadAsync(string repository, string target, string path)
    {
        var snapshot = await ResolveAsync(repository, target);
        path = string.IsNullOrWhiteSpace(path) ? snapshot.Paths.FirstOrDefault() : path;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("No changes found since the branch base.");
        return await LoadFileAsync(snapshot.Root, snapshot.MergeBase, snapshot.Changes, path);
    }

    public async Task<DiffLensChangeSet> LoadSetAsync(string repository, string target, string path = null)
    {
        var snapshot = await ResolveAsync(repository, target);
        var paths = string.IsNullOrWhiteSpace(path) ? snapshot.Paths : [path];
        var entries = new List<DiffLensEntry>();
        foreach (var relative in paths)
        {
            try
            {
                var file = await LoadFileAsync(snapshot.Root, snapshot.MergeBase, snapshot.Changes, relative);
                entries.Add(new DiffLensEntry(file.RelativePath, file, null));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                // Preserve unsupported files in the map instead of silently omitting changes.
                entries.Add(new DiffLensEntry(relative, null, ex.Message));
            }
        }
        return new DiffLensChangeSet(snapshot.Root, snapshot.MergeBase, entries);
    }

    public async Task<DiffLensChangeSet> LoadReviewSetAsync(
        string repository, string target, CommandLineReviewMode mode, string path = null)
    {
        if (mode == CommandLineReviewMode.Tree)
            return await LoadTreeSetAsync(repository, path);
        ICodeReviewChangedFileSource source = mode switch
        {
            CommandLineReviewMode.Uncommitted => new GitWorkingTreeChangedFileSource(m_git, repository),
            CommandLineReviewMode.Committed => new GitBranchComparisonChangedFileSource(m_git, repository, target, fetchTargetBranch: true),
            _ => new LocalRepositoryChangedFileSource(repository, RepositoryUtilities.IsGitRepository(repository) ? m_git : null)
        };
        var reviewFiles = (await source.LoadAsync()).Files;
        var paths = reviewFiles.Select(file => file.Path)
            .Where(relative => string.IsNullOrWhiteSpace(path) || relative == path)
            .OrderBy(relative => relative, StringComparer.Ordinal).ToArray();
        var root = Path.GetFullPath(repository);
        var entries = new List<DiffLensEntry>();
        if (!RepositoryUtilities.IsGitRepository(root))
        {
            foreach (var relative in paths)
            {
                try
                {
                    var fullPath = ResolveEditablePath(root, relative);
                    var file = new DiffLensFile(fullPath, relative, "Working tree", string.Empty, await File.ReadAllBytesAsync(fullPath));
                    entries.Add(new DiffLensEntry(relative, file, null));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
                {
                    entries.Add(new DiffLensEntry(relative, null, ex.Message));
                }
            }
            return new DiffLensChangeSet(root, "Working tree", entries);
        }

        var snapshot = await ResolveAsync(root, mode == CommandLineReviewMode.Committed ? target : "HEAD");
        foreach (var relative in paths)
        {
            try
            {
                var file = await LoadFileAsync(snapshot.Root, snapshot.MergeBase, snapshot.Changes, relative);
                entries.Add(new DiffLensEntry(relative, file, null));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                entries.Add(new DiffLensEntry(relative, null, ex.Message));
            }
        }
        return new DiffLensChangeSet(snapshot.Root, snapshot.MergeBase, entries);
    }

    private async Task<DiffLensChangeSet> LoadTreeSetAsync(string repository, string path)
    {
        var root = Path.GetFullPath(repository);
        var isGit = RepositoryUtilities.IsGitRepository(root);
        var source = new LocalRepositoryChangedFileSource(root, isGit ? m_git : null);
        var files = (await source.EnumerateAnalyzableFilesAsync(new DirectoryInfo(root)))
            .Select(file => Path.GetRelativePath(root, file.FullName).Replace('\\', '/'))
            .Where(relative => string.IsNullOrWhiteSpace(path) || relative == path)
            .OrderBy(relative => relative, StringComparer.Ordinal).ToArray();
        var revision = "Working tree";
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, byte[]> baselines = new Dictionary<string, byte[]>();
        if (isGit)
        {
            var snapshot = await ResolveAsync(root, "HEAD");
            root = snapshot.Root;
            revision = snapshot.MergeBase;
            changes = snapshot.Changes.ToDictionary(change => change.NewPath, change => change.OldPath, StringComparer.Ordinal);
            baselines = await GitBaselineBatchReader.ReadAsync(root, revision, files.Select(relative => changes.GetValueOrDefault(relative, relative)));
        }
        var entries = new List<DiffLensEntry>();
        foreach (var relative in files)
        {
            try
            {
                var baseline = baselines.GetValueOrDefault(changes.GetValueOrDefault(relative, relative));
                var file = await LoadFileAsync(root, revision, [], relative, baseline, baselineProvided: true);
                entries.Add(new DiffLensEntry(relative, file, null));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                entries.Add(new DiffLensEntry(relative, null, ex.Message));
            }
        }
        return new DiffLensChangeSet(root, revision, entries);
    }

    private async Task<(string Root, string MergeBase, (string OldPath, string NewPath)[] Changes, string[] Paths)> ResolveAsync(string repository, string target)
    {
        var root = Path.GetFullPath((await RunAsync(repository, "rev-parse", "--show-toplevel")).Trim());
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("Choose a target branch, such as main.");
        string mergeBase;
        if (target == "HEAD")
        {
            // Comparing the index and working tree to HEAD excludes already committed branch changes.
            mergeBase = (await RunAsync(root, "rev-parse", "HEAD")).Trim();
        }
        else
        {
            var reference = target.StartsWith("refs/", StringComparison.Ordinal) ? target : $"refs/remotes/origin/{target}";
            if (!(await m_git.RunAsync(root, "show-ref", "--verify", "--quiet", reference)).IsSuccess)
                reference = $"refs/heads/{target}";
            mergeBase = (await RunAsync(root, "merge-base", "HEAD", reference)).Trim();
        }
        var statuses = (await RunAsync(root, "diff", "--name-status", "-z", "--find-renames", mergeBase, "--")).Split('\0');
        var changes = ParseChanges(statuses);
        var untracked = (await RunAsync(root, "ls-files", "--others", "--exclude-standard", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return (root, mergeBase, changes, changes.Select(c => c.NewPath).Concat(untracked).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    private async Task<DiffLensFile> LoadFileAsync(string root, string mergeBase, (string OldPath, string NewPath)[] changes, string path,
        byte[] baselineBytes = null, bool baselineProvided = false)
    {
        var fullPath = ResolveEditablePath(root, path);
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        var change = changes.FirstOrDefault(c => c.NewPath == relative);
        var oldPath = change.OldPath ?? relative;
        var hasBaseline = baselineBytes != null;
        if (!baselineProvided)
        {
            var exists = await m_git.RunAsync(root, "ls-tree", "-z", mergeBase, "--", oldPath);
            if (!exists.IsSuccess)
                throw new IOException(exists.StandardError);
            hasBaseline = !string.IsNullOrEmpty(exists.StandardOutput);
            if (hasBaseline)
            {
                var result = await m_git.RunBytesAsync(root, "show", $"{mergeBase}:{oldPath}");
                if (!result.IsSuccess)
                    throw new IOException(result.StandardError);
                baselineBytes = result.StandardOutput;
            }
        }
        var baseline = baselineBytes == null ? string.Empty : GitChangedFileContent.DecodeText(baselineBytes)
            ?? throw new InvalidOperationException("The baseline is not supported text.");
        var bytes = File.Exists(fullPath) ? await File.ReadAllBytesAsync(fullPath) : null;
        if (bytes == null && !hasBaseline)
            throw new FileNotFoundException("File is absent from both the branch base and working tree.", fullPath);
        return new DiffLensFile(fullPath, relative, mergeBase, baseline, bytes);
    }

    private static string ResolveEditablePath(string root, string path)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("Choose a file inside this repository.");
        // Refuse symlink traversal when opening an editable document.
        for (var item = fullPath; !string.Equals(item, root, StringComparison.OrdinalIgnoreCase); item = Path.GetDirectoryName(item))
        {
            if (string.IsNullOrEmpty(item))
                throw new InvalidOperationException("Unable to resolve the repository path.");
            if ((File.Exists(item) || Directory.Exists(item)) && File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Symlink files and directories are not editable in this prototype.");
        }
        return fullPath;
    }

    private async Task<string> RunAsync(string root, params string[] args)
    {
        var result = await m_git.RunAsync(root, args);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.StandardError.Trim());
        return result.StandardOutput;
    }

    private static (string OldPath, string NewPath)[] ParseChanges(string[] fields)
    {
        var result = new System.Collections.Generic.List<(string, string)>();
        for (var i = 0; i < fields.Length && !string.IsNullOrEmpty(fields[i]);)
        {
            var status = fields[i++];
            var oldPath = fields[i++];
            var newPath = status.StartsWith('R') || status.StartsWith('C') ? fields[i++] : oldPath;
            result.Add((oldPath, newPath));
        }
        return result.ToArray();
    }
}
