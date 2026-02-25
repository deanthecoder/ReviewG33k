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

namespace ReviewG33k.Services;

public sealed class CodeSmellReportAnalyzer
{
    private readonly GitCommandRunner m_gitCommandRunner;
    private readonly IReadOnlyList<ICodeReviewCheck> m_checks;

    public CodeSmellReportAnalyzer(GitCommandRunner gitCommandRunner)
    {
        m_gitCommandRunner = gitCommandRunner ?? throw new ArgumentNullException(nameof(gitCommandRunner));
        m_checks = CreateChecks();
    }

    public IReadOnlyList<ICodeReviewCheck> Checks => m_checks;

    public async Task<CodeSmellReport> AnalyzeAsync(string reviewWorktreePath, string targetBranch)
    {
        var report = new CodeSmellReport();
        if (string.IsNullOrWhiteSpace(reviewWorktreePath) || !Directory.Exists(reviewWorktreePath))
        {
            report.AddInfo("Code review scan skipped: review worktree path not found.");
            return report;
        }

        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            report.AddInfo("Code review scan skipped: target branch unavailable from PR metadata.");
            return report;
        }

        var baseRef = $"origin/{targetBranch}";
        var fetchTargetResult = await m_gitCommandRunner.RunAsync(
            reviewWorktreePath,
            "fetch",
            "--prune",
            "origin",
            $"+refs/heads/{targetBranch}:{baseRef}");
        if (!fetchTargetResult.IsSuccess)
        {
            report.AddInfo($"Code review scan skipped: unable to fetch target branch '{targetBranch}'.");
            return report;
        }

        var diffRange = $"{baseRef}...HEAD";
        var nameStatusResult = await m_gitCommandRunner.RunAsync(
            reviewWorktreePath,
            "diff",
            "--name-status",
            "--find-renames",
            diffRange);
        if (!nameStatusResult.IsSuccess)
        {
            report.AddInfo("Code review scan skipped: unable to enumerate diff file status.");
            return report;
        }

        var changedFileEntries = ParseNameStatusOutput(nameStatusResult.StandardOutput)
            .Where(entry => entry.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(entry => !entry.Path.EndsWith(".axaml.cs", StringComparison.OrdinalIgnoreCase))
            .Where(entry => !entry.Path.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (changedFileEntries.Length == 0)
        {
            report.AddInfo("Code review scan: no changed C# files detected.");
            return report;
        }

        var changedFiles = new List<CodeReviewChangedFile>(changedFileEntries.Length);
        foreach (var entry in changedFileEntries)
        {
            var fullPath = Path.Combine(reviewWorktreePath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                continue;

            var text = await File.ReadAllTextAsync(fullPath);
            var lines = SplitLines(text);
            var addedLineNumbers = await GetAddedLineNumbersAsync(reviewWorktreePath, diffRange, entry.Path);

            changedFiles.Add(new CodeReviewChangedFile(entry.Status, entry.Path, fullPath, text, lines, addedLineNumbers));
        }

        if (changedFiles.Count == 0)
        {
            report.AddInfo("Code review scan: no analyzable changed C# files found.");
            return report;
        }

        report.AddInfo($"Code review scan: analyzing {changedFiles.Count} changed C# file(s).");
        var context = BuildContext(changedFiles);
        m_checks
            .AsParallel()
            .ForAll(check => check.Analyze(context, report));

        return report;
    }

    private static CodeReviewAnalysisContext BuildContext(IReadOnlyList<CodeReviewChangedFile> changedFiles)
    {
        var addedTestFilesByName = new HashSet<string>(
            changedFiles
                .Where(file => file.IsAdded)
                .Where(file => file.Path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetFileName(file.Path)),
            StringComparer.OrdinalIgnoreCase);

        return new CodeReviewAnalysisContext(changedFiles, addedTestFilesByName);
    }

    private async Task<HashSet<int>> GetAddedLineNumbersAsync(string workingDirectory, string diffRange, string relativePath)
    {
        var result = await m_gitCommandRunner.RunAsync(
            workingDirectory,
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

    private static IReadOnlyList<ICodeReviewCheck> CreateChecks() =>
    [
        new AsyncVoidCodeReviewCheck(),
        new AsyncMethodNameSuffixCodeReviewCheck(),
        new PublicMethodArgumentGuardsCodeReviewCheck(),
        new EmptyCatchCodeReviewCheck(),
        new SwallowingCatchCodeReviewCheck(),
        new LockTargetCodeReviewCheck(),
        new TaskRunAsyncCodeReviewCheck(),
        new PropertyCanBeAutoPropertyCodeReviewCheck(),
        new PrivatePropertyShouldBeFieldCodeReviewCheck(),
        new MethodParameterCountCodeReviewCheck(),
        new GenericTypeNameSuffixCodeReviewCheck(),
        new IfElseBraceConsistencyCodeReviewCheck(),
        new ConstructorTooLongCodeReviewCheck(),
        new ThreadSleepCodeReviewCheck(),
        new MethodCanBeStaticCodeReviewCheck(),
        new UnobservedTaskResultCodeReviewCheck(),
        new DisposableNotDisposedCodeReviewCheck(),
        new MultipleEnumerationCodeReviewCheck(),
        new PublicMutableStaticStateCodeReviewCheck(),
        new UnusedPrivateMemberCodeReviewCheck(),
        new MissingXmlDocsCodeReviewCheck(),
        new MissingUnitTestsCodeReviewCheck(),
        new UnusedUsingRoslynCodeReviewCheck()
    ];
}
