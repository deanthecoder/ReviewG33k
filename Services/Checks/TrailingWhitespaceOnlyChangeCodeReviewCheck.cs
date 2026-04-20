// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using System.Linq;
using ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Services.Checks;

/// <summary>
/// Reports files where the only detected text difference is trailing whitespace.
/// </summary>
/// <remarks>
/// Gives reviewers a low-noise signal when a file can be excluded from behavioral review.
/// </remarks>
public sealed class TrailingWhitespaceOnlyChangeCodeReviewCheck : CodeReviewCheckBase
{
    public override string RuleId => CodeReviewRuleIds.TrailingWhitespaceOnlyChange;

    public override string DisplayName => "Only trailing whitespace changed";

    public override CodeReviewCheckScope Scope => CodeReviewCheckScope.ChangedFileSet;

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.AllChangedFiles)
        {
            if (!TextFileChangeUtilities.TryGetComparableText(file, out var baselineText, out var currentText))
                continue;
            if (!TextFileChangeUtilities.IsOnlyTrailingWhitespaceChange(baselineText, currentText))
                continue;

            var lineNumber = file.AddedLineNumbers?.Where(line => line > 0).DefaultIfEmpty(1).Min() ?? 1;
            AddFinding(
                report,
                CodeReviewFindingSeverity.Hint,
                file.Path,
                lineNumber,
                "Only trailing whitespace appears to have changed in this file.");
        }
    }
}
