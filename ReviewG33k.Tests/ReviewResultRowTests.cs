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
using ReviewG33k.Views;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class ReviewResultRowTests
{
    [Test]
    public void CanCodexPromptActiveWhenRowIsIncludedReturnsTrue()
    {
        var row = CreateCodexRow();

        Assert.That(row.CanCodexPromptActive, Is.True);
    }

    [Test]
    public void CanCodexPromptActiveWhenRowIsDeselectedReturnsFalse()
    {
        var row = CreateCodexRow();
        row.IsIncluded = false;

        Assert.That(row.CanCodexPromptActive, Is.False);
    }

    private static ReviewResultRow CreateCodexRow()
    {
        var finding = new CodeSmellFinding(
            CodeReviewFindingSeverity.Hint,
            "sample-rule",
            "Sample.cs",
            12,
            "Sample finding");

        return new ReviewResultRow(
            finding,
            canOpen: false,
            commentAvailability: ReviewResultRow.ActionAvailability.Hidden,
            fixAvailability: ReviewResultRow.ActionAvailability.Hidden,
            codexPromptAvailability: ReviewResultRow.ActionAvailability.Enabled);
    }
}
