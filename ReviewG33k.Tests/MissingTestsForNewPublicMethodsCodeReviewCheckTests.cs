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

public sealed class MissingTestsForNewPublicMethodsCodeReviewCheckTests
{
    [Test]
    public void AnalyzeWhenNewPublicMethodAddedAndNoTestsChangedReportsSuggestion()
    {
        const string source = """
            public sealed class OrderService
            {
                public void Submit()
                {
                }
            }
            """;

        var report = Analyze(source);

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Suggestion));
        Assert.That(report.Findings[0].Message, Does.Contain("Submit"));
    }

    [Test]
    public void AnalyzeWhenMatchingTestFileChangedDoesNotReport()
    {
        const string source = """
            public sealed class OrderService
            {
                public void Submit()
                {
                }
            }
            """;

        const string testSource = """
            public sealed class OrderServiceTests
            {
                public void Submit_WhenCalled_DoesSomething()
                {
                }
            }
            """;

        var report = Analyze(source, ("Tests/OrderServiceTests.cs", "M", testSource));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void AnalyzeWhenUnrelatedTestFileChangedReportsHint()
    {
        const string source = """
            public sealed class OrderService
            {
                public void Submit()
                {
                }
            }
            """;

        const string testSource = """
            public sealed class CustomerServiceTests
            {
                public void Lookup_WhenCalled_ReturnsValue()
                {
                }
            }
            """;

        var report = Analyze(source, ("Tests/CustomerServiceTests.cs", "M", testSource));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Hint));
        Assert.That(report.Findings[0].Message, Does.Contain("OrderServiceTests.cs"));
    }

    [Test]
    public void AnalyzeWhenMethodIsPrivateDoesNotReport()
    {
        const string source = """
            public sealed class OrderService
            {
                private void Submit()
                {
                }
            }
            """;

        var report = Analyze(source);

        Assert.That(report.Findings, Is.Empty);
    }

    private static CodeSmellReport Analyze(string productionSource, params (string Path, string Status, string Source)[] additionalFiles)
    {
        var files = new List<CodeReviewChangedFile>
        {
            CreateChangedFile("Services/OrderService.cs", "A", productionSource)
        };

        foreach (var (path, status, source) in additionalFiles ?? [])
            files.Add(CreateChangedFile(path, status, source));

        var addedTestFilesByName = new HashSet<string>(
            files.Where(file => file.IsAdded && file.Path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetFileName(file.Path)),
            StringComparer.OrdinalIgnoreCase);

        var context = new CodeReviewAnalysisContext(files, addedTestFilesByName);

        var report = new CodeSmellReport();
        var check = new MissingTestsForNewPublicMethodsCodeReviewCheck();
        check.Analyze(context, report);
        return report;
    }

    private static CodeReviewChangedFile CreateChangedFile(string path, string status, string source)
    {
        var normalizedSource = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedSource.Split('\n');
        return new CodeReviewChangedFile(
            status,
            path,
            path,
            normalizedSource,
            lines,
            new HashSet<int>(Enumerable.Range(1, lines.Length)));
    }
}
