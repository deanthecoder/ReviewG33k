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

public sealed class RoslynStyleCodeReviewChecksTests
{
    [Test]
    public void AutoPropertyCheckWhenPublicWrapperPropertyIsAddedReportsHint()
    {
        const string source = """
            public sealed class Sample
            {
                private string name;

                public string Name
                {
                    get { return this.name; }
                    set { this.name = value; }
                }
            }
            """;

        var report = AnalyzeSource(new PropertyCanBeAutoPropertyCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Hint));
        Assert.That(report.Findings[0].Message, Does.Contain("Name"));
    }

    [Test]
    public void PrivatePropertyFieldCheckWhenSimplePrivateAutoPropertyIsAddedReportsHint()
    {
        const string source = """
            public sealed class Sample
            {
                private int Count { get; set; }
            }
            """;

        var report = AnalyzeSource(new PrivatePropertyShouldBeFieldCodeReviewCheck(), "A", source, Enumerable.Range(1, 5));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Message, Does.Contain("Count"));
    }

    [Test]
    public void MethodParameterCountCheckWhenMethodHasSixParametersReportsHint()
    {
        const string source = """
            public sealed class Sample
            {
                public void DoWork(int a, int b, int c, int d, int e, int f)
                {
                }
            }
            """;

        var report = AnalyzeSource(new MethodParameterCountCodeReviewCheck(), "A", source, Enumerable.Range(1, 7));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Message, Does.Contain("6 parameters"));
    }

    [Test]
    public void MethodParameterCountCheckWhenConstructorHasSixParametersReportsHint()
    {
        const string source = """
            public sealed class Sample
            {
                public Sample(int a, int b, int c, int d, int e, int f)
                {
                }
            }
            """;

        var report = AnalyzeSource(new MethodParameterCountCodeReviewCheck(), "A", source, Enumerable.Range(1, 7));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Message, Does.Contain("Constructor `Sample`"));
        Assert.That(report.Findings[0].Message, Does.Contain("6 parameters"));
    }

    [Test]
    public void GenericTypeNameSuffixCheckWhenClassNameEndsWithHelperReportsHint()
    {
        const string source = """
            public sealed class TextHelper
            {
            }
            """;

        var report = AnalyzeSource(new GenericTypeNameSuffixCodeReviewCheck(), "A", source, Enumerable.Range(1, 3));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Message, Does.Contain("TextHelper"));
    }

    [Test]
    public void IfElseBraceConsistencyCheckWhenOnlyOneBranchUsesBracesReportsSuggestion()
    {
        const string source = """
            public sealed class Sample
            {
                public int Run(bool flag)
                {
                    if (flag)
                    {
                        return 1;
                    }
                    else
                        return 0;
                }
            }
            """;

        var report = AnalyzeSource(new IfElseBraceConsistencyCodeReviewCheck(), "A", source, Enumerable.Range(1, 12));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Suggestion));
    }

    [Test]
    public void IfElseBraceConsistencyCheckWhenElseIfChainDoesNotReport()
    {
        const string source = """
            public sealed class Sample
            {
                public int Run(int value)
                {
                    if (value == 0)
                    {
                        return 0;
                    }
                    else if (value == 1)
                        return 1;
                    else
                        return -1;
                }
            }
            """;

        var report = AnalyzeSource(new IfElseBraceConsistencyCodeReviewCheck(), "A", source, Enumerable.Range(1, 15));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void ConstructorTooLongCheckWhenConstructorHasFifteenCodeLinesReportsSuggestion()
    {
        const string source = """
            public sealed class Sample
            {
                public Sample()
                {
                    var a1 = 1;
                    var a2 = 2;
                    var a3 = 3;
                    var a4 = 4;
                    var a5 = 5;
                    var a6 = 6;
                    var a7 = 7;
                    var a8 = 8;
                    var a9 = 9;
                    var a10 = 10;
                    var a11 = 11;
                    var a12 = 12;
                    var a13 = 13;
                    var a14 = 14;
                    var a15 = 15;
                }
            }
            """;

        var report = AnalyzeSource(new ConstructorTooLongCodeReviewCheck(), "A", source, Enumerable.Range(1, 22));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Suggestion));
        Assert.That(report.Findings[0].Message, Does.Contain("15 code lines"));
    }

    [Test]
    public void ConstructorTooLongCheckWhenConstructorHasFourteenCodeLinesDoesNotReport()
    {
        const string source = """
            public sealed class Sample
            {
                public Sample()
                {
                    var a1 = 1;
                    var a2 = 2;
                    var a3 = 3;
                    var a4 = 4;
                    var a5 = 5;
                    var a6 = 6;
                    var a7 = 7;
                    var a8 = 8;
                    var a9 = 9;
                    var a10 = 10;
                    var a11 = 11;
                    var a12 = 12;
                    var a13 = 13;
                    var a14 = 14;
                }
            }
            """;

        var report = AnalyzeSource(new ConstructorTooLongCodeReviewCheck(), "A", source, Enumerable.Range(1, 21));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void ThreadSleepCheckWhenThreadSleepIsUsedReportsSuggestion()
    {
        const string source = """
            using System.Threading;

            public sealed class Sample
            {
                public void Run()
                {
                    Thread.Sleep(20);
                }
            }
            """;

        var report = AnalyzeSource(new ThreadSleepCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Suggestion));
        Assert.That(report.Findings[0].Message, Does.Contain("Thread.Sleep"));
    }

    [Test]
    public void ThreadSleepCheckWhenUsingStaticThreadReportsSuggestion()
    {
        const string source = """
            using static System.Threading.Thread;

            public sealed class Sample
            {
                public void Run()
                {
                    Sleep(20);
                }
            }
            """;

        var report = AnalyzeSource(new ThreadSleepCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
    }

    [Test]
    public void ThreadSleepCheckWhenLocalSleepMethodIsUsedDoesNotReport()
    {
        const string source = """
            public sealed class Sample
            {
                public void Run()
                {
                    Sleep(20);
                }

                private static void Sleep(int milliseconds)
                {
                }
            }
            """;

        var report = AnalyzeSource(new ThreadSleepCodeReviewCheck(), "A", source, Enumerable.Range(1, 13));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void ThrowExCheckWhenThrowExUsedInCatchReportsImportant()
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
                    catch (Exception ex)
                    {
                        throw ex;
                    }
                }

                private static void ThrowSomething() => throw new Exception();
            }
            """;

        var report = AnalyzeSource(new ThrowExCodeReviewCheck(), "A", source, Enumerable.Range(1, 19));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Important));
        Assert.That(report.Findings[0].Message, Does.Contain("throw ex"));
    }

    [Test]
    public void ThrowExCheckWhenBareThrowUsedDoesNotReport()
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
                        throw;
                    }
                }

                private static void ThrowSomething() => throw new Exception();
            }
            """;

        var report = AnalyzeSource(new ThrowExCodeReviewCheck(), "A", source, Enumerable.Range(1, 19));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void WarningSuppressionCheckWhenPragmaDisableAddedReportsImportant()
    {
        const string source = """
            public sealed class Sample
            {
                #pragma warning disable CS0168
                public void Run()
                {
                    int unused;
                }
                #pragma warning restore CS0168
            }
            """;

        var report = AnalyzeSource(new WarningSuppressionCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Important));
        Assert.That(report.Findings[0].Message, Does.Contain("#pragma warning disable"));
    }

    [Test]
    public void WarningSuppressionCheckWhenSuppressMessageAttributeAddedReportsImportant()
    {
        const string source = """
            using System.Diagnostics.CodeAnalysis;

            [SuppressMessage("Style", "IDE0001")]
            public sealed class Sample
            {
                public void Run()
                {
                }
            }
            """;

        var report = AnalyzeSource(new WarningSuppressionCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Important));
        Assert.That(report.Findings[0].Message, Does.Contain("SuppressMessage"));
    }

    [Test]
    public void AsyncVoidCheckWhenAsyncVoidMethodIsAddedReportsImportant()
    {
        const string source = """
            public sealed class Sample
            {
                public async void RunAsyncThing()
                {
                    await Task.Delay(1);
                }
            }
            """;

        var report = AnalyzeSource(new AsyncVoidCodeReviewCheck(), "A", source, Enumerable.Range(1, 8));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Important));
    }

    [Test]
    public void AsyncVoidCheckWhenMethodLooksLikeEventHandlerDoesNotReport()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;

            public sealed class Sample
            {
                public async void OnClick(object? sender, EventArgs e)
                {
                    await Task.Delay(1);
                }
            }
            """;

        var report = AnalyzeSource(new AsyncVoidCodeReviewCheck(), "A", source, Enumerable.Range(1, 11));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void TaskRunAsyncCheckWhenTaskRunUsesAsyncLambdaReportsSuggestion()
    {
        const string source = """
            using System.Threading.Tasks;

            public sealed class Sample
            {
                public Task RunAsync()
                {
                    return Task.Run(async () =>
                    {
                        await Task.Delay(1);
                    });
                }
            }
            """;

        var report = AnalyzeSource(new TaskRunAsyncCodeReviewCheck(), "A", source, Enumerable.Range(1, 13));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Suggestion));
    }

    [Test]
    public void TaskRunAsyncCheckWhenTaskRunUsesCpuBoundLambdaDoesNotReport()
    {
        const string source = """
            using System.Threading.Tasks;

            public sealed class Sample
            {
                public Task<int> RunAsync()
                {
                    return Task.Run(() => 42);
                }
            }
            """;

        var report = AnalyzeSource(new TaskRunAsyncCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void TaskRunAsyncCheckWhenFileHasUnrelatedMissingTypeStillReports()
    {
        const string source = """
            using System.Threading.Tasks;

            public sealed class Sample
            {
                public Task RunAsync()
                {
                    MissingType value = null;
                    return Task.Run(async () =>
                    {
                        await Task.Delay(1);
                    });
                }
            }
            """;

        var report = AnalyzeSource(new TaskRunAsyncCodeReviewCheck(), "A", source, Enumerable.Range(1, 14));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Message, Does.Contain("Task.Run"));
    }

    [Test]
    public void MethodCanBeStaticCheckWhenMethodDoesNotUseInstanceStateReportsHint()
    {
        const string source = """
            using System;

            public sealed class Sample
            {
                public int SumAbs(int left, int right)
                {
                    return Math.Abs(left) + Math.Abs(right);
                }
            }
            """;

        var report = AnalyzeSource(new MethodCanBeStaticCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Hint));
        Assert.That(report.Findings[0].Message, Does.Contain("`SumAbs`"));
    }

    [Test]
    public void MethodCanBeStaticCheckWhenMethodUsesInstanceFieldDoesNotReport()
    {
        const string source = """
            public sealed class Sample
            {
                private readonly int value = 3;

                public int Calculate(int input)
                {
                    return input + this.value;
                }
            }
            """;

        var report = AnalyzeSource(new MethodCanBeStaticCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void MethodCanBeStaticCheckWhenMethodIsAlreadyStaticDoesNotReport()
    {
        const string source = """
            public sealed class Sample
            {
                public static int Sum(int left, int right)
                {
                    return left + right;
                }
            }
            """;

        var report = AnalyzeSource(new MethodCanBeStaticCodeReviewCheck(), "A", source, Enumerable.Range(1, 8));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void MethodCanBeStaticCheckWhenFileIsTestFileDoesNotReport()
    {
        const string source = """
            using System;

            public sealed class SampleTests
            {
                [Test]
                public int SumAbs(int left, int right)
                {
                    return Math.Abs(left) + Math.Abs(right);
                }
            }
            """;

        var report = AnalyzeSource(new MethodCanBeStaticCodeReviewCheck(), "A", source, Enumerable.Range(1, 12), "Tests/SampleTests.cs");

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void UnobservedTaskResultCheckWhenTaskCallIsIgnoredReportsSuggestion()
    {
        const string source = """
            using System.Threading.Tasks;

            public sealed class Sample
            {
                public async Task RunAsync()
                {
                    FireAndForgetAsync();
                    await Task.Delay(1);
                }

                private static Task FireAndForgetAsync() => Task.CompletedTask;
            }
            """;

        var report = AnalyzeSource(new UnobservedTaskResultCodeReviewCheck(), "A", source, Enumerable.Range(1, 15));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Suggestion));
        Assert.That(report.Findings[0].Message, Does.Contain("`FireAndForgetAsync`"));
    }

    [Test]
    public void UnobservedTaskResultCheckWhenTaskIsAwaitedDoesNotReport()
    {
        const string source = """
            using System.Threading.Tasks;

            public sealed class Sample
            {
                public async Task RunAsync()
                {
                    await FireAndForgetAsync();
                }

                private static Task FireAndForgetAsync() => Task.CompletedTask;
            }
            """;

        var report = AnalyzeSource(new UnobservedTaskResultCodeReviewCheck(), "A", source, Enumerable.Range(1, 13));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void UnobservedTaskResultCheckWhenValueTaskCallIsIgnoredReportsSuggestion()
    {
        const string source = """
            using System.Threading.Tasks;

            public sealed class Sample
            {
                public void Run()
                {
                    DoAsync();
                }

                private static ValueTask DoAsync() => ValueTask.CompletedTask;
            }
            """;

        var report = AnalyzeSource(new UnobservedTaskResultCodeReviewCheck(), "A", source, Enumerable.Range(1, 13));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Message, Does.Contain("`DoAsync`"));
    }

    [Test]
    public void DisposableNotDisposedCheckWhenDisposableCreatedWithoutUsingReportsSuggestion()
    {
        const string source = """
            using System.IO;

            public sealed class Sample
            {
                public void Run()
                {
                    var stream = new MemoryStream();
                }
            }
            """;

        var report = AnalyzeSource(new DisposableNotDisposedCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Suggestion));
        Assert.That(report.Findings[0].Message, Does.Contain("`MemoryStream`"));
    }

    [Test]
    public void DisposableNotDisposedCheckWhenUsingDeclarationIsUsedDoesNotReport()
    {
        const string source = """
            using System.IO;

            public sealed class Sample
            {
                public void Run()
                {
                    using var stream = new MemoryStream();
                }
            }
            """;

        var report = AnalyzeSource(new DisposableNotDisposedCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void DisposableNotDisposedCheckWhenDisposeCalledLaterDoesNotReport()
    {
        const string source = """
            using System.IO;

            public sealed class Sample
            {
                public void Run()
                {
                    var stream = new MemoryStream();
                    stream.Dispose();
                }
            }
            """;

        var report = AnalyzeSource(new DisposableNotDisposedCodeReviewCheck(), "A", source, Enumerable.Range(1, 11));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void MultipleEnumerationCheckWhenIEnumerableIsEnumeratedTwiceReportsSuggestion()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public sealed class Sample
            {
                public int Run(IEnumerable<int> values)
                {
                    var first = values.First();
                    var count = values.Count();
                    return first + count;
                }
            }
            """;

        var report = AnalyzeSource(new MultipleEnumerationCodeReviewCheck(), "A", source, Enumerable.Range(1, 14));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Suggestion));
        Assert.That(report.Findings[0].Message, Does.Contain("`values`"));
    }

    [Test]
    public void MultipleEnumerationCheckWhenCollectionTypeIsCheapDoesNotReport()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public sealed class Sample
            {
                public int Run(List<int> values)
                {
                    var first = values.First();
                    var count = values.Count();
                    return first + count;
                }
            }
            """;

        var report = AnalyzeSource(new MultipleEnumerationCodeReviewCheck(), "A", source, Enumerable.Range(1, 14));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void PublicMutableStaticStateCheckWhenPublicStaticFieldIsMutableReportsImportant()
    {
        const string source = """
            public sealed class Sample
            {
                public static int Counter = 0;
            }
            """;

        var report = AnalyzeSource(new PublicMutableStaticStateCodeReviewCheck(), "A", source, Enumerable.Range(1, 5));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Important));
        Assert.That(report.Findings[0].Message, Does.Contain("`Counter`"));
    }

    [Test]
    public void PublicMutableStaticStateCheckWhenPublicStaticPropertyHasSetterReportsImportant()
    {
        const string source = """
            public sealed class Sample
            {
                public static string Name { get; set; }
            }
            """;

        var report = AnalyzeSource(new PublicMutableStaticStateCodeReviewCheck(), "A", source, Enumerable.Range(1, 5));

        Assert.That(report.Findings, Has.Count.EqualTo(1));
        Assert.That(report.Findings[0].Severity, Is.EqualTo(CodeReviewFindingSeverity.Important));
        Assert.That(report.Findings[0].Message, Does.Contain("`Name`"));
    }

    [Test]
    public void PublicMutableStaticStateCheckWhenFieldReadonlyAndPropertyPrivateSetDoesNotReport()
    {
        const string source = """
            public sealed class Sample
            {
                public static readonly int Counter = 0;
                public static string Name { get; private set; }
            }
            """;

        var report = AnalyzeSource(new PublicMutableStaticStateCodeReviewCheck(), "A", source, Enumerable.Range(1, 6));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void UnusedPrivateMemberCheckWhenPrivateFieldMethodAndPropertyAreUnusedReportsHint()
    {
        const string source = """
            public sealed class Sample
            {
                private readonly int count = 1;

                private int Value { get; set; }

                private void DoWork()
                {
                }
            }
            """;

        var report = AnalyzeSource(new UnusedPrivateMemberCodeReviewCheck(), "A", source, Enumerable.Range(1, 12));

        Assert.That(report.Findings, Has.Count.EqualTo(3));
        Assert.That(report.Findings.All(finding => finding.Severity == CodeReviewFindingSeverity.Hint), Is.True);
        Assert.That(report.Findings.Any(finding => finding.Message.Contains("field `count`", StringComparison.Ordinal)), Is.True);
        Assert.That(report.Findings.Any(finding => finding.Message.Contains("property `Value`", StringComparison.Ordinal)), Is.True);
        Assert.That(report.Findings.Any(finding => finding.Message.Contains("method `DoWork`", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void UnusedPrivateMemberCheckWhenPrivateMembersAreUsedDoesNotReport()
    {
        const string source = """
            public sealed class Sample
            {
                private int count;

                private int Value { get; set; }

                public int Run()
                {
                    this.count++;
                    this.Value = this.count;
                    this.DoWork();
                    return this.Value;
                }

                private void DoWork()
                {
                }
            }
            """;

        var report = AnalyzeSource(new UnusedPrivateMemberCodeReviewCheck(), "A", source, Enumerable.Range(1, 20));

        Assert.That(report.Findings, Is.Empty);
    }

    [Test]
    public void UnusedPrivateMemberCheckWhenContainingTypeIsPartialDoesNotReport()
    {
        const string source = """
            public partial class Sample
            {
                private int count;
                private int Value { get; set; }
                private void DoWork()
                {
                }
            }
            """;

        var report = AnalyzeSource(new UnusedPrivateMemberCodeReviewCheck(), "A", source, Enumerable.Range(1, 10));

        Assert.That(report.Findings, Is.Empty);
    }

    private static CodeSmellReport AnalyzeSource(ICodeReviewCheck check, string status, string source, IEnumerable<int> addedLines, string path = "Packages/CSharp.Core/Sample.cs")
    {
        var normalizedSource = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalizedSource.Split('\n');
        var changedFile = new CodeReviewChangedFile(
            status,
            path,
            path,
            normalizedSource,
            lines,
            new HashSet<int>(addedLines ?? []));
        var context = new CodeReviewAnalysisContext(
            [changedFile],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var report = new CodeSmellReport();
        check.Analyze(context, report);
        return report;
    }
}
