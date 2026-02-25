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
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ReviewG33k.Services;

namespace ReviewG33k.Views;

public partial class ReviewResultsWindow : Window
{
    private const int PreviewLinesBefore = 4;
    private const int PreviewLinesAfter = 8;

    private readonly Action<CodeSmellFinding> m_openFindingAction;
    private readonly Func<CodeSmellFinding, Task<bool>> m_commentFindingAction;
    private readonly Func<CodeSmellFinding, string> m_resolveFindingPath;
    private readonly ReviewResultRow[] m_rows;
    private bool m_isBulkCommenting;

    public ReviewResultsWindow()
        : this(Array.Empty<CodeSmellFinding>(), false, false, null, null, null)
    {
    }

    public ReviewResultsWindow(IEnumerable<CodeSmellFinding> findings)
        : this(findings, false, false, null, null, null)
    {
    }

    public ReviewResultsWindow(
        IEnumerable<CodeSmellFinding> findings,
        bool canOpenInVsCode,
        bool canCommentInBitbucket,
        Action<CodeSmellFinding> openFindingAction,
        Func<CodeSmellFinding, Task<bool>> commentFindingAction,
        Func<CodeSmellFinding, string> resolveFindingPath)
    {
        m_openFindingAction = openFindingAction;
        m_commentFindingAction = commentFindingAction;
        m_resolveFindingPath = resolveFindingPath;
        InitializeComponent();
        ResultsListBox.SelectionChanged += ResultsListBox_OnSelectionChanged;
        CommentSelectedButton.IsVisible = canCommentInBitbucket;

        m_rows = (findings ?? [])
            .Where(finding => finding != null)
            .Where(finding => finding.Severity != CodeReviewFindingSeverity.Ok)
            .OrderBy(finding => GetSeveritySortOrder(finding.Severity))
            .ThenBy(finding => finding.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.LineNumber)
            .Select(finding => MapToRow(finding, canOpenInVsCode, canCommentInBitbucket))
            .ToArray();

        foreach (var row in m_rows)
            row.PropertyChanged += ReviewResultRow_OnPropertyChanged;

        ResultsListBox.ItemsSource = m_rows;
        if (m_rows.Length > 0)
            ResultsListBox.SelectedIndex = 0;
        else
            SetPreviewText("Select an issue to preview surrounding file content.", "Preview");

        SummaryTextBlock.Text = m_rows.Length == 0
            ? "No review findings"
            : $"{m_rows.Length} finding(s)";
        UpdateBatchActionButtonStates();
    }

