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
public sealed class CatchCodeReviewCheckTests
{
    [Test]
    public void EmptyCatchCheckWhenCatchBodyIsTrulyEmptyReportsImportant()
    {
        const string source = """
            using System;

            public sealed class Sample
            {
                public void Run()
                {
                    try
                    {
                        ThrowSomething();
                    }
                    catch (Exception)
                    {
                    }
                }

                private static void ThrowSomething() => throw new Exception();
            }
            """;

        var report = Analyze(source, new EmptyCatchCodeReviewCheck());

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Important));
        Assert.That(report.Findings[0].Message, Does.Contain("Empty catch block"));
    }

    [Test]
    public void EmptyCatchCheckWhenCatchBodyContainsOnlyCommentReportsSuggestion()
    {
        const string source = """
            using System;

            public sealed class Sample
            {
                public void Run()
                {
                    try
                    {
                        ThrowSomething();
                    }
                    catch (Exception)
                    {
                        // Intentionally ignored.
                    }
                }

                private static void ThrowSomething() => throw new Exception();
            }
            """;

        var report = Analyze(source, new EmptyCatchCodeReviewCheck());

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Suggestion));
        Assert.That(report.Findings[0].Message, Does.Contain("Empty catch block"));
    }

    [Test]
    public void SwallowingCatchCheckWhenCatchBodyContainsOnlyCommentDoesNotReport()
    {
        const string source = """
            using System;

            public sealed class Sample
            {
                public void Run()
                {
                    try
                    {
                        ThrowSomething();
                    }
                    catch (Exception)
                    {
                        // Intentionally ignored.
                    }
                }

                private static void ThrowSomething() => throw new Exception();
            }
            """;

        var report = Analyze(source, new SwallowingCatchCodeReviewCheck());

        Assert.That(report.Findings, Is.Empty);
    }

    private static CodeSmellReport Analyze(string source, ICodeReviewCheck check)
    {
        var normalizedSource = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedSource.Split('\n');
        var changedFile = new CodeReviewChangedFile(
            "A",
            "Services/Sample.cs",
            "Services/Sample.cs",
            normalizedSource,
            lines,
            new HashSet<int>(Enumerable.Range(1, lines.Length)));
        var context = new CodeReviewAnalysisContext(
            [changedFile],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var report = new CodeSmellReport();
        check.Analyze(context, report);
        return report;
    }
}
