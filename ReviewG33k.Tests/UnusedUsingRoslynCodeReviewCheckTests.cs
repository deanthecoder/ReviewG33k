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

public sealed class UnusedUsingRoslynCodeReviewCheckTests
{
    [Test]
    public void AnalyzeWhenAddedFileContainsUnusedUsingReportsHint()
    {
        const string source = """
            using System;
            using System.Text;

            public sealed class Sample
            {
                public int Compare(string left, string right) => string.Compare(left, right, StringComparison.Ordinal);
            }
            """;

        var report = AnalyzeSource("A", source, [1, 2, 3, 4, 5, 6, 7]);

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Hint));
        Assert.That(report.Findings[0].Message, Does.Contain("`using System.Text;`"));
    }

    [Test]
    public void AnalyzeWhenUsingIsRequiredDoesNotReport()
    {
        const string source = """
            using System;

            public sealed class Sample
            {
                public Type GetTypeToken() => typeof(string);
            }
            """;

        var report = AnalyzeSource("A", source, [1, 2, 3, 4, 5, 6]);

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void AnalyzeWhenUnusedUsingLineIsNotAddedForModifiedFileDoesNotReport()
    {
        const string source = """
            using System.Text;

            public sealed class Sample
            {
                public int Add(int a, int b) => a + b;
            }
            """;

        var report = AnalyzeSource("M", source, [5]);

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void AnalyzeWhenFileHasSyntaxErrorsDoesNotReportUnusedUsings()
    {
        const string source = """
            public sealed class Sample
            {
                public int Add(int a, int b) => a + ;
            }
            """;

        var report = AnalyzeSource("A", source, [1, 2, 3, 4, 5]);

        Assert.That(report.Findings, Is.Empty);
    }

    private static CodeSmellReport AnalyzeSource(string status, string source, IReadOnlyCollection<int> addedLineNumbers)
    {
        var normalizedSource = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedSource.Split('\n');
        var changedFile = new CodeReviewChangedFile(
            status,
            "Packages/CSharp.Core/Sample.cs",
            "Packages/CSharp.Core/Sample.cs",
            normalizedSource,
            lines,
            new HashSet<int>(addedLineNumbers));
        var context = new CodeReviewAnalysisContext(
            [changedFile],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var report = new CodeSmellReport();
        var check = new UnusedUsingRoslynCodeReviewCheck();
        check.Analyze(context, report);
        return report;
    }
}
