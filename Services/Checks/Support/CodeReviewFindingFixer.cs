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
using System.IO;
using Microsoft.CodeAnalysis.Text;
using ReviewG33k.Services;

namespace ReviewG33k.Services.Checks.Support;

public static class CodeReviewFindingFixer
{
    public static bool CanFix(CodeSmellFinding finding) =>
        finding != null &&
        string.Equals(finding.RuleId, CodeReviewRuleIds.UnusedUsingsRoslyn, StringComparison.OrdinalIgnoreCase);

    public static bool TryFix(CodeSmellFinding finding, string resolvedFilePath, out string resultMessage)
    {
        resultMessage = null;

        if (!CanFix(finding))
        {
            resultMessage = "Finding is not fixable.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(resolvedFilePath) || !File.Exists(resolvedFilePath))
        {
            resultMessage = "File path could not be resolved.";
            return false;
        }

        if (finding.LineNumber < 1)
        {
            resultMessage = "Finding does not contain a valid line number.";
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(resolvedFilePath);
        }
        catch (Exception exception)
        {
            resultMessage = $"Could not read file: {exception.Message}";
            return false;
        }

        var sourceText = SourceText.From(text);
        var lineIndex = finding.LineNumber - 1;
        if (lineIndex < 0 || lineIndex >= sourceText.Lines.Count)
        {
            resultMessage = "Finding line number is out of range for this file.";
            return false;
        }

        var line = sourceText.Lines[lineIndex];
        var lineText = line.ToString();
        var trimmed = lineText.TrimStart();
        if (!trimmed.StartsWith("using ", StringComparison.Ordinal) &&
            !trimmed.StartsWith("using\t", StringComparison.Ordinal) &&
            !trimmed.StartsWith("global using ", StringComparison.Ordinal) &&
            !trimmed.StartsWith("global using\t", StringComparison.Ordinal))
        {
            resultMessage = "Target line is not a using directive.";
            return false;
        }

        var spanToRemove = TextSpan.FromBounds(line.Start, line.EndIncludingLineBreak);
        var updatedText = sourceText.WithChanges(new TextChange(spanToRemove, string.Empty)).ToString();
        updatedText = CollapseConsecutiveBlankLinesNearLine(updatedText, lineIndex);

        try
        {
            File.WriteAllText(resolvedFilePath, updatedText);
        }
        catch (Exception exception)
        {
            resultMessage = $"Could not write file: {exception.Message}";
            return false;
        }

        resultMessage = "Removed unused using directive.";
        return true;
    }

    private static string CollapseConsecutiveBlankLinesNearLine(string text, int centerLineIndex)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var sourceText = SourceText.From(text);
        var lines = sourceText.Lines;
        if (lines.Count == 0)
            return text;

        var start = Math.Max(0, centerLineIndex - 4);
        var end = Math.Min(lines.Count - 1, centerLineIndex + 4);

        var changes = new System.Collections.Generic.List<TextChange>();
        var blankRunStart = -1;

        for (var index = start; index <= end; index++)
        {
            var isBlank = string.IsNullOrWhiteSpace(lines[index].ToString());
            if (!isBlank)
            {
                blankRunStart = -1;
                continue;
            }

            if (blankRunStart < 0)
            {
                blankRunStart = index;
                continue;
            }

            // More than one consecutive blank line: delete this extra blank line.
            var extraBlankLine = lines[index];
            var deleteSpan = TextSpan.FromBounds(extraBlankLine.Start, extraBlankLine.EndIncludingLineBreak);
            changes.Add(new TextChange(deleteSpan, string.Empty));
        }

        if (changes.Count == 0)
            return text;

        var updated = sourceText.WithChanges(changes).ToString();
        return updated;
    }
}
