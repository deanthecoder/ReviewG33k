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
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ReviewG33k.Views;

namespace ReviewG33k.Services;

/// <summary>
/// Contains reusable state calculations and mutations for review-results UI behavior.
/// </summary>
/// <remarks>
/// Useful for keeping summary, export, and inclusion-toggle logic out of window code-behind so
/// behavior stays centralized and easier to test.
/// </remarks>
internal sealed class ReviewResultsStateService
{
    public string BuildSummaryText(IEnumerable<ReviewResultRow> rows)
    {
        var rowList = rows?.Where(row => row != null).ToArray() ?? [];
        if (rowList.Length == 0)
            return "No review findings";

        var fileCount = rowList
            .Select(row => row.Finding?.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return $"{rowList.Length} finding(s) across {fileCount} file(s)";
    }

    public ReviewResultsBatchActionState BuildBatchActionState(
        IEnumerable<ReviewResultRow> rows,
        bool isBulkCommenting,
        bool canCommentInBitbucket)
    {
        var rowList = rows?.Where(row => row != null).ToArray() ?? [];
        var hasIncludedFindings = rowList.Any(row => row.IsIncluded);
        var canCommentSelected = !isBulkCommenting &&
                                 canCommentInBitbucket &&
                                 rowList.Any(row => row.IsIncluded && row.CanCommentActive);

        return new ReviewResultsBatchActionState(
            CanToggleAll: rowList.Length > 0,
            CanExportToClipboard: hasIncludedFindings,
            CanCommentSelected: canCommentSelected);
    }

    public void ToggleAllIncluded(IList<ReviewResultRow> rows)
    {
        if (rows == null || rows.Count == 0)
            return;

        var includedCount = rows.Count(row => row.IsIncluded);
        var excludedCount = rows.Count - includedCount;
        var nextIncludedState = includedCount <= excludedCount;

        foreach (var row in rows)
            row.IsIncluded = nextIncludedState;
    }

    public void SetSameTypeIncludedState(IEnumerable<ReviewResultRow> rows, string ruleId, bool isIncluded)
    {
        if (rows == null || string.IsNullOrWhiteSpace(ruleId))
            return;

        foreach (var row in rows.Where(row => string.Equals(row.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)))
            row.IsIncluded = isIncluded;
    }

    public string BuildExportText(IEnumerable<ReviewResultRow> rows, out int exportedCount)
    {
        var rowsToExport = rows?
            .Where(row => row != null && row.IsIncluded)
            .ToArray() ?? [];
        exportedCount = rowsToExport.Length;
        if (exportedCount == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("ReviewG33k Findings");
        builder.AppendLine("Scope: Included findings");
        builder.Append("Count: ")
            .Append(exportedCount)
            .AppendLine();
        builder.AppendLine();

        for (var index = 0; index < rowsToExport.Length; index++)
        {
            var row = rowsToExport[index];
            var finding = row.Finding;
            var location = !string.IsNullOrWhiteSpace(finding.FilePath)
                ? finding.LineNumber > 0
                    ? $"{finding.FilePath}:{finding.LineNumber}"
                    : finding.FilePath
                : "(no file)";

            builder.Append(index + 1)
                .Append(". [")
                .Append(row.CategoryText)
                .Append("] ")
                .Append(location)
                .AppendLine();

            builder.Append("   ")
                .AppendLine((finding.Message ?? string.Empty).Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }
}

internal readonly record struct ReviewResultsBatchActionState(
    bool CanToggleAll,
    bool CanExportToClipboard,
    bool CanCommentSelected);
