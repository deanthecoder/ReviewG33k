using System;
using System.IO;
using ReviewG33k.Services;
using ReviewG33k.Services.Checks;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class FixableCodeReviewChecksTests
{
    [Test]
    public void UnusedUsingCheckTryFixRemovesTheLineAndDoesNotLeaveTripleBlankLines()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReviewG33k-{Guid.NewGuid():N}.cs");
        try
        {
            var source = """
                using System;
                using System.Text;

                public sealed class Sample
                {
                    public string X() => new StringBuilder().ToString();
                }
                """;

            File.WriteAllText(tempPath, source);

            var finding = new CodeSmellFinding(
                CodeReviewFindingSeverity.Hint,
                CodeReviewRuleIds.UnusedUsingsRoslyn,
                "Sample.cs",
                1,
                "Unused using");

            var check = new UnusedUsingRoslynCodeReviewCheck();
            var success = check.TryFix(finding, tempPath, out var message);

            Assert.That(success, Is.True);
            Assert.That(message, Is.Not.Empty);

            var updated = File.ReadAllText(tempPath);
            Assert.That(updated, Does.Not.Contain("using System;"));
            Assert.That(updated, Does.Contain("using System.Text;"));
            Assert.That(updated, Does.Not.Contain("\n\n\n"));
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    [Test]
    public void ThrowExCheckTryFixReplacesWithThrow()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReviewG33k-{Guid.NewGuid():N}.cs");
        try
        {
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

            File.WriteAllText(tempPath, source);

            var finding = new CodeSmellFinding(
                CodeReviewFindingSeverity.Important,
                CodeReviewRuleIds.ThrowExInCatch,
                "Sample.cs",
                13,
                "Use throw; instead of throw ex;");

            var check = new ThrowExCodeReviewCheck();
            var success = check.TryFix(finding, tempPath, out var message);

            Assert.That(success, Is.True);
            Assert.That(message, Is.Not.Empty);

            var updated = File.ReadAllText(tempPath);
            Assert.That(updated, Does.Not.Contain("throw ex;"));
            Assert.That(updated, Does.Contain("throw;"));
            Assert.That(updated, Does.Not.Contain("\n\n\n"));
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    [Test]
    public void WarningSuppressionCheckTryFixWhenPragmaRemovesTheLine()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReviewG33k-{Guid.NewGuid():N}.cs");
        try
        {
            var source = """
                using System;

                #pragma warning disable CS0168

                public sealed class Sample
                {
                }
                """;

            File.WriteAllText(tempPath, source);

            var finding = new CodeSmellFinding(
                CodeReviewFindingSeverity.Important,
                CodeReviewRuleIds.WarningSuppression,
                "Sample.cs",
                3,
                "Suppression added");

            var check = new WarningSuppressionCodeReviewCheck();
            var success = check.TryFix(finding, tempPath, out var message);

            Assert.That(success, Is.True);
            Assert.That(message, Is.Not.Empty);

            var updated = File.ReadAllText(tempPath);
            Assert.That(updated, Does.Not.Contain("#pragma warning disable"));
            Assert.That(updated, Does.Not.Contain("\n\n\n"));
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    [Test]
    public void WarningSuppressionCheckTryFixWhenSuppressMessageRemovesTheAttribute()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReviewG33k-{Guid.NewGuid():N}.cs");
        try
        {
            var source = """
                using System;
                using System.Diagnostics.CodeAnalysis;

                [SuppressMessage("Usage", "CA1801:Review unused parameters", Justification = "Test.")]
                public sealed class Sample
                {
                }
                """;

            File.WriteAllText(tempPath, source);

            var finding = new CodeSmellFinding(
                CodeReviewFindingSeverity.Important,
                CodeReviewRuleIds.WarningSuppression,
                "Sample.cs",
                4,
                "Suppression added");

            var check = new WarningSuppressionCodeReviewCheck();
            var success = check.TryFix(finding, tempPath, out var message);

            Assert.That(success, Is.True);
            Assert.That(message, Is.Not.Empty);

            var updated = File.ReadAllText(tempPath);
            Assert.That(updated, Does.Not.Contain("SuppressMessage"));
            Assert.That(updated, Does.Not.Contain("\n\n\n"));
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }
}

