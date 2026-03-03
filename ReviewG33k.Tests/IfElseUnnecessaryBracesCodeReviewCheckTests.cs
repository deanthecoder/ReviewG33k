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
public sealed class IfElseUnnecessaryBracesCodeReviewCheckTests
{
    [Test]
    public void CanFixWhenFindingMatchesRuleAndLineIsPositiveReturnsTrue()
    {
        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Hint,
            CodeReviewRuleIds.IfElseUnnecessaryBraces,
            "Sample.cs",
            8,
            "Unnecessary braces.");
        var check = new IfElseUnnecessaryBracesCodeReviewCheck();

        var canFix = check.CanFix(finding);

        Assert.That(canFix, Is.True);
    }

    [Test]
    public void TryFixWhenFindingIsNullThrowsArgumentNullException()
    {
        var check = new IfElseUnnecessaryBracesCodeReviewCheck();

        Assert.Throws<ArgumentNullException>(() => check.TryFix(null, new FileInfo("Sample.cs"), out _));
    }

    [Test]
    public void TryFixWhenFilePathIsBlankReturnsFalse()
    {
        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Hint,
            CodeReviewRuleIds.IfElseUnnecessaryBraces,
            "Sample.cs",
            8,
            "Unnecessary braces.");
        var check = new IfElseUnnecessaryBracesCodeReviewCheck();

        var success = check.TryFix(finding, null, out var message);

        Assert.That(success, Is.False);
        Assert.That(message, Is.EqualTo("A valid file path is required."));
    }
}
