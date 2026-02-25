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

public sealed class TaskRunAsyncCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly Regex TaskRunAsyncLambdaRegex = new(@"\bTask\.Run\s*\(\s*async\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TaskRunAsyncMethodGroupRegex = new(
        @"\bTask\.Run\s*\(\s*[A-Za-z_][A-Za-z0-9_\.]*Async\s*(?:,\s*[^)]*)?\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TaskRunAsyncLambdaCallRegex = new(
        @"\bTask\.Run\s*\(\s*\(\s*\)\s*=>\s*(?:await\s+)?[A-Za-z_][A-Za-z0-9_\.]*Async\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TaskRunAsyncFuncWrapperRegex = new(
        @"\bTask\.Run\s*\(\s*new\s+Func\s*<\s*Task(?:<[^>]+>)?\s*>\s*\(\s*[A-Za-z_][A-Za-z0-9_\.]*Async\s*\)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override string RuleId => CodeReviewRuleIds.TaskRunAsync;

    public override string DisplayName => "Task.Run(async ...)";

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

                if (!ContainsTaskRunAsyncPattern(line))
                    continue;

                AddFinding(report, CodeReviewFindingSeverity.Suggestion, file.Path, lineNumber, "Task.Run wrapping async code detected (possible fake async).");
            }
        }
    }

    private static bool ContainsTaskRunAsyncPattern(string line) =>
        !string.IsNullOrWhiteSpace(line) &&
        (TaskRunAsyncLambdaRegex.IsMatch(line) ||
         TaskRunAsyncMethodGroupRegex.IsMatch(line) ||
         TaskRunAsyncLambdaCallRegex.IsMatch(line) ||
         TaskRunAsyncFuncWrapperRegex.IsMatch(line));
}
