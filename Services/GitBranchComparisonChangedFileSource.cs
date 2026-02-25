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
using ReviewG33k.Services.Checks;

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

        if (string.IsNullOrWhiteSpace(m_repositoryPath) || !Directory.Exists(m_repositoryPath))
        {
            info.Add("Code review scan skipped: review worktree path not found.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        if (string.IsNullOrWhiteSpace(m_targetBranch))
        {
            info.Add("Code review scan skipped: target branch unavailable from PR metadata.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        var baseRef = $"origin/{m_targetBranch}";
        if (m_fetchTargetBranch)
        {
            var fetchTargetResult = await m_gitCommandRunner.RunAsync(
                m_repositoryPath,
                "fetch",
                "--prune",
                "origin",
                $"+refs/heads/{m_targetBranch}:{baseRef}");
            if (!fetchTargetResult.IsSuccess)
            {
                info.Add($"Code review scan skipped: unable to fetch target branch '{m_targetBranch}'.");
                return new CodeReviewChangedFileSourceResult([], info);
            }
        }

        var diffRange = $"{baseRef}...HEAD";
        var nameStatusResult = await m_gitCommandRunner.RunAsync(
            m_repositoryPath,
            "diff",
            "--name-status",
            "--find-renames",
            diffRange);
        if (!nameStatusResult.IsSuccess)
        {
            info.Add("Code review scan skipped: unable to enumerate diff file status.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        var changedFileEntries = ParseNameStatusOutput(nameStatusResult.StandardOutput)
            .Where(entry => CodeReviewFileClassification.IsAnalyzableChangedCSharpPath(entry.Path))
            .ToArray();

        if (changedFileEntries.Length == 0)
        {
            info.Add("Code review scan: no changed C# files detected.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        var changedFiles = new List<CodeReviewChangedFile>(changedFileEntries.Length);
        foreach (var entry in changedFileEntries)
        {
            var fullPath = Path.Combine(m_repositoryPath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                continue;

            var text = await File.ReadAllTextAsync(fullPath);
            var lines = SplitLines(text);
            var addedLineNumbers = await GetAddedLineNumbersAsync(diffRange, entry.Path);

            changedFiles.Add(new CodeReviewChangedFile(entry.Status, entry.Path, fullPath, text, lines, addedLineNumbers));
        }

        if (changedFiles.Count == 0)
        {
            info.Add("Code review scan: no analyzable changed C# files found.");
            return new CodeReviewChangedFileSourceResult([], info);
        }

        info.Add($"Code review scan: analyzing {changedFiles.Count} changed C# file(s).");
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

    private static IReadOnlyList<string> SplitLines(string text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
