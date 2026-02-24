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
using System.Text.RegularExpressions;

namespace ReviewG33k.Services;

public sealed class AsyncVoidCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly Regex AsyncVoidRegex = new(@"\basync\s+void\s+[A-Za-z_][A-Za-z0-9_]*\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override string RuleId => CodeReviewRuleIds.AsyncVoid;

    public override string DisplayName => "async void (non-event handlers)";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            foreach (var lineNumber in file.AddedLineNumbers.OrderBy(n => n))
            {
                if (lineNumber < 1 || lineNumber > file.Lines.Count)
                    continue;

                var line = file.Lines[lineNumber - 1];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (AsyncVoidRegex.IsMatch(line) && !CodeReviewCheckUtilities.LooksLikeEventHandlerSignature(line))
                    AddFinding(report, CodeReviewFindingSeverity.Warning, file.Path, lineNumber, "Suspicious 'async void' usage (non-event handler).");
            }
        }
    }
}