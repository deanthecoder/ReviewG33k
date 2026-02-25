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
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReviewG33k.Services;

namespace ReviewG33k.Views;

public partial class ReviewResultsWindow : Window
{
    private const int PreviewLinesBefore = 4;
    private const int PreviewLinesAfter = 8;

    private readonly Action<CodeSmellFinding> m_openFindingAction;
    private readonly Func<CodeSmellFinding, string> m_resolveFindingPath;

    public ReviewResultsWindow()
        : this(Array.Empty<CodeSmellFinding>(), false, null, null)
    {
    }

    public ReviewResultsWindow(IEnumerable<CodeSmellFinding> findings)
        : this(findings, false, null, null)
    {
    }

    public ReviewResultsWindow(
        IEnumerable<CodeSmellFinding> findings,
        bool canOpenInVsCode,
        Action<CodeSmellFinding> openFindingAction,
        Func<CodeSmellFinding, string> resolveFindingPath)
    {
        m_openFindingAction = openFindingAction;
        m_resolveFindingPath = resolveFindingPath;
        InitializeComponent();
        ResultsListBox.SelectionChanged += ResultsListBox_OnSelectionChanged;

        var rows = (findings ?? [])
            .Where(finding => finding != null)
            .Where(finding => finding.Severity != CodeReviewFindingSeverity.Ok)
            .OrderBy(finding => GetSeveritySortOrder(finding.Severity))
            .ThenBy(finding => finding.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(finding => finding.LineNumber)
            .Select(finding => MapToRow(finding, canOpenInVsCode))
            .ToArray();

        ResultsListBox.ItemsSource = rows;
        if (rows.Length > 0)
            ResultsListBox.SelectedIndex = 0;
        else
            SetPreviewText("Select an issue to preview surrounding file content.", "Preview");

        SummaryTextBlock.Text = rows.Length == 0
            ? "No review findings"
            : $"{rows.Length} finding(s)";
    }

    private void OpenFindingButton_OnClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ReviewResultRow row } || !row.CanOpen)
            return;

        m_openFindingAction?.Invoke(row.Finding);
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

    private static ReviewResultRow MapToRow(CodeSmellFinding finding, bool canOpenInVsCode)
    {
        var issueLocation = finding.LineNumber > 0
            ? $"{finding.FilePath}:{finding.LineNumber}"
            : finding.FilePath;
        var issueSummary = finding.Message ?? string.Empty;
        var issueFull = issueSummary;
        var canOpen = canOpenInVsCode &&
                      !string.IsNullOrWhiteSpace(finding.FilePath) &&
                      finding.LineNumber > 0;
        var openToolTip = canOpen
            ? "Open file and line in VS Code"
            : "VS Code not detected or no valid file/line for this issue";
        return new ReviewResultRow(finding, finding.Severity.ToString().ToUpperInvariant(), issueSummary, issueFull, issueLocation, canOpen, openToolTip);
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

    private static int GetSeveritySortOrder(CodeReviewFindingSeverity severity) =>
        severity switch
        {
            CodeReviewFindingSeverity.Important => 0,
            CodeReviewFindingSeverity.Suggestion => 1,
            CodeReviewFindingSeverity.Hint => 2,
            CodeReviewFindingSeverity.Ok => 3,
            _ => 4
        };

    private sealed class ReviewResultRow
    {
        public ReviewResultRow(CodeSmellFinding finding, string severityText, string issueSummary, string issueFull, string issueLocation, bool canOpen, string openToolTip)
        {
            Finding = finding;
            SeverityText = severityText ?? string.Empty;
            IssueSummary = issueSummary ?? string.Empty;
            IssueFull = issueFull ?? string.Empty;
            IssueLocation = issueLocation ?? string.Empty;
            CanOpen = canOpen;
            OpenToolTip = openToolTip ?? string.Empty;
        }

        public CodeSmellFinding Finding { get; }

        public string SeverityText { get; }

        public string IssueSummary { get; }

        public string IssueFull { get; }

        public string IssueLocation { get; }

        public bool CanOpen { get; }

        public string OpenToolTip { get; }
    }
}
