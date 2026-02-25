// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReviewG33k.Services;

public sealed class AsyncMethodNameSuffixCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly Regex AsyncMethodDeclarationRegex = new(
        @"^\s*(?:public|private|internal)\s+(?:(?:static|virtual|override|sealed|abstract|unsafe|new|partial|extern)\s+)*async\s+[A-Za-z_][A-Za-z0-9_<>,\.\[\]\?]*\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override string RuleId => CodeReviewRuleIds.AsyncMethodNameSuffix;

    public override string DisplayName => "async method name ends with Async";

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

                var match = AsyncMethodDeclarationRegex.Match(line);
                if (!match.Success)
                    continue;

                var methodName = match.Groups["name"].Value;
                if (methodName.EndsWith("Async", StringComparison.Ordinal))
                    continue;

                AddFinding(
                    report,
                    CodeReviewFindingSeverity.Hint,
                    file.Path,
                    lineNumber,
                    $"Async method '{methodName}' should end with 'Async'.");
            }
        }
    }
}
