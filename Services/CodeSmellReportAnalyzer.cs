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
        new PrivatePropertyShouldBeFieldCodeReviewCheck(),
        new PrivateFieldCanBeReadonlyCodeReviewCheck(),
        new MethodParameterCountCodeReviewCheck(),
        new GenericTypeNameSuffixCodeReviewCheck(),
        new IfElseBraceConsistencyCodeReviewCheck(),
        new ConstructorTooLongCodeReviewCheck(),
        new ThreadSleepCodeReviewCheck(),
        new ThrowExCodeReviewCheck(),
        new MethodCanBeStaticCodeReviewCheck(),
        new UnnecessaryCastCodeReviewCheck(),
        new UnobservedTaskResultCodeReviewCheck(),
        new DisposableNotDisposedCodeReviewCheck(),
        new MultipleEnumerationCodeReviewCheck(),
        new PublicMutableStaticStateCodeReviewCheck(),
        new UnusedPrivateMemberCodeReviewCheck(),
        new MissingXmlDocsCodeReviewCheck(),
        new MissingUnitTestsCodeReviewCheck(),
        new MissingTestsForNewPublicMethodsCodeReviewCheck(),
        new WarningSuppressionCodeReviewCheck(),
        new UnusedUsingRoslynCodeReviewCheck()
    ];
}
