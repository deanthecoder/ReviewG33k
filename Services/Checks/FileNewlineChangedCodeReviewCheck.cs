// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Services.Checks;

/// <summary>
/// Reports changed text files whose newline style changed.
/// </summary>
/// <remarks>
/// Keeps file-wide LF/CRLF churn visible so reviewers can separate formatting noise from meaningful edits.
/// </remarks>
public sealed class FileNewlineChangedCodeReviewCheck : CodeReviewCheckBase
{
    public override string RuleId => CodeReviewRuleIds.FileNewlineChanged;

    public override string DisplayName => "File newline style changed";

    public override CodeReviewCheckScope Scope => CodeReviewCheckScope.ChangedFileSet;

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.AllChangedFiles)
        {
            if (!TextFileChangeUtilities.TryGetComparableText(file, out var baselineText, out var currentText))
                continue;

            var baselineNewlines = TextFileChangeUtilities.DetectNewlineKind(baselineText);
            var currentNewlines = TextFileChangeUtilities.DetectNewlineKind(currentText);
            if (baselineNewlines == currentNewlines ||
                baselineNewlines == NewlineKind.None ||
                currentNewlines == NewlineKind.None)
            {
                continue;
            }

            AddFinding(
                report,
                CodeReviewFindingSeverity.Hint,
                file.Path,
                1,
                $"File newline style changed from {TextFileChangeUtilities.GetNewlineDisplayName(baselineNewlines)} to {TextFileChangeUtilities.GetNewlineDisplayName(currentNewlines)}.");
        }
    }
}
