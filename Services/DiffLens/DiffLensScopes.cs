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
using System.Threading.Tasks;

namespace ReviewG33k.Services.DiffLens;

/// <summary>
/// Identifies a Git comparison independently of its display label.
/// </summary>
/// <remarks>
/// Commit IDs are immutable; local scopes are reloaded from Git on refresh.
/// </remarks>
internal sealed record DiffLensScope(string Id, string Label, string Commit = null, string Date = null, string ShortHash = null)
{
    public bool IsReadOnly => Id == "staged" || Commit != null;
    public override string ToString() => Label;
}

internal sealed partial class DiffLensFileLoader
{
    public async Task<(string Target, List<DiffLensScope> Scopes)> GetScopesAsync(string root, string target, DiffLensScope retained = null)
    {
        Report("Reading Git comparisons…");
        if (string.IsNullOrWhiteSpace(target) || target == "HEAD")
        {
            target = await new LocalBaseBranchService(m_git).ResolveLocalBaseBranchAsync(root, null, false, null);
            if (string.IsNullOrWhiteSpace(target))
            {
                foreach (var candidate in new[] { "main", "master" })
                    if ((await m_git.RunAsync(root, m_cancellation, "rev-parse", "--verify", $"refs/heads/{candidate}")).IsSuccess)
                    {
                        target = candidate;
                        break;
                    }
            }
            target ??= "HEAD";
        }
        var staged = !string.IsNullOrEmpty(await RunAsync(root, "diff", "--cached", "--name-only", "-z"));
        var unstaged = !string.IsNullOrEmpty(await RunAsync(root, "diff", "--name-only", "-z")) ||
            !string.IsNullOrEmpty(await RunAsync(root, "ls-files", "--others", "--exclude-standard", "-z"));
        var scopes = new List<DiffLensScope>();
        if (staged || unstaged)
            scopes.Add(new DiffLensScope("local", "All local changes"));
        if (staged && unstaged)
        {
            scopes.Add(new DiffLensScope("unstaged", "Unstaged changes"));
            scopes.Add(new DiffLensScope("staged", "Staged changes"));
        }
        scopes.Add(new DiffLensScope("branch", $"Entire branch · base: {target}"));
        if (retained != null && scopes.All(s => s.Id != retained.Id) && retained.Commit == null)
            scopes.Insert(0, retained);
        var head = await m_git.RunAsync(root, m_cancellation, "rev-parse", "--verify", "HEAD");
        if (head.IsSuccess)
        {
            var baseline = (await ResolveAsync(root, target)).MergeBase;
            // On the base branch itself, show its history rather than an empty commit picker.
            var current = await m_git.RunAsync(root, m_cancellation, "symbolic-ref", "--quiet", "--short", "HEAD");
            var onBaseBranch = current.IsSuccess && current.StandardOutput.Trim() == LocalBaseBranchService.NormalizeBranchName(target);
            var range = baseline == head.StandardOutput.Trim() && onBaseBranch ? "HEAD" : $"{baseline}..HEAD";
            var log = await RunAsync(root, "log", "--first-parent", "--format=%H%x09%h%x09%as%x09%s", range, "--");
            foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.TrimEnd('\r').Split('\t', 4);
                if (fields.Length == 4)
                    scopes.Add(new DiffLensScope(fields[0], fields[3], fields[0], fields[2], fields[1]));
            }
        }
        if (retained?.Commit != null && scopes.All(s => s.Id != retained.Id))
            scopes.Add(retained);
        return (target, scopes);
    }

    public async Task<DiffLensChangeSet> LoadScopeAsync(string repository, string target, DiffLensScope scope, string path = null)
    {
        if (scope.Id == "tree")
            return await LoadReviewSetAsync(repository, "HEAD", CommandLineReviewMode.Tree, path);
        if (scope.Id == "branch")
            return await LoadSetAsync(repository, target, path);
        if (scope.Id == "local")
            return await LoadSetAsync(repository, "HEAD", path);

        var root = Path.GetFullPath((await RunAsync(repository, "rev-parse", "--show-toplevel")).Trim());
        Report("Reading comparison…");
        string baseline;
        string revision;
        string status;
        if (scope.Commit != null)
        {
            revision = (await RunAsync(root, "rev-parse", "--verify", scope.Commit + "^{commit}")).Trim();
            var parents = (await RunAsync(root, "rev-list", "--parents", "-n", "1", revision)).Trim().Split(' ');
            baseline = parents.Length > 1 ? parents[1] : null;
            status = baseline == null
                ? await RunAsync(root, "diff-tree", "--root", "--no-commit-id", "-r", "--name-status", "-z", revision, "--")
                : await RunAsync(root, "diff", "--name-status", "-z", "--find-renames", baseline, revision, "--");
        }
        else
        {
            // Capture the index as an immutable tree, without changing the index or working files.
            var index = (await RunAsync(root, "write-tree")).Trim();
            baseline = scope.Id == "unstaged" ? index : (await RunAsync(root, "rev-parse", "HEAD")).Trim();
            revision = scope.Id == "staged" ? index : null;
            status = revision == null
                ? await RunAsync(root, "diff", "--name-status", "-z", "--find-renames", baseline, "--")
                : await RunAsync(root, "diff", "--name-status", "-z", "--find-renames", baseline, revision, "--");
        }
        var changes = ParseChanges(status.Split('\0'));
        var paths = changes.Select(c => c.NewPath);
        if (scope.Id == "unstaged")
            paths = paths.Concat((await RunAsync(root, "ls-files", "--others", "--exclude-standard", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries));
        var selected = paths.Distinct(StringComparer.Ordinal).Where(p => path == null || p == path).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var entries = new List<DiffLensEntry>();
        foreach (var relative in selected)
        {
            Report("Loading files", entries.Count, selected.Length);
            try
            {
                if (revision == null)
                    entries.Add(new DiffLensEntry(relative, await LoadFileAsync(root, baseline, changes, relative), null));
                else
                {
                    var oldPath = changes.First(c => c.NewPath == relative).OldPath;
                    var before = baseline == null ? null : await ReadSnapshotBytesAsync(root, baseline, oldPath);
                    var after = await ReadSnapshotBytesAsync(root, revision, relative);
                    var text = before == null ? string.Empty : GitChangedFileContent.DecodeText(before)
                        ?? throw new InvalidOperationException("Binary file — no text preview.");
                    var file = new DiffLensFile(Path.Combine(root, relative), relative, baseline ?? "Empty tree", text, after, readOnly: true);
                    entries.Add(new DiffLensEntry(relative, file, null));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                entries.Add(new DiffLensEntry(relative, null, ex.Message));
            }
        }
        Report("Loading files", entries.Count, selected.Length);
        return new DiffLensChangeSet(root, baseline ?? "Empty tree", entries);
    }

    private async Task<byte[]> ReadSnapshotBytesAsync(string root, string revision, string path)
    {
        if (string.IsNullOrEmpty(await RunAsync(root, "ls-tree", "-z", revision, "--", path)))
            return null;
        var result = await m_git.RunBytesAsync(root, m_cancellation, "show", $"{revision}:{path}");
        if (!result.IsSuccess)
            throw new IOException(result.StandardError);
        return result.StandardOutput;
    }
}
