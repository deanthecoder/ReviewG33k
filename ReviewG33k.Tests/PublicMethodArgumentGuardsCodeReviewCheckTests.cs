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

public sealed class PublicMethodArgumentGuardsCodeReviewCheckTests
{
    [Test]
    public void AnalyzeWhenUsingCombinedNullCheckDoesNotReportValueOrParameter()
    {
        const string source = """
            using System;
            using System.Globalization;

            public sealed class EnumToBoolConverter
            {
                public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
                {
                    if (value == null || parameter == null)
                        return false;

                    if (value is not Enum enumValue || parameter is not string enumString)
                        return false;

                    return enumValue.ToString().Equals(enumString, StringComparison.InvariantCultureIgnoreCase);
                }
            }
            """;

        var report = AnalyzeSource(source);

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void AnalyzeWhenUsedParameterHasNoGuardReportsHint()
    {
        const string source = """
            public sealed class Sample
            {
                public void Convert(object value, object parameter)
                {
                    if (value == null)
                        return;

                    _ = parameter.ToString();
                }
            }
            """;

        var report = AnalyzeSource(source);

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Hint));
        Assert.That(report.Findings[0].Message, Does.Contain("parameter"));
    }

    private static CodeSmellReport AnalyzeSource(string source)
    {
        var normalizedSource = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedSource.Split('\n');
        var addedLineNumbers = new HashSet<int>(Enumerable.Range(1, lines.Length));
        var changedFile = new CodeReviewChangedFile(
            "M",
            "Packages/CSharp.Avalonia/Converters/EnumToBoolConverter.cs",
            "Packages/CSharp.Avalonia/Converters/EnumToBoolConverter.cs",
            normalizedSource,
            lines,
            addedLineNumbers);
        var context = new CodeReviewAnalysisContext(
            [changedFile],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var report = new CodeSmellReport();
        var check = new PublicMethodArgumentGuardsCodeReviewCheck();
        check.Analyze(context, report);
        return report;
    }
}