    private void OpenFindingButton_OnClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReviewResultRow row } || !row.CanOpenActive)
            return;

        m_openFindingAction?.Invoke(row.Finding);
    }

    private async void CommentFindingButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReviewResultRow row } || !row.CanCommentActive || m_commentFindingAction == null)
            return;

        row.IsPostingComment = true;
        try
        {
            var success = await m_commentFindingAction(row.Finding);
            if (success)
                row.HasPostedComment = true;
        }
        catch (Exception exception)
        {
            SetPreviewText($"Failed to post comment: {exception.Message}", "Preview");
        }
        finally
        {
            row.IsPostingComment = false;
        }
    }

    private void ToggleAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (m_rows.Length == 0)
            return;

        var includedCount = m_rows.Count(row => row.IsIncluded);
        var excludedCount = m_rows.Length - includedCount;
        var nextIncludedState = includedCount <= excludedCount;

        foreach (var row in m_rows)
            row.IsIncluded = nextIncludedState;

        UpdateBatchActionButtonStates();
    }

    private async void CommentSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (m_isBulkCommenting || m_commentFindingAction == null)
            return;

        var rowsToComment = m_rows
            .Where(row => row.IsIncluded && row.CanCommentActive)
            .ToArray();
        if (rowsToComment.Length == 0)
            return;

        m_isBulkCommenting = true;
        UpdateBatchActionButtonStates();

        var successCount = 0;
        var failureCount = 0;
        foreach (var row in rowsToComment)
        {
            row.IsPostingComment = true;
            try
            {
                var success = await m_commentFindingAction(row.Finding);
                if (success)
                {
                    row.HasPostedComment = true;
                    successCount++;
                }
                else
                {
                    failureCount++;
                }
            }
            catch (Exception exception)
            {
                failureCount++;
                SetPreviewText($"Failed to post comment: {exception.Message}", "Preview");
            }
            finally
            {
                row.IsPostingComment = false;
            }
        }

        m_isBulkCommenting = false;
        UpdateBatchActionButtonStates();

        if (failureCount == 0)
            SetPreviewText($"Posted {successCount} comment(s).", "Preview");
        else
            SetPreviewText($"Posted {successCount} comment(s). {failureCount} failed. See log for details.", "Preview");
    }

    private async void ExportToClipboardButton_OnClick(object sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            SetPreviewText("Clipboard is unavailable in this window.", "Preview");
            return;
        }

        var exportText = BuildExportText(out var exportedCount, out var exportedIncludedOnly);
        if (exportedCount == 0)
        {
            SetPreviewText("No findings available to export.", "Preview");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(exportText);
            var scope = exportedIncludedOnly ? "included" : "all";
            SetPreviewText($"Copied {exportedCount} {scope} finding(s) to the clipboard.", "Preview");
        }
        catch (Exception exception)
        {
            SetPreviewText($"Failed to copy to clipboard: {exception.Message}", "Preview");
        }
    }

    private void UntickSameTypeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ReviewResultRow sourceRow } || string.IsNullOrWhiteSpace(sourceRow.RuleId))
            return;

        foreach (var row in m_rows.Where(row => string.Equals(row.RuleId, sourceRow.RuleId, StringComparison.OrdinalIgnoreCase)))
            row.IsIncluded = false;

        UpdateBatchActionButtonStates();
    }

    private void ResultsListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsListBox.SelectedItem is not ReviewResultRow row)
        {
            SetPreviewText("Select an issue to preview surrounding file content.", "Preview");
            return;
        }

        UpdatePreviewForFinding(row.Finding);
    }

    private static ReviewResultRow MapToRow(
        CodeSmellFinding finding,
        bool canOpenInVsCode,
        bool canCommentInBitbucket)
    {
        var issueLocation = finding.LineNumber > 0
            ? $"{finding.FilePath}:{finding.LineNumber}"
            : finding.FilePath;
        var issueSummary = finding.Message ?? string.Empty;
        var issueFull = issueSummary;
        var hasFileAndLine = !string.IsNullOrWhiteSpace(finding.FilePath) && finding.LineNumber > 0;
        var canOpen = canOpenInVsCode && hasFileAndLine;
        var canComment = canCommentInBitbucket && hasFileAndLine;
        var openToolTipBase = canOpen
            ? "Open file and line in VS Code"
            : "VS Code not detected or no valid file/line for this issue";
        var commentToolTipBase = canComment
            ? "Post this issue as a Bitbucket PR inline comment"
            : "Bitbucket PR context is unavailable or no valid file/line for this issue";
        return new ReviewResultRow(
            finding,
            finding.RuleId,
            finding.Severity.ToString().ToUpperInvariant(),
            issueSummary,
            issueFull,
            issueLocation,
            canOpen,
            openToolTipBase,
            canComment,
            commentToolTipBase,
            canCommentInBitbucket,
            false);
    }

    private void ReviewResultRow_OnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReviewResultRow.IsIncluded) or nameof(ReviewResultRow.IsPostingComment) or nameof(ReviewResultRow.HasPostedComment))
            UpdateBatchActionButtonStates();
    }

    private void UpdatePreviewForFinding(CodeSmellFinding finding)
    {
        if (finding == null)
        {
            SetPreviewText("No finding selected.", "Preview");
            return;
        }

        var lineNumber = finding.LineNumber > 0 ? finding.LineNumber : 1;
        var resolvedPath = m_resolveFindingPath?.Invoke(finding);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
        {
            SetPreviewText($"Could not resolve file for '{finding.FilePath}'.", "Preview");
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(resolvedPath);
        }
        catch (Exception exception)
        {
            SetPreviewText($"Could not read file: {exception.Message}", "Preview");
            return;
        }

        if (lines.Length == 0)
        {
            SetPreviewText("(File is empty)", $"Preview: {Path.GetFileName(resolvedPath)}");
            return;
        }

        var startLine = Math.Max(1, lineNumber - PreviewLinesBefore);
        var endLine = Math.Min(lines.Length, lineNumber + PreviewLinesAfter);
        var lineNumberWidth = endLine.ToString().Length;

        var builder = new StringBuilder();
        for (var line = startLine; line <= endLine; line++)
        {
            var marker = line == lineNumber ? ">" : " ";
            builder.Append(marker)
                .Append(' ')
                .Append(line.ToString().PadLeft(lineNumberWidth))
                .Append(": ")
                .AppendLine(lines[line - 1]);
        }

        SetPreviewText(builder.ToString().TrimEnd('\r', '\n'), $"Preview: {Path.GetFileName(resolvedPath)}");
    }

    private void SetPreviewText(string text, string header)
    {
        PreviewHeaderTextBlock.Text = header ?? "Preview";
        PreviewTextBox.Text = text ?? string.Empty;
        PreviewTextBox.CaretIndex = 0;
    }

    private void UpdateBatchActionButtonStates()
    {
        ToggleAllButton.IsEnabled = m_rows.Length > 0;
        ExportToClipboardButton.IsEnabled = m_rows.Length > 0;
        CommentSelectedButton.IsEnabled = !m_isBulkCommenting &&
                                          m_commentFindingAction != null &&
                                          m_rows.Any(row => row.IsIncluded && row.CanCommentActive);
    }

    private string BuildExportText(out int exportedCount, out bool exportedIncludedOnly)
    {
        var rowsToExport = m_rows.Where(row => row.IsIncluded).ToArray();
        exportedIncludedOnly = rowsToExport.Length > 0;
        if (!exportedIncludedOnly)
            rowsToExport = m_rows;

        exportedCount = rowsToExport.Length;
        if (exportedCount == 0)
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("ReviewG33k Findings");
        builder.Append("Scope: ")
            .Append(exportedIncludedOnly ? "Included findings" : "All findings")
            .AppendLine();
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
                .Append(row.SeverityText)
                .Append("] ")
                .Append(location)
                .AppendLine();

            if (!string.IsNullOrWhiteSpace(finding.RuleId))
            {
                builder.Append("   Rule: ")
                    .Append(finding.RuleId)
                    .AppendLine();
            }

            builder.Append("   ")
                .AppendLine((finding.Message ?? string.Empty).Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static int GetSeveritySortOrder(CodeReviewFindingSeverity severity) =>
        severity switch
        {
            CodeReviewFindingSeverity.Important => 0,
            CodeReviewFindingSeverity.Suggestion => 1,
            CodeReviewFindingSeverity.Hint => 2,
            CodeReviewFindingSeverity.Ok => 3,
            _ => 4
        };

    private sealed class ReviewResultRow : INotifyPropertyChanged
    {
        private static readonly IBrush IncludedIssueBrush = Brushes.Gainsboro;
        private static readonly IBrush ExcludedIssueBrush = Brushes.Gray;
        private static readonly IBrush IncludedLocationBrush = Brushes.Gray;
        private static readonly IBrush ExcludedLocationBrush = new SolidColorBrush(Color.Parse("#6B6B6B"));

        private readonly string m_openToolTipBase;
        private readonly string m_commentToolTipBase;
        private bool m_isIncluded = true;
        private bool m_isPostingComment;
        private bool m_hasPostedComment;

        public ReviewResultRow(
            CodeSmellFinding finding,
            string ruleId,
            string severityText,
            string issueSummary,
            string issueFull,
            string issueLocation,
            bool canOpen,
            string openToolTipBase,
            bool canComment,
            string commentToolTipBase,
            bool showCommentButton,
            bool hasExistingComment)
        {
            Finding = finding;
            RuleId = ruleId ?? string.Empty;
            SeverityText = severityText ?? string.Empty;
            IssueSummary = issueSummary ?? string.Empty;
            IssueFull = issueFull ?? string.Empty;
            IssueLocation = issueLocation ?? string.Empty;
            CanOpen = canOpen;
            CanComment = canComment;
            ShowCommentButton = showCommentButton;
            m_openToolTipBase = openToolTipBase ?? string.Empty;
            m_commentToolTipBase = commentToolTipBase ?? string.Empty;
            m_hasPostedComment = hasExistingComment;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public CodeSmellFinding Finding { get; }

        public string RuleId { get; }

        public string SeverityText { get; }

        public string IssueSummary { get; }

        public string IssueFull { get; }

        public string IssueLocation { get; }

        public bool CanOpen { get; }

        public bool CanComment { get; }

        public bool ShowCommentButton { get; }

        public bool IsIncluded
        {
            get => m_isIncluded;
            set
            {
                if (m_isIncluded == value)
                    return;

                m_isIncluded = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CanOpenActive));
                RaisePropertyChanged(nameof(CanCommentActive));
                RaisePropertyChanged(nameof(OpenToolTip));
                RaisePropertyChanged(nameof(CommentToolTip));
                RaisePropertyChanged(nameof(IssueForeground));
                RaisePropertyChanged(nameof(IssueLocationForeground));
            }
        }

        public bool IsPostingComment
        {
            get => m_isPostingComment;
            set
            {
                if (m_isPostingComment == value)
                    return;

                m_isPostingComment = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CanCommentActive));
                RaisePropertyChanged(nameof(CommentToolTip));
            }
        }

        public bool HasPostedComment
        {
            get => m_hasPostedComment;
            set
            {
                if (m_hasPostedComment == value)
                    return;

                m_hasPostedComment = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CanCommentActive));
                RaisePropertyChanged(nameof(CommentToolTip));
            }
        }

        public bool CanOpenActive => CanOpen && IsIncluded;

        public bool CanCommentActive => CanComment && IsIncluded && !IsPostingComment && !HasPostedComment;

        public string OpenToolTip => !IsIncluded ? "Issue is ignored." : m_openToolTipBase;

        public string CommentToolTip
        {
            get
            {
                if (!IsIncluded)
                    return "Issue is ignored.";
                if (HasPostedComment)
                    return "Comment already posted for this issue.";
                if (IsPostingComment)
                    return "Posting comment...";
                return m_commentToolTipBase;
            }
        }

        public string UntickSameTypeMenuHeader =>
            string.IsNullOrWhiteSpace(RuleId)
                ? "Untick issues of this type"
                : $"Untick all `{RuleId}` issues";

        public IBrush IssueForeground => IsIncluded ? IncludedIssueBrush : ExcludedIssueBrush;

        public IBrush IssueLocationForeground => IsIncluded ? IncludedLocationBrush : ExcludedLocationBrush;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
