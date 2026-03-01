using DTC.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ReviewG33k.Services;
using ReviewG33k.Services.Checks;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class FixableCodeReviewChecksTests
{
    [Test]
    public void UnusedUsingCheckTryFixRemovesTheLineAndDoesNotLeaveTripleBlankLines()
    {
        using var tempFile = new TempFile(".cs");
        var source = """
            using System;
            using System.Text;

            public sealed class Sample
            {
                public string X() => new StringBuilder().ToString();
            }
            """;

        File.WriteAllText(tempFile.FullName, source);

        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Hint,
            CodeReviewRuleIds.UnusedUsingsRoslyn,
            "Sample.cs",
            1,
            "Unused using");

        var check = new UnusedUsingRoslynCodeReviewCheck();
        var success = check.TryFix(finding, tempFile.FullName, out var message);

        Assert.That(success, Is.True);
        Assert.That(message, Is.Not.Empty);

        var updated = File.ReadAllText(tempFile.FullName);
        Assert.That(updated, Does.Not.Contain("using System;"));
        Assert.That(updated, Does.Contain("using System.Text;"));
        Assert.That(updated, Does.Not.Contain("\n\n\n"));
    }

    [Test]
    public void ThrowExCheckTryFixReplacesWithThrow()
    {
        using var tempFile = new TempFile(".cs");
        var source = """
            using System;

            public sealed class Sample
            {
                public void Go()
                {
                    try
                    {
                        throw new Exception("x");
                    }
                    catch (Exception ex)
                    {
                        throw ex;
                    }
                }
            }
            """;

        File.WriteAllText(tempFile.FullName, source);

        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Important,
            CodeReviewRuleIds.ThrowExInCatch,
            "Sample.cs",
            13,
            "Use throw; instead of throw ex;");

        var check = new ThrowExCodeReviewCheck();
        var success = check.TryFix(finding, tempFile.FullName, out var message);

        Assert.That(success, Is.True);
        Assert.That(message, Is.Not.Empty);

        var updated = File.ReadAllText(tempFile.FullName);
        Assert.That(updated, Does.Not.Contain("throw ex;"));
        Assert.That(updated, Does.Contain("throw;"));
        Assert.That(updated, Does.Not.Contain("\n\n\n"));
    }

    [Test]
    public void IfElseBraceConsistencyCheckTryFixRemovesUnnecessaryBraceWhenSafe()
    {
        using var tempFile = new TempFile(".cs");
        var source = """
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

        File.WriteAllText(tempFile.FullName, source);

        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Suggestion,
            CodeReviewRuleIds.IfElseBraceConsistency,
            "Sample.cs",
            5,
            "If/else branches should both use braces when either branch uses braces.");

        var check = new IfElseBraceConsistencyCodeReviewCheck();
        var success = check.TryFix(finding, tempFile.FullName, out var message);

        Assert.That(success, Is.True);
        Assert.That(message, Is.Not.Empty);

        var updated = File.ReadAllText(tempFile.FullName);
        Assert.That(updated, Does.Not.Contain("\n\n\n"));

        var root = CSharpSyntaxTree.ParseText(updated).GetCompilationUnitRoot();
        var ifStatement = root.DescendantNodes().OfType<IfStatementSyntax>().Single();
        Assert.That(ifStatement.Statement, Is.Not.TypeOf<BlockSyntax>());
        Assert.That(ifStatement.Else.Statement, Is.Not.TypeOf<BlockSyntax>());
    }

    [Test]
    public void IfElseBraceConsistencyCheckTryFixAddsMissingBracesWhenRemovalIsUnsafe()
    {
        using var tempFile = new TempFile(".cs");
        var source = """
            public sealed class Sample
            {
                public int Run(bool flag)
                {
                    if (flag)
                    {
                        var x = 1;
                        return x;
                    }
                    else
                        return 0;
                }
            }
            """;

        File.WriteAllText(tempFile.FullName, source);

        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Suggestion,
            CodeReviewRuleIds.IfElseBraceConsistency,
            "Sample.cs",
            5,
            "If/else branches should both use braces when either branch uses braces.");

        var check = new IfElseBraceConsistencyCodeReviewCheck();
        var success = check.TryFix(finding, tempFile.FullName, out var message);

        Assert.That(success, Is.True);
        Assert.That(message, Is.Not.Empty);

        var updated = File.ReadAllText(tempFile.FullName);
        Assert.That(updated, Does.Not.Contain("\n\n\n"));
        var normalized = updated.Replace("\r\n", "\n");
        Assert.That(normalized, Does.Not.Contain("else\n\n{"));
        Assert.That(normalized, Does.Match(@"else\s*\n\s*\{\s*\n\s*return 0;\s*\n\s*\}"));

        var root = CSharpSyntaxTree.ParseText(updated).GetCompilationUnitRoot();
        var ifStatement = root.DescendantNodes().OfType<IfStatementSyntax>().Single();
        Assert.That(ifStatement.Statement, Is.TypeOf<BlockSyntax>());
        Assert.That(ifStatement.Else.Statement, Is.TypeOf<BlockSyntax>());
    }

    [Test]
    public void IfElseUnnecessaryBracesCheckTryFixRemovesBracesFromBothBranches()
    {
        using var tempFile = new TempFile(".cs");
        var source = """
            public sealed class Sample
            {
                public int Run(bool flag)
                {
                    if (flag)
                    {
                        return 1;
                    }
                    else
                    {
                        return 0;
                    }
                }
            }
            """;

        File.WriteAllText(tempFile.FullName, source);

        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Hint,
            CodeReviewRuleIds.IfElseUnnecessaryBraces,
            "Sample.cs",
            5,
            "If/else braces are unnecessary when each branch contains a single simple statement.");

        var check = new IfElseUnnecessaryBracesCodeReviewCheck();
        var success = check.TryFix(finding, tempFile.FullName, out var message);

        Assert.That(success, Is.True);
        Assert.That(message, Is.Not.Empty);

        var updated = File.ReadAllText(tempFile.FullName);
        Assert.That(updated, Does.Not.Contain("\n\n\n"));

        var root = CSharpSyntaxTree.ParseText(updated).GetCompilationUnitRoot();
        var ifStatement = root.DescendantNodes().OfType<IfStatementSyntax>().Single();
        Assert.That(ifStatement.Statement, Is.Not.TypeOf<BlockSyntax>());
        Assert.That(ifStatement.Else.Statement, Is.Not.TypeOf<BlockSyntax>());
    }

    [Test]
    public void WarningSuppressionCheckTryFixWhenPragmaRemovesTheLine()
    {
        using var tempFile = new TempFile(".cs");
        var source = """
            using System;

            #pragma warning disable CS0168

            public sealed class Sample
            {
            }
            """;

        File.WriteAllText(tempFile.FullName, source);

        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Important,
            CodeReviewRuleIds.WarningSuppression,
            "Sample.cs",
            3,
            "Suppression added");

        var check = new WarningSuppressionCodeReviewCheck();
        var success = check.TryFix(finding, tempFile.FullName, out var message);

        Assert.That(success, Is.True);
        Assert.That(message, Is.Not.Empty);

        var updated = File.ReadAllText(tempFile.FullName);
        Assert.That(updated, Does.Not.Contain("#pragma warning disable"));
        Assert.That(updated, Does.Not.Contain("\n\n\n"));
    }

    [Test]
    public void WarningSuppressionCheckTryFixWhenSuppressMessageRemovesTheAttribute()
    {
        using var tempFile = new TempFile(".cs");
        var source = """
            using System;
            using System.Diagnostics.CodeAnalysis;

            [SuppressMessage("Usage", "CA1801:Review unused parameters", Justification = "Test.")]
            public sealed class Sample
            {
            }
            """;

        File.WriteAllText(tempFile.FullName, source);

        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Important,
            CodeReviewRuleIds.WarningSuppression,
            "Sample.cs",
            4,
            "Suppression added");

        var check = new WarningSuppressionCodeReviewCheck();
        var success = check.TryFix(finding, tempFile.FullName, out var message);

        Assert.That(success, Is.True);
        Assert.That(message, Is.Not.Empty);

        var updated = File.ReadAllText(tempFile.FullName);
        Assert.That(updated, Does.Not.Contain("SuppressMessage"));
        Assert.That(updated, Does.Not.Contain("\n\n\n"));
    }

    [Test]
    public void MissingBlankLineBetweenMethodsCheckTryFixInsertsBlankLineBeforeSecondMethod()
    {
        using var tempFile = new TempFile(".cs");
        var source = """
            public sealed class Sample
            {
                private int Foo()
                {
                    return 1;
                }
                private int Bar()
                {
                    return 2;
                }
            }
            """;

        File.WriteAllText(tempFile.FullName, source);

        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Hint,
            CodeReviewRuleIds.MissingBlankLineBetweenMethods,
            "Sample.cs",
            7,
            "Add a blank line between methods.");

        var check = new MissingBlankLineBetweenMethodsCodeReviewCheck();
        var success = check.TryFix(finding, tempFile.FullName, out var message);

        Assert.That(success, Is.True);
        Assert.That(message, Is.Not.Empty);

        var updated = File.ReadAllText(tempFile.FullName);
        Assert.That(updated, Does.Contain("}\n\n    private int Bar()"));
    }

    [Test]
    public void UnnecessaryVerbatimStringPrefixCheckTryFixRemovesPrefixFromLiteral()
    {
        using var tempFile = new TempFile(".cs");
        var source = """
            public sealed class Sample
            {
                public string Run()
                {
                    var foo = @"hello";
                    return foo;
                }
            }
            """;

        File.WriteAllText(tempFile.FullName, source);

        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Hint,
            CodeReviewRuleIds.UnnecessaryVerbatimStringPrefix,
            "Sample.cs",
            5,
            "Unnecessary verbatim string prefix.");

        var check = new UnnecessaryVerbatimStringPrefixCodeReviewCheck();
        var success = check.TryFix(finding, tempFile.FullName, out var message);

        Assert.That(success, Is.True);
        Assert.That(message, Is.Not.Empty);

        var updated = File.ReadAllText(tempFile.FullName);
        Assert.That(updated, Does.Contain("var foo = \"hello\";"));
        Assert.That(updated, Does.Not.Contain("@\"hello\""));
    }

    [Test]
    public void UnnecessaryVerbatimStringPrefixCheckTryFixRemovesPrefixFromInterpolatedLiteral()
    {
        using var tempFile = new TempFile(".cs");
        var source = """
            public sealed class Sample
            {
                public string Run(string name)
                {
                    return $@"hello {name}";
                }
            }
            """;

        File.WriteAllText(tempFile.FullName, source);

        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Hint,
            CodeReviewRuleIds.UnnecessaryVerbatimStringPrefix,
            "Sample.cs",
            5,
            "Unnecessary verbatim string prefix.");

        var check = new UnnecessaryVerbatimStringPrefixCodeReviewCheck();
        var success = check.TryFix(finding, tempFile.FullName, out var message);

        Assert.That(success, Is.True);
        Assert.That(message, Is.Not.Empty);

        var updated = File.ReadAllText(tempFile.FullName);
        Assert.That(updated, Does.Contain("return $\"hello {name}\";"));
        Assert.That(updated, Does.Not.Contain("$@\""));
        Assert.That(updated, Does.Not.Contain("@$\""));
    }
}
