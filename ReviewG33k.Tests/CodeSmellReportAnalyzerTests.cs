// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using ReviewG33k.Services;
using ReviewG33k.Services.Checks;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class CodeSmellReportAnalyzerTests
{
    [Test]
    public void AnalyzeFilesWhenChangedFilesIsNullThrowsArgumentNullException()
    {
        var analyzer = new CodeSmellReportAnalyzer(new GitCommandRunner());

        Assert.Throws<ArgumentNullException>(() => analyzer.AnalyzeFiles(null));
    }

    [Test]
    public void AnalyzeFilesWhenChangedFilesIsEmptyReturnsReportWithoutFindings()
    {
        var analyzer = new CodeSmellReportAnalyzer(new GitCommandRunner());

        var report = analyzer.AnalyzeFiles([]);

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void ChecksHaveExpectedScopes()
    {
        var analyzer = new CodeSmellReportAnalyzer(new GitCommandRunner());

        var missingXmlDocsCheck = analyzer.Checks.OfType<MissingXmlDocsCodeReviewCheck>().Single();
        var missingUnitTestsCheck = analyzer.Checks.OfType<MissingUnitTestsCodeReviewCheck>().Single();
        var missingTestsForPublicMethodsCheck = analyzer.Checks.OfType<MissingTestsForNewPublicMethodsCodeReviewCheck>().Single();
        var missingReadmeCheck = analyzer.Checks.OfType<MissingReadmeForNewProjectCodeReviewCheck>().Single();
        var missingTypedBindingContextCheck = analyzer.Checks.OfType<MissingTypedBindingContextCodeReviewCheck>().Single();
        var missingBlankLineCheck = analyzer.Checks.OfType<MissingBlankLineBetweenMethodsCodeReviewCheck>().Single();
        var stringConcatCheck = analyzer.Checks.OfType<StringConcatenationToSameTargetCodeReviewCheck>().Single();
        var constructorEventSubscriptionCheck = analyzer.Checks.OfType<ConstructorEventSubscriptionLifecycleCodeReviewCheck>().Single();
        var resxMissingLocaleKeysCheck = analyzer.Checks.OfType<ResxMissingLocaleKeysCodeReviewCheck>().Single();
        var resxUnexpectedExtraKeysCheck = analyzer.Checks.OfType<ResxUnexpectedExtraKeysCodeReviewCheck>().Single();
        var localVariableCanBeConstCheck = analyzer.Checks.OfType<LocalVariableCanBeConstCodeReviewCheck>().Single();
        var unusedLocalVariableCheck = analyzer.Checks.OfType<UnusedLocalVariableCodeReviewCheck>().Single();

        Assert.That(missingXmlDocsCheck.Scope, Is.EqualTo(CodeReviewCheckScope.AddedLinesOnly));
        Assert.That(missingUnitTestsCheck.Scope, Is.EqualTo(CodeReviewCheckScope.AddedLinesOnly));
        Assert.That(missingTestsForPublicMethodsCheck.Scope, Is.EqualTo(CodeReviewCheckScope.AddedLinesOnly));
        Assert.That(missingReadmeCheck.Scope, Is.EqualTo(CodeReviewCheckScope.AddedLinesOnly));
        Assert.That(missingTypedBindingContextCheck.Scope, Is.EqualTo(CodeReviewCheckScope.AddedLinesOnly));
        Assert.That(missingBlankLineCheck.Scope, Is.EqualTo(CodeReviewCheckScope.AddedLinesOnly));
        Assert.That(stringConcatCheck.Scope, Is.EqualTo(CodeReviewCheckScope.AddedLinesOnly));
        Assert.That(constructorEventSubscriptionCheck.Scope, Is.EqualTo(CodeReviewCheckScope.AddedLinesOnly));
        Assert.That(resxMissingLocaleKeysCheck.Scope, Is.EqualTo(CodeReviewCheckScope.ChangedFileSet));
        Assert.That(resxUnexpectedExtraKeysCheck.Scope, Is.EqualTo(CodeReviewCheckScope.ChangedFileSet));
        Assert.That(localVariableCanBeConstCheck.Scope, Is.EqualTo(CodeReviewCheckScope.WholeChangedFile));
        Assert.That(unusedLocalVariableCheck.Scope, Is.EqualTo(CodeReviewCheckScope.WholeChangedFile));
    }
}
