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
/// Loads current files against the merge base of HEAD and a target branch.
/// </summary>
/// <remarks>
/// Resolves local refs without fetching and preserves the original path of renamed files.
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

    private async Task<(string Root, string MergeBase, (string OldPath, string NewPath)[] Changes, string[] Paths)> ResolveAsync(string repository, string target)
    {
        var root = Path.GetFullPath((await RunAsync(repository, "rev-parse", "--show-toplevel")).Trim());
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("Choose a target branch, such as main.");
        var reference = target.StartsWith("refs/", StringComparison.Ordinal) ? target : $"refs/remotes/origin/{target}";
        if (!(await m_git.RunAsync(root, "show-ref", "--verify", "--quiet", reference)).IsSuccess)
            reference = $"refs/heads/{target}";
        var mergeBase = (await RunAsync(root, "merge-base", "HEAD", reference)).Trim();
        var statuses = (await RunAsync(root, "diff", "--name-status", "-z", "--find-renames", mergeBase, "--")).Split('\0');
        var changes = ParseChanges(statuses);
        var untracked = (await RunAsync(root, "ls-files", "--others", "--exclude-standard", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return (root, mergeBase, changes, changes.Select(c => c.NewPath).Concat(untracked).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray());
    }

    private async Task<DiffLensFile> LoadFileAsync(string root, string mergeBase, (string OldPath, string NewPath)[] changes, string path)
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
        var change = changes.FirstOrDefault(c => c.NewPath == relative);
        var oldPath = change.OldPath ?? relative;
        var baseline = string.Empty;
        var exists = await m_git.RunAsync(root, "ls-tree", "-z", mergeBase, "--", oldPath);
        if (!exists.IsSuccess)
            throw new IOException(exists.StandardError);
        if (!string.IsNullOrEmpty(exists.StandardOutput))
        {
            var result = await m_git.RunBytesAsync(root, "show", $"{mergeBase}:{oldPath}");
            if (!result.IsSuccess)
                throw new IOException(result.StandardError);
            baseline = GitChangedFileContent.DecodeText(result.StandardOutput)
                ?? throw new InvalidOperationException("The baseline is not supported text.");
        }
        var bytes = File.Exists(fullPath) ? await File.ReadAllBytesAsync(fullPath) : null;
        if (bytes == null && string.IsNullOrEmpty(exists.StandardOutput))
            throw new FileNotFoundException("File is absent from both the branch base and working tree.", fullPath);
        return new DiffLensFile(fullPath, relative, mergeBase, baseline, bytes);
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
