using System;
using System.IO;
using ReviewG33k.Services;
using ReviewG33k.Services.Checks;
using ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class CodeReviewFindingFixerTests
{
    [Test]
    public void TryFixWhenUnusedUsingFindingRemovesTheLine()
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

            var success = CodeReviewFindingFixer.TryFix(finding, tempPath, out var message);

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
                // ignore temp file cleanup issues in tests
            }
        }
    }

    [Test]
    public void TryFixWhenTargetLineIsNotAUsingDirectiveReturnsFalse()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReviewG33k-{Guid.NewGuid():N}.cs");
        try
        {
            var source = """
                public sealed class Sample
                {
                }
                """;

            File.WriteAllText(tempPath, source);

            var finding = new CodeSmellFinding(
                CodeReviewFindingSeverity.Hint,
                CodeReviewRuleIds.UnusedUsingsRoslyn,
                "Sample.cs",
                1,
                "Unused using");

            var success = CodeReviewFindingFixer.TryFix(finding, tempPath, out var message);

            Assert.That(success, Is.False);
            Assert.That(message, Is.Not.Empty);
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
                // ignore temp file cleanup issues in tests
            }
        }
    }
}
