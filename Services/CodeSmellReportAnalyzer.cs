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

    public async Task<CodeSmellReport> AnalyzeAsync(string reviewWorktreePath, string targetBranch)
    {
        var changedFileSource = new GitBranchComparisonChangedFileSource(
            m_gitCommandRunner,
            reviewWorktreePath,
            targetBranch,
            fetchTargetBranch: true);

        return await AnalyzeAsync(changedFileSource);
    }

    public async Task<CodeSmellReport> AnalyzeAsync(ICodeReviewChangedFileSource changedFileSource)
    {
        var infoMessages = new List<string>();
        if (changedFileSource == null)
        {
            var unavailableReport = new CodeSmellReport();
            unavailableReport.AddInfo("Code review scan skipped: Changed file source unavailable.");
            return unavailableReport;
        }

        var sourceResult = await changedFileSource.LoadAsync();
        foreach (var infoMessage in sourceResult?.InfoMessages ?? [])
            infoMessages.Add(infoMessage);

        var changedFiles = sourceResult?.Files?.Where(file => file != null).ToArray() ?? [];
        if (changedFiles.Length == 0)
        {
            var emptyReport = new CodeSmellReport();
            foreach (var infoMessage in infoMessages)
                emptyReport.AddInfo(infoMessage);
            return emptyReport;
        }

        var report = AnalyzeFiles(changedFiles);
        foreach (var infoMessage in infoMessages)
            report.AddInfo(infoMessage);
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

    private static CodeReviewAnalysisContext BuildContext(IReadOnlyList<CodeReviewChangedFile> changedFiles)
    {
        var addedTestFilesByName = new HashSet<string>(
            changedFiles
                .Where(file => file.IsAdded)
                .Where(file => CodeReviewFileClassification.IsTestFilePath(file.Path))
                .Select(file => Path.GetFileName(file.Path)),
            StringComparer.OrdinalIgnoreCase);

        return new CodeReviewAnalysisContext(changedFiles, addedTestFilesByName);
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
        new DisposableNotDisposedCodeReviewCheck(),
        new MultipleEnumerationCodeReviewCheck(),
        new PublicMutableStaticStateCodeReviewCheck(),
        new UnusedPrivateMemberCodeReviewCheck(),
        new MissingXmlDocsCodeReviewCheck(),
        new MissingUnitTestsCodeReviewCheck(),
        new MissingTestsForNewPublicMethodsCodeReviewCheck(),
        new MissingTypedBindingContextCodeReviewCheck(),
        new MultipleClassesPerFileCodeReviewCheck(),
        new WarningSuppressionCodeReviewCheck(),
        new UnusedUsingRoslynCodeReviewCheck()
    ];
}
