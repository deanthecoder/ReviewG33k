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
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DTC.Core.Extensions;
using ReviewG33k.Services;
using ReviewG33k.Services.Checks;
using ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Views;

public partial class ReviewResultsWindow : Window
{
    private const int PreviewLinesBefore = 4;
    private const int PreviewLinesAfter = 24;
    private const int CodexPromptLinesBefore = 8;
    private const int CodexPromptLinesAfter = 14;

    private readonly Action<CodeSmellFinding> m_openFindingAction;
    private readonly Func<CodeSmellFinding, Task<bool>> m_commentFindingAction;
    private readonly Func<CodeSmellFinding, string> m_resolveFindingPath;
    private readonly Func<string, Task<IReadOnlyList<CodeSmellFinding>>> m_resampleFileFindingsAction;
    private readonly ICodeReviewFindingFixer m_findingFixer;
    private readonly List<ReviewResultRow> m_rows;
    private readonly bool m_canOpenInVsCode;
    private readonly bool m_canCommentInBitbucket;
    private readonly bool m_canFixLocally;
    private readonly bool m_hasPathResolver;
    private Cursor m_previousCursor;
    private bool m_isBulkCommenting;
    private bool m_isApplyingFix;
    private string m_previewFileName;

    public ReviewResultsWindow()
        : this(Array.Empty<CodeSmellFinding>(), false, false, false, null, null, null, null, null)
    {
    }

    public ReviewResultsWindow(IEnumerable<CodeSmellFinding> findings)
        : this(findings, false, false, false, null, null, null, null, null)
    {
    }

    public ReviewResultsWindow(
        IEnumerable<CodeSmellFinding> findings,
        bool canOpenInVsCode,
        bool canCommentInBitbucket,
        bool canFixLocally,
        ICodeReviewFindingFixer findingFixer,
        Action<CodeSmellFinding> openFindingAction,
        Func<CodeSmellFinding, Task<bool>> commentFindingAction,
        Func<CodeSmellFinding, string> resolveFindingPath,
        Func<string, Task<IReadOnlyList<CodeSmellFinding>>> resampleFileFindingsAction)
    {
        m_canOpenInVsCode = canOpenInVsCode;
        m_canCommentInBitbucket = canCommentInBitbucket;
        m_canFixLocally = canFixLocally;
        m_hasPathResolver = resolveFindingPath != null;
        m_openFindingAction = openFindingAction;
        m_commentFindingAction = commentFindingAction;
        m_resolveFindingPath = resolveFindingPath;
        m_resampleFileFindingsAction = resampleFileFindingsAction;
        m_findingFixer = findingFixer;
        InitializeComponent();
        UpdateCopyPreviewFileNameButtonState();
        ResultsListBox.SelectionChanged += ResultsListBox_OnSelectionChanged;
        CommentSelectedButton.IsVisible = canCommentInBitbucket;

        m_rows = (findings ?? [])
            .Where(finding => finding != null)
            .Where(finding => finding.Severity != CodeReviewFindingSeverity.Ok)
            .OrderBy(finding => GetSeveritySortOrder(finding.Severity))
            .ThenBy(finding => finding.RuleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.LineNumber)
            .Select(finding => MapToRow(finding, canOpenInVsCode, canCommentInBitbucket, canFixLocally, resolveFindingPath != null, findingFixer))
            .ToList();

        foreach (var row in m_rows)
            row.PropertyChanged += ReviewResultRow_OnPropertyChanged;

        ResultsListBox.ItemsSource = m_rows.ToArray();
        if (m_rows.Count > 0)
            ResultsListBox.SelectedIndex = 0;
        else
            SetPreviewText("Select an issue to preview surrounding file content.", "Preview");

        UpdateSummaryText();
        UpdateBatchActionButtonStates();
        UpdateOpenSelectedButtonState();
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

    private async void FixFindingButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (m_isApplyingFix || sender is not Button { DataContext: ReviewResultRow row } || !row.CanFixActive)
            return;

        if (row.Finding == null)
            return;

        if (!TryResolveFindingFile(row.Finding, out var resolvedFile))
        {
            SetPreviewText("Could not resolve file path for fix.", "Preview");
            return;
        }

        m_isApplyingFix = true;
        SetFixUiBusy(true);
        row.IsFixing = true;
        try
        {
            if (m_findingFixer == null)
            {
                SetPreviewText("No fixer is available in this mode.", "Preview");
                return;
            }

            if (!m_findingFixer.TryFix(row.Finding, resolvedFile, out var fixMessage))
            {
                SetPreviewText(fixMessage ?? "Fix failed.", "Preview");
                return;
            }

            SetPreviewText(fixMessage ?? "Fixed.", "Preview");

            if (!string.IsNullOrWhiteSpace(row.Finding?.FilePath) && m_resampleFileFindingsAction != null)
            {
                var previousRowIndex = m_rows.IndexOf(row);
                await RefreshFindingsForFileAsync(row.Finding.FilePath, row.Finding.LineNumber, previousRowIndex);
            }
            else
            {
                row.HasBeenFixed = true;
                row.IsIncluded = false;
                if (ResultsListBox.SelectedItem is ReviewResultRow selectedRow &&
                    ReferenceEquals(selectedRow, row) &&
                    row.Finding != null)
                {
                    UpdatePreviewForFinding(row.Finding);
                }
            }
        }
        catch (Exception exception)
        {
            SetPreviewText($"Fix failed: {exception.Message}", "Preview");
        }
        finally
        {
            row.IsFixing = false;
            m_isApplyingFix = false;
            SetFixUiBusy(false);
        }
    }

