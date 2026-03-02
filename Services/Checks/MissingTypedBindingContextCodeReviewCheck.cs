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
using ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Services.Checks;

public sealed class MissingTypedBindingContextCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly Regex BindingRegex = new(@"\{\s*(?:Compiled)?Binding\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TypedContextRegex = new(
        @"\bx:DataType\s*=|\bd:DataContext\s*=|\bd:DesignInstance\b|\bd:DesignData\b|<\s*Design\.DataContext\b|\bDataType\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override string RuleId => CodeReviewRuleIds.MissingTypedBindingContext;

    public override string DisplayName => "UI bindings have typed/design data context";

    public override CodeReviewCheckScope Scope => CodeReviewCheckScope.AddedLinesOnly;

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        foreach (var file in context.Files)
        {
            if (!CodeReviewFileClassification.IsMarkupFilePath(file.Path))
                continue;
            if (string.IsNullOrWhiteSpace(file.Text))
                continue;
            if (!BindingRegex.IsMatch(file.Text))
                continue;
            if (TypedContextRegex.IsMatch(file.Text))
                continue;
            if (!HasRelevantBindingChange(file))
                continue;

            var lineNumber = FindFirstRelevantBindingLine(file);
            AddFinding(
                report,
                CodeReviewFindingSeverity.Hint,
                file.Path,
                lineNumber,
                "Markup has binding(s) but no typed/design data context hint (for example `x:DataType` or `d:DataContext`).");
        }
    }

    private static bool HasRelevantBindingChange(CodeReviewChangedFile file)
    {
        if (file == null)
            return false;
        if (file.IsAdded)
            return true;

        foreach (var lineNumber in file.AddedLineNumbers)
        {
            if (lineNumber < 1 || lineNumber > file.Lines.Count)
                continue;

            if (BindingRegex.IsMatch(file.Lines[lineNumber - 1] ?? string.Empty))
                return true;
        }

        return false;
    }

    private static int FindFirstRelevantBindingLine(CodeReviewChangedFile file)
    {
        if (file == null || file.Lines == null || file.Lines.Count == 0)
            return 1;

        if (!file.IsAdded)
        {
            var firstAddedBindingLine = file.AddedLineNumbers
                .OrderBy(line => line)
                .FirstOrDefault(line =>
                    line >= 1 &&
                    line <= file.Lines.Count &&
                    BindingRegex.IsMatch(file.Lines[line - 1] ?? string.Empty));
            if (firstAddedBindingLine > 0)
                return firstAddedBindingLine;
        }

        for (var index = 0; index < file.Lines.Count; index++)
        {
            if (BindingRegex.IsMatch(file.Lines[index] ?? string.Empty))
                return index + 1;
        }

        return 1;
    }
}
