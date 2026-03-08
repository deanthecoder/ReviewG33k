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
public sealed class MissingXmlDocsCodeReviewCheckTests
{
    [Test]
    public void AnalyzeWhenAddedPublicTypeHasNoXmlDocsReportsHint()
    {
        const string source = """
            public sealed class SmartRipOpcDocumentation
            {
            }
            """;

        var report = AnalyzeAddedFile("Services/SmartRipOpcDocumentation.cs", source);

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].RuleId, Is.EqualTo(CodeReviewRuleIds.MissingXmlDocs));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Hint));
    }

    [Test]
    public void AnalyzeWhenAddedInternalTypeHasNoXmlDocsReportsHint()
    {
        const string source = """
            internal sealed class IndexedApplication
            {
            }
            """;

        var report = AnalyzeAddedFile("Models/IndexedApplication.cs", source);

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].RuleId, Is.EqualTo(CodeReviewRuleIds.MissingXmlDocs));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Hint));
    }

    [Test]
    public void AnalyzeWhenAddedTypeIsATestFixtureDoesNotReport()
    {
        const string source = """
            [TestFixture]
            public sealed class SmartRipOpcDocumentation
            {
            }
            """;

        var report = AnalyzeAddedFile("Services/SmartRipOpcDocumentation.cs", source);

        Assert.That(report.Findings, Is.Empty);
    }

    private static CodeSmellReport AnalyzeAddedFile(string path, string source)
    {
        var normalizedSource = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedSource.Split('\n');
        var changedFile = new CodeReviewChangedFile(
            "A",
            path,
            path,
            normalizedSource,
            lines,
            new HashSet<int>(Enumerable.Range(1, lines.Length)));
        var context = new CodeReviewAnalysisContext(
            [changedFile],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var report = new CodeSmellReport();
        var check = new MissingXmlDocsCodeReviewCheck();
        check.Analyze(context, report);
        return report;
    }
}
