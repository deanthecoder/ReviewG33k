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

    public async Task<CodeSmellReport> AnalyzeAsync(
        string reviewWorktreePath,
        string targetBranch,
        Action<string> progressLogger = null,
        Action<int, int, string> progressReporter = null)
    {
        var changedFileSource = new GitBranchComparisonChangedFileSource(
            m_gitCommandRunner,
            reviewWorktreePath,
            targetBranch,
            fetchTargetBranch: true);

        return await AnalyzeAsync(changedFileSource, progressLogger, progressReporter);
    }

    public async Task<CodeSmellReport> AnalyzeAsync(
        ICodeReviewChangedFileSource changedFileSource,
        Action<string> progressLogger = null,
        Action<int, int, string> progressReporter = null)
    {
        var report = new CodeSmellReport();
        if (changedFileSource == null)
        {
            report.AddInfo("Code review scan skipped: Changed file source unavailable.");
            return report;
        }

        var sourceResult = await changedFileSource.LoadAsync();
        foreach (var infoMessage in sourceResult?.InfoMessages ?? [])
            report.AddInfo(infoMessage);

        var changedFiles = sourceResult?.Files?.Where(file => file != null).ToArray() ?? [];
        if (changedFiles.Length == 0)
            return report;

        var context = BuildContext(changedFiles);
        var totalChecks = m_checks.Count;
        progressReporter?.Invoke(0, totalChecks, null);

        var checkTasks = m_checks
            .Select(check => Task.Run(() =>
            {
                var checkReport = new CodeSmellReport();
                check.Analyze(context, checkReport);
                return (Check: check, Report: checkReport);
            }))
            .ToArray();

        for (var index = 0; index < totalChecks; index++)
        {
            progressLogger?.Invoke($"CHECK RUN: {index + 1}/{totalChecks} {m_checks[index].DisplayName}");
            var checkResult = await checkTasks[index];
            MergeReport(report, checkResult.Report);
            progressReporter?.Invoke(index + 1, totalChecks, checkResult.Check.DisplayName);
        }

        return report;
    }

    public CodeSmellReport AnalyzeFiles(IReadOnlyList<CodeReviewChangedFile> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);

        var report = new CodeSmellReport();
        var files = changedFiles.Where(file => file != null).ToArray();
        if (files.Length == 0)
            return report;

        var context = BuildContext(files);
        m_checks
            .AsParallel()
            .ForAll(check => check.Analyze(context, report));

        return report;
    }

    private static void MergeReport(CodeSmellReport destination, CodeSmellReport source)
    {
        if (destination == null || source == null)
            return;

        foreach (var info in source.Info)
            destination.AddInfo(info);

        foreach (var finding in source.Findings)
            destination.AddFinding(finding.Severity, finding.RuleId, finding.FilePath, finding.LineNumber, finding.Message);
    }

    private static CodeReviewAnalysisContext BuildContext(IReadOnlyList<CodeReviewChangedFile> changedFiles)
    {
        var csharpFiles = changedFiles
            .Where(file => CodeReviewFileClassification.IsAnalyzableChangedCSharpPath(file.Path))
            .ToArray();
        var resxFiles = changedFiles
            .Where(file => CodeReviewFileClassification.IsAnalyzableResxPath(file.Path))
            .ToArray();

        var addedTestFilesByName = new HashSet<string>(
            csharpFiles
                .Where(file => file.IsAdded)
                .Where(file => CodeReviewFileClassification.IsTestFilePath(file.Path))
                .Select(file => Path.GetFileName(file.Path)),
            StringComparer.OrdinalIgnoreCase);

        return new CodeReviewAnalysisContext(csharpFiles, addedTestFilesByName, resxFiles);
    }

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
        new PrivateGetOnlyAutoPropertyShouldBeFieldCodeReviewCheck(),
        new PrivatePropertyShouldBeFieldCodeReviewCheck(),
        new PrivateFieldCanBeReadonlyCodeReviewCheck(),
        new MissingBlankLineBetweenMethodsCodeReviewCheck(),
        new MethodParameterCountCodeReviewCheck(),
        new GenericTypeNameSuffixCodeReviewCheck(),
        new IfElseBraceConsistencyCodeReviewCheck(),
        new IfElseUnnecessaryBracesCodeReviewCheck(),
        new ConstructorTooLongCodeReviewCheck(),
        new ThreadSleepCodeReviewCheck(),
        new ThrowExCodeReviewCheck(),
        new MethodCanBeStaticCodeReviewCheck(),
        new RedundantSelfLookupCodeReviewCheck(),
        new BooleanLiteralComparisonCodeReviewCheck(),
        new UnnecessaryCastCodeReviewCheck(),
        new UnnecessaryVerbatimStringPrefixCodeReviewCheck(),
        new UnobservedTaskResultCodeReviewCheck(),
        new DisposeMethodWithoutIDisposableCodeReviewCheck(),
        new DisposableNotDisposedCodeReviewCheck(),
        new MultipleEnumerationCodeReviewCheck(),
        new PublicMutableStaticStateCodeReviewCheck(),
        new UnusedPrivateMemberCodeReviewCheck(),
        new MissingXmlDocsCodeReviewCheck(),
        new MissingUnitTestsCodeReviewCheck(),
        new MissingTestsForNewPublicMethodsCodeReviewCheck(),
        new MissingReadmeForNewProjectCodeReviewCheck(),
        new MissingTypedBindingContextCodeReviewCheck(),
        new MultipleClassesPerFileCodeReviewCheck(),
        new ResxMissingLocaleKeysCodeReviewCheck(),
        new ResxUnexpectedExtraKeysCodeReviewCheck(),
        new ResxEmptyTranslationValuesCodeReviewCheck(),
        new WarningSuppressionCodeReviewCheck(),
        new UnusedUsingRoslynCodeReviewCheck(),
        new LocalVariableCanBeConstCodeReviewCheck()
    ];
}
