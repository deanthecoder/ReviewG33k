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
using DTC.Core.Extensions;
using ReviewG33k.Services.Checks;
using Support = ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Services;

public sealed class GitBranchComparisonChangedFileSource : ICodeReviewChangedFileSource
{
    private readonly GitCommandRunner m_gitCommandRunner;
    private readonly string m_repositoryPath;
    private readonly string m_targetBranch;
    private readonly bool m_fetchTargetBranch;

    public GitBranchComparisonChangedFileSource(
        GitCommandRunner gitCommandRunner,
        string repositoryPath,
        string targetBranch,
        bool fetchTargetBranch = true)
    {
        m_gitCommandRunner = gitCommandRunner ?? throw new ArgumentNullException(nameof(gitCommandRunner));
        m_repositoryPath = repositoryPath ?? string.Empty;
        m_targetBranch = targetBranch ?? string.Empty;
        m_fetchTargetBranch = fetchTargetBranch;
    }

    public async Task<CodeReviewChangedFileSourceResult> LoadAsync()
    {
        var info = new List<string>();
        var repositoryPathInfo = m_repositoryPath.ToDir();

        if (string.IsNullOrWhiteSpace(m_repositoryPath) || !repositoryPathInfo.Exists())
        {
            info.Add("Code review scan skipped: Review worktree path not found.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        if (string.IsNullOrWhiteSpace(m_targetBranch))
        {
            info.Add("Code review scan skipped: Target branch unavailable from PR metadata.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        var remoteBaseRef = $"origin/{m_targetBranch}";
        var remoteBaseRefName = $"refs/remotes/{remoteBaseRef}";
        var localBaseRefName = $"refs/heads/{m_targetBranch}";
        if (m_fetchTargetBranch)
        {
            var fetchTargetResult = await m_gitCommandRunner.RunAsync(
                m_repositoryPath,
                "fetch",
                "--prune",
                "origin",
                $"+refs/heads/{m_targetBranch}:{remoteBaseRef}");
            if (!fetchTargetResult.IsSuccess)
            {
                info.Add($"Code review scan: Unable to fetch target branch '{m_targetBranch}' from origin. Trying local branch.");
            }
        }

        var hasRemoteBaseRef = await RefExistsAsync(remoteBaseRefName);
        var hasLocalBaseRef = await RefExistsAsync(localBaseRefName);
        var baseRef = hasRemoteBaseRef
            ? remoteBaseRef
            : hasLocalBaseRef
                ? m_targetBranch
                : null;
        if (baseRef == null)
        {
            info.Add($"Code review scan skipped: Target branch '{m_targetBranch}' was not found on origin or as a local branch.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        if (!hasRemoteBaseRef && hasLocalBaseRef)
            info.Add($"Code review scan: Using local target branch '{m_targetBranch}' because origin/{m_targetBranch} was not available.");

        var diffRange = $"{baseRef}...HEAD";
        var nameStatusResult = await m_gitCommandRunner.RunAsync(
            m_repositoryPath,
            "diff",
            "--name-status",
            "--find-renames",
            diffRange);
        if (!nameStatusResult.IsSuccess)
        {
            info.Add("Code review scan skipped: Unable to enumerate diff file status.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        var baseDiffEntries = ParseNameStatusOutput(nameStatusResult.StandardOutput).ToArray();
        var workingTreeEntries = await LoadWorkingTreeChangedFileEntriesAsync();
        var allChangedFileEntries = MergeChangedFileEntries(baseDiffEntries, workingTreeEntries).ToArray();
        var baseDiffPaths = new HashSet<string>(
            baseDiffEntries.Select(entry => NormalizeRepoPath(entry.Path)),
            StringComparer.OrdinalIgnoreCase);

        if (allChangedFileEntries.Length == 0)
        {
            info.Add($"Code review scan: No differences between HEAD and {baseRef}.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        var changedFileEntries = allChangedFileEntries
            .Where(entry => Support.CodeReviewFileClassification.IsAnalyzableChangedPath(entry.Path))
            .ToArray();

        if (changedFileEntries.Length == 0)
        {
            info.Add("Code review scan: No changed analyzable files detected.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        var changedFiles = new List<CodeReviewChangedFile>(changedFileEntries.Length);
        foreach (var entry in changedFileEntries)
        {
            var fullPath = repositoryPathInfo.GetFile(entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!fullPath.Exists())
                continue;

            var text = await File.ReadAllTextAsync(fullPath.FullName);
            var lines = SplitLines(text);
            HashSet<int> addedLineNumbers;
            if (baseDiffPaths.Contains(NormalizeRepoPath(entry.Path)))
                addedLineNumbers = await GetAddedLineNumbersAsync(diffRange, entry.Path);
            else if (entry.Status.Equals("A", StringComparison.OrdinalIgnoreCase))
                addedLineNumbers = new HashSet<int>(Enumerable.Range(1, lines.Count));
            else
                addedLineNumbers = await GetAddedLineNumbersAsync("HEAD", entry.Path);

            changedFiles.Add(new CodeReviewChangedFile(entry.Status, entry.Path, fullPath.FullName, text, lines, addedLineNumbers));
        }

        if (changedFiles.Count == 0)
        {
            info.Add("Code review scan: No analyzable changed files found.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        info.Add($"Code review scan: Analyzing {changedFiles.Count} changed analyzable file(s).");
        return new CodeReviewChangedFileSourceResult(changedFiles, info);
    }

    private async Task<HashSet<int>> GetAddedLineNumbersAsync(string diffRange, string relativePath)
    {
        var result = await m_gitCommandRunner.RunAsync(
            m_repositoryPath,
            "diff",
            "--unified=0",
            "--no-color",
            diffRange,
            "--",
            relativePath);
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
            return [];

        return ParseAddedLineNumbers(result.StandardOutput);
    }

    private async Task<bool> RefExistsAsync(string referenceName)
    {
        if (string.IsNullOrWhiteSpace(referenceName))
            return false;

        var result = await m_gitCommandRunner.RunAsync(
            m_repositoryPath,
            "show-ref",
            "--verify",
            "--quiet",
            referenceName);
        return result.IsSuccess;
    }

    private static HashSet<int> ParseAddedLineNumbers(string diffText)
    {
        var addedLines = new HashSet<int>();
        var lines = diffText.Replace("\r\n", "\n").Split('\n');

        var currentNewLine = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                if (TryParseHunkStart(line, out var newStart))
                    currentNewLine = newStart;
                continue;
            }

            if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                addedLines.Add(currentNewLine);
                currentNewLine++;
                continue;
            }

            if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
                continue;

            if (line.StartsWith(' '))
                currentNewLine++;
        }

        return addedLines;
    }

    private static bool TryParseHunkStart(string hunkHeader, out int newStart)
    {
        newStart = 0;
        var plusIndex = hunkHeader.IndexOf('+');
        if (plusIndex < 0)
            return false;

        var commaIndex = hunkHeader.IndexOf(',', plusIndex);
        var spaceIndex = hunkHeader.IndexOf(' ', plusIndex);
        var endIndex = commaIndex >= 0 ? commaIndex : spaceIndex;
        if (endIndex < 0)
            return false;

        var numberText = hunkHeader[(plusIndex + 1)..endIndex];
        return int.TryParse(numberText, out newStart);
    }

    private static IReadOnlyList<(string Status, string Path)> ParseNameStatusOutput(string output)
    {
        var entries = new List<(string Status, string Path)>();
        if (string.IsNullOrWhiteSpace(output))
            return entries;

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Split('\t');
            if (parts.Length < 2)
                continue;

            var status = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(status))
                continue;

            var normalizedStatus = status[..1];
            var path = normalizedStatus.Equals("R", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3
                ? parts[2]
                : parts[1];

            if (!string.IsNullOrWhiteSpace(path))
                entries.Add((normalizedStatus, path));
        }

        return entries;
    }

    private async Task<IReadOnlyList<(string Status, string Path)>> LoadWorkingTreeChangedFileEntriesAsync()
    {
        var entries = new List<(string Status, string Path)>();

        var trackedChangesResult = await m_gitCommandRunner.RunAsync(
            m_repositoryPath,
            "diff",
            "--name-status",
            "--find-renames",
            "HEAD");
        if (trackedChangesResult.IsSuccess)
            entries.AddRange(ParseNameStatusOutput(trackedChangesResult.StandardOutput));

        var untrackedResult = await m_gitCommandRunner.RunAsync(
            m_repositoryPath,
            "ls-files",
            "--others",
            "--exclude-standard");
        if (untrackedResult.IsSuccess)
            entries.AddRange(ParseUntrackedOutput(untrackedResult.StandardOutput));

        return entries;
    }

    private static IReadOnlyList<(string Status, string Path)> ParseUntrackedOutput(string output)
    {
        var entries = new List<(string Status, string Path)>();
        if (string.IsNullOrWhiteSpace(output))
            return entries;

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var path = rawLine.Trim();
            if (!string.IsNullOrWhiteSpace(path))
                entries.Add(("A", path));
        }

        return entries;
    }

    private static IReadOnlyList<(string Status, string Path)> MergeChangedFileEntries(
        IReadOnlyList<(string Status, string Path)> baseEntries,
        IReadOnlyList<(string Status, string Path)> additionalEntries)
    {
        var merged = new Dictionary<string, (string Status, string Path)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in baseEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
                continue;

            merged[NormalizeRepoPath(entry.Path)] = entry;
        }

        foreach (var entry in additionalEntries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
                continue;

            var key = NormalizeRepoPath(entry.Path);
            if (!merged.ContainsKey(key))
                merged[key] = entry;
        }

        return merged.Values.ToArray();
    }

    private static string NormalizeRepoPath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim();

    private static IReadOnlyList<string> SplitLines(string text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