    private void SetFixUiBusy(bool isBusy)
    {
        if (isBusy)
        {
            m_previousCursor = Cursor;
            Cursor = new Cursor(StandardCursorType.Wait);
        }
        else
        {
            Cursor = m_previousCursor;
            m_previousCursor = null;
        }

        if (WindowContentGrid != null)
            WindowContentGrid.IsEnabled = !isBusy;
    }

    private async void CreateCodexPromptButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReviewResultRow row } || !row.CanCodexPromptActive)
            return;

        var clipboard = Clipboard;
        if (clipboard == null)
        {
            SetPreviewText("Clipboard is unavailable in this window.", "Preview");
            return;
        }

        if (!TryBuildCodexPrompt(row.Finding, out var promptText, out var failureReason))
        {
            SetPreviewText(failureReason ?? "Could not create Codex prompt.", "Preview");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(promptText);
            SetPreviewText("Copied Codex prompt to clipboard.", "Preview");
        }
        catch (Exception exception)
        {
            SetPreviewText($"Failed to copy Codex prompt: {exception.Message}", "Preview");
        }
    }

    private async void CopyPreviewFileNameButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(m_previewFileName))
            return;

        var clipboard = Clipboard;
        if (clipboard == null)
        {
            SetPreviewText("Clipboard is unavailable in this window.", "Preview", m_previewFileName);
            return;
        }

        try
        {
            await clipboard.SetTextAsync(m_previewFileName);
        }
        catch (Exception exception)
        {
            SetPreviewText($"Failed to copy file name: {exception.Message}", $"Preview: {m_previewFileName}", m_previewFileName);
        }
    }

    private void ToggleAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (m_rows.Count == 0)
            return;

        var includedCount = m_rows.Count(row => row.IsIncluded);
        var excludedCount = m_rows.Count - includedCount;
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
        var clipboard = Clipboard;
        if (clipboard == null)
        {
            SetPreviewText("Clipboard is unavailable in this window.", "Preview");
            return;
        }

        var exportText = BuildExportText(out var exportedCount);
        if (exportedCount == 0)
        {
            SetPreviewText("No included findings available to export.", "Preview");
            return;
        }

        try
        {
            await clipboard.SetTextAsync(exportText);
            SetPreviewText($"Copied {exportedCount} included finding(s) to the clipboard.", "Preview");
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
            UpdateOpenSelectedButtonState();
            SetPreviewText("Select an issue to preview surrounding file content.", "Preview");
            return;
        }

        UpdateOpenSelectedButtonState();
        UpdatePreviewForFinding(row.Finding);
    }

    private void OpenSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ResultsListBox.SelectedItem is not ReviewResultRow row || !row.CanOpenActive)
            return;

        m_openFindingAction?.Invoke(row.Finding);
    }

    private ReviewResultRow MapToRow(CodeSmellFinding finding) =>
        MapToRow(
            finding,
            m_canOpenInVsCode,
            m_canCommentInBitbucket,
            m_canFixLocally,
            m_hasPathResolver,
            m_findingFixer);

    private static ReviewResultRow MapToRow(
        CodeSmellFinding finding,
        bool canOpenInVsCode,
        bool canCommentInBitbucket,
        bool canFixLocally,
        bool hasPathResolver,
        ICodeReviewFindingFixer findingFixer)
    {
        var hasFileAndLine = !string.IsNullOrWhiteSpace(finding.FilePath) && finding.LineNumber > 0;
        var canOpen = canOpenInVsCode && hasFileAndLine;
        var canComment = canCommentInBitbucket && hasFileAndLine;
        var canFix = canFixLocally && hasPathResolver && hasFileAndLine && findingFixer != null && findingFixer.CanFix(finding);
        var canCreateCodexPrompt = canFixLocally &&
                                   hasPathResolver &&
                                   hasFileAndLine &&
                                   !canFix &&
                                   CanUseCodexPromptForFinding(finding);
        var commentAvailability = canCommentInBitbucket
            ? canComment
                ? ReviewResultRow.ActionAvailability.Enabled
                : ReviewResultRow.ActionAvailability.Disabled
            : ReviewResultRow.ActionAvailability.Hidden;
        var fixAvailability = canFix
            ? ReviewResultRow.ActionAvailability.Enabled
            : ReviewResultRow.ActionAvailability.Hidden;
        var codexPromptAvailability = canCreateCodexPrompt
            ? ReviewResultRow.ActionAvailability.Enabled
            : ReviewResultRow.ActionAvailability.Hidden;

        return new ReviewResultRow(
            finding,
            canOpen,
            commentAvailability,
            fixAvailability,
            codexPromptAvailability);
    }

    private static bool CanUseCodexPromptForFinding(CodeSmellFinding finding)
    {
        var ruleId = finding?.RuleId;
        return !string.Equals(ruleId, CodeReviewRuleIds.EmptyCatch, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(ruleId, CodeReviewRuleIds.SwallowingCatch, StringComparison.OrdinalIgnoreCase);
    }

    private void ReviewResultRow_OnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReviewResultRow.IsIncluded) or nameof(ReviewResultRow.IsPostingComment) or nameof(ReviewResultRow.HasPostedComment) or nameof(ReviewResultRow.IsFixing) or nameof(ReviewResultRow.HasBeenFixed))
        {
            UpdateOpenSelectedButtonState();
            UpdateBatchActionButtonStates();
        }
    }

    private async Task RefreshFindingsForFileAsync(string filePath, int preferredLineNumber, int preferredRowIndex = -1)
    {
        if (m_resampleFileFindingsAction == null || string.IsNullOrWhiteSpace(filePath))
            return;

        IReadOnlyList<CodeSmellFinding> refreshedFindings;
        try
        {
            refreshedFindings = await m_resampleFileFindingsAction(filePath) ?? [];
        }
        catch (Exception exception)
        {
            SetPreviewText($"Fixed, but failed to refresh findings: {exception.Message}", "Preview");
            return;
        }

        var rowsToRemove = m_rows
            .Where(row => RepositoryUtilities.AreSameRepoPath(row.Finding?.FilePath, filePath))
            .ToArray();
        foreach (var row in rowsToRemove)
        {
            row.PropertyChanged -= ReviewResultRow_OnPropertyChanged;
            m_rows.Remove(row);
        }

        var refreshedRows = refreshedFindings
            .Where(finding => finding != null)
            .Where(finding => finding.Severity != CodeReviewFindingSeverity.Ok)
            .Where(finding => RepositoryUtilities.AreSameRepoPath(finding.FilePath, filePath))
            .Select(MapToRow)
            .ToArray();
        foreach (var row in refreshedRows)
        {
            row.PropertyChanged += ReviewResultRow_OnPropertyChanged;
            m_rows.Add(row);
        }

        m_rows.Sort(CompareRows);
        ResultsListBox.ItemsSource = m_rows.ToArray();
        UpdateSummaryText();
        UpdateBatchActionButtonStates();
        UpdateOpenSelectedButtonState();

        if (m_rows.Count == 0)
        {
            SetPreviewText("No review findings.", "Preview");
            return;
        }

        ReviewResultRow nextRow = null;
        if (preferredRowIndex >= 0)
        {
            var boundedIndex = Math.Clamp(preferredRowIndex, 0, m_rows.Count - 1);
            nextRow = m_rows[boundedIndex];
        }

        nextRow ??= m_rows
            .Where(row => RepositoryUtilities.AreSameRepoPath(row.Finding?.FilePath, filePath))
            .OrderBy(row => Math.Abs((row.Finding?.LineNumber ?? 0) - preferredLineNumber))
            .ThenBy(row => row.Finding?.LineNumber ?? int.MaxValue)
            .FirstOrDefault() ?? m_rows[0];

        ResultsListBox.SelectedItem = nextRow;
        if (nextRow.Finding != null)
            UpdatePreviewForFinding(nextRow.Finding);
    }

    private void UpdatePreviewForFinding(CodeSmellFinding finding)
    {
        if (finding == null)
        {
            SetPreviewText("No finding selected.", "Preview");
            return;
        }

        if (!TryResolveFindingFile(finding, out var resolvedFile))
        {
            SetPreviewText($"Could not resolve file for '{finding.FilePath}'.", "Preview");
            return;
        }

        var lineNumber = finding.LineNumber > 0 ? finding.LineNumber : 1;
        string[] lines;
        try
        {
            lines = resolvedFile.ReadAllLines() ?? [];
        }
        catch (Exception exception)
        {
            SetPreviewText($"Could not read file: {exception.Message}", "Preview");
            return;
        }

        if (lines.Length == 0)
        {
            SetPreviewText("(File is empty)", $"Preview: {resolvedFile.Name}", resolvedFile.Name);
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

        SetPreviewText(builder.ToString().TrimEnd('\r', '\n'), $"Preview: {resolvedFile.Name}", resolvedFile.Name);
    }

    private void SetPreviewText(string text, string header, string previewFileName = null)
    {
        m_previewFileName = string.IsNullOrWhiteSpace(previewFileName) ? null : previewFileName.Trim();
        PreviewHeaderTextBlock.Text = header ?? "Preview";
        PreviewTextBox.Text = text ?? string.Empty;
        PreviewTextBox.CaretIndex = 0;
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrEmpty(PreviewTextBox.Text))
                PreviewTextBox.ScrollToLine(0);
        });
        UpdateCopyPreviewFileNameButtonState();
    }

    private void UpdateCopyPreviewFileNameButtonState()
    {
        if (CopyPreviewFileNameButton == null)
            return;

        var hasPreviewFileName = !string.IsNullOrWhiteSpace(m_previewFileName);
        CopyPreviewFileNameButton.IsEnabled = hasPreviewFileName;
        ToolTip.SetTip(
            CopyPreviewFileNameButton,
            hasPreviewFileName
                ? $"Copy '{m_previewFileName}' to clipboard"
                : "Copy file name to clipboard");
    }

    private void UpdateBatchActionButtonStates()
    {
        var hasIncludedFindings = m_rows.Any(row => row.IsIncluded);
        ToggleAllButton.IsEnabled = m_rows.Count > 0;
        ExportToClipboardButton.IsEnabled = hasIncludedFindings;
        CommentSelectedButton.IsEnabled = !m_isBulkCommenting &&
                                          m_commentFindingAction != null &&
                                          m_rows.Any(row => row.IsIncluded && row.CanCommentActive);
    }

    private void UpdateSummaryText()
    {
        SummaryTextBlock.Text = m_rows.Count == 0
            ? "No review findings"
            : $"{m_rows.Count} finding(s)";
    }

    private void UpdateOpenSelectedButtonState()
    {
        if (OpenSelectedButton == null)
            return;

        OpenSelectedButton.IsEnabled = ResultsListBox.SelectedItem is ReviewResultRow row && row.CanOpenActive;
    }

    private string BuildExportText(out int exportedCount)
    {
        var rowsToExport = m_rows.Where(row => row.IsIncluded).ToArray();
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
                .Append(row.SeverityText)
                .Append("] ")
                .Append(location)
                .AppendLine();

            builder.Append("   ")
                .AppendLine((finding.Message ?? string.Empty).Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private bool TryBuildCodexPrompt(CodeSmellFinding finding, out string promptText, out string failureReason)
    {
        promptText = string.Empty;
        failureReason = null;

        if (finding == null)
        {
            failureReason = "No finding selected.";
            return false;
        }

        if (!TryResolveFindingFile(finding, out var resolvedFile))
        {
            failureReason = $"Could not resolve file for '{finding.FilePath}'.";
            return false;
        }

        if (!TryFindRepositoryRoot(resolvedFile, out var repositoryPath))
        {
            failureReason = "Could not detect repository root from the selected file.";
            return false;
        }

        var issueLine = finding.LineNumber > 0 ? finding.LineNumber : 1;
        var issuePath = GetPromptRelativePath(repositoryPath.FullName, resolvedFile.FullName, finding.FilePath);
        var issueMessage = string.IsNullOrWhiteSpace(finding.Message) ? "(no message)" : finding.Message.Trim();
        var codeContext = BuildCodexPromptCodeContext(resolvedFile, issueLine);

        var builder = new StringBuilder();
        builder.AppendLine("You are fixing one local code review issue.");
        builder.AppendLine();
        builder.Append("Repository path: ").AppendLine(repositoryPath.FullName);
        builder.Append("File: ").Append(issuePath).Append(':').Append(issueLine).AppendLine();
        builder.Append("Issue: ").AppendLine(issueMessage);
        builder.AppendLine();
        builder.AppendLine("Code context:");
        builder.AppendLine("```");
        builder.AppendLine(codeContext);
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("Task:");
        builder.AppendLine("- Implement a concise fix for this issue in this repository.");
        builder.AppendLine("- Keep behavior unchanged except for resolving this issue.");
        builder.AppendLine("- Run relevant build/tests if practical.");
        builder.AppendLine();
        builder.AppendLine("Return:");
        builder.AppendLine("* Summary of the fix");
        builder.AppendLine("* Test/build commands run (or why none were run)");
        builder.AppendLine("* Risks or follow-up checks or code improvement suggestions");
        builder.AppendLine("* Any other relevant information");

        promptText = builder.ToString().TrimEnd('\r', '\n');
        return true;
    }

    private static string BuildCodexPromptCodeContext(FileInfo resolvedFile, int lineNumber)
    {
        string[] lines;
        try
        {
            lines = resolvedFile.ReadAllLines() ?? [];
        }
        catch
        {
            return "(Unable to read file contents.)";
        }

        if (lines.Length == 0)
            return "(File is empty.)";

        var boundedLineNumber = Math.Clamp(lineNumber, 1, lines.Length);
        var startLine = Math.Max(1, boundedLineNumber - CodexPromptLinesBefore);
        var endLine = Math.Min(lines.Length, boundedLineNumber + CodexPromptLinesAfter);
        var lineNumberWidth = endLine.ToString().Length;

        var builder = new StringBuilder();
        for (var line = startLine; line <= endLine; line++)
        {
            var marker = line == boundedLineNumber ? ">" : " ";
            builder.Append(marker)
                .Append(' ')
                .Append(line.ToString().PadLeft(lineNumberWidth))
                .Append(": ")
                .AppendLine(lines[line - 1]);
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private bool TryResolveFindingFile(CodeSmellFinding finding, out FileInfo resolvedFile)
    {
        resolvedFile = null;
        if (finding == null)
            return false;

        var resolvedPath = m_resolveFindingPath?.Invoke(finding);
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return false;

        resolvedFile = resolvedPath.ToFile();
        return resolvedFile.Exists();
    }

    private static bool TryFindRepositoryRoot(FileInfo resolvedFile, out DirectoryInfo repositoryPath)
    {
        repositoryPath = null;
        if (resolvedFile?.Exists() != true)
            return false;

        var current = resolvedFile.Directory;
        while (current != null)
        {
            if (current.GetDir(".git").Exists() || current.GetFile(".git").Exists())
            {
                repositoryPath = current;
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static string GetPromptRelativePath(string repositoryPath, string resolvedPath, string fallbackPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(resolvedPath))
            return RepositoryUtilities.NormalizeRepoPath(fallbackPath);

        try
        {
            var relativePath = Path.GetRelativePath(repositoryPath, resolvedPath);
            if (!string.IsNullOrWhiteSpace(relativePath) &&
                !relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                return RepositoryUtilities.NormalizeRepoPath(relativePath);
            }
        }
        catch
        {
            // Fall back to the finding path below.
        }

        return RepositoryUtilities.NormalizeRepoPath(fallbackPath);
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

    private static int CompareRows(ReviewResultRow left, ReviewResultRow right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left == null)
            return 1;
        if (right == null)
            return -1;

        var leftFinding = left.Finding;
        var rightFinding = right.Finding;
        var severityCompare = GetSeveritySortOrder(leftFinding?.Severity ?? CodeReviewFindingSeverity.Ok)
            .CompareTo(GetSeveritySortOrder(rightFinding?.Severity ?? CodeReviewFindingSeverity.Ok));
        if (severityCompare != 0)
            return severityCompare;

        var ruleCompare = string.Compare(leftFinding?.RuleId, rightFinding?.RuleId, StringComparison.OrdinalIgnoreCase);
        if (ruleCompare != 0)
            return ruleCompare;

        var fileCompare = string.Compare(leftFinding?.FilePath, rightFinding?.FilePath, StringComparison.OrdinalIgnoreCase);
        if (fileCompare != 0)
            return fileCompare;

        return (leftFinding?.LineNumber ?? int.MaxValue).CompareTo(rightFinding?.LineNumber ?? int.MaxValue);
    }

}
