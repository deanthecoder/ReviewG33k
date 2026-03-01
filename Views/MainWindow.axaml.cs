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
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DTC.Core.UI;
using ReviewG33k.Models;
using ReviewG33k.Services;
using ReviewG33k.Services.Checks.Support;
using ReviewG33k.ViewModels;

namespace ReviewG33k.Views;

public partial class MainWindow : Window
{
    private static readonly Regex LogLocationRegex = new(@"\[(?<path>.+?\.[^:\]]+):(?<line>\d+)\]", RegexOptions.Compiled);
    private static readonly IBrush TimestampedLogBrush = Brushes.Gainsboro;
    private static readonly IBrush DetailLogBrush = Brushes.Gray;
    private static readonly IBrush ErrorLogBrush = Brushes.IndianRed;
    private static readonly IBrush WarningLogBrush = Brushes.Orange;
    private static readonly IBrush HintLogBrush = Brushes.LightSteelBlue;
    private static readonly IBrush PassLogBrush = Brushes.LimeGreen;

    private readonly GitCommandRunner m_gitCommandRunner = new();
    private readonly CodeReviewOrchestrator m_orchestrator;
    private readonly CodeSmellReportAnalyzer m_codeSmellReportAnalyzer;
    private readonly BitbucketPullRequestMetadataClient m_pullRequestMetadataClient = new();
    private readonly Settings m_settings = Settings.Instance;
    private readonly ObservableCollection<LogLineEntry> m_logLines = [];
    private CancellationTokenSource m_previewUpdateCancellation;
    private BitbucketPullRequestReference m_latestPullRequest;
    private string m_latestReviewWorktreePath;
    private string m_latestSolutionPath;
    private string m_previewPullRequestState;
    private string m_lastNonOpenPullRequestNoticeKey;
    private string m_vsCodeExecutablePath;
    private bool? m_previewPullRequestIsOpen;
    private bool m_busy;
    private bool m_isGitAvailable = true;
    private bool m_gitAvailabilityChecked;
    private bool m_vsCodeDetectionAttempted;
    private bool m_vsCodeUsesCommandShell;
    private bool m_normalizingPullRequestUrl;

    public MainWindow()
    {
        m_orchestrator = new CodeReviewOrchestrator(m_gitCommandRunner);
        m_codeSmellReportAnalyzer = new CodeSmellReportAnalyzer(m_gitCommandRunner);
        InitializeComponent();
        PullRequestUrlTextBox.AddHandler(DragDrop.DragOverEvent, PullRequestUrlTextBox_OnDragOver);
        PullRequestUrlTextBox.AddHandler(DragDrop.DropEvent, PullRequestUrlTextBox_OnDrop);
        PullRequestUrlTextBox.TextChanged += PullRequestUrlTextBox_OnTextChanged;
        RepositoryRootTextBox.TextChanged += RepositoryRootTextBox_OnTextChanged;
        RepositoryRootTextBox.LostFocus += RepositoryRootTextBox_OnLostFocus;
        LocalRepositoryFolderTextBox.TextChanged += LocalRepositoryFolderTextBox_OnTextChanged;
        LocalRepositoryFolderTextBox.LostFocus += LocalRepositoryFolderTextBox_OnLostFocus;
        LocalBaseBranchTextBox.TextChanged += LocalBaseBranchTextBox_OnTextChanged;
        LocalBaseBranchTextBox.LostFocus += LocalBaseBranchTextBox_OnLostFocus;
        LogListBox.AddHandler(PointerPressedEvent, LogListBox_OnPointerPressed, RoutingStrategies.Bubble);
        Opened += MainWindow_OnOpened;
        Activated += MainWindow_OnActivated;
        Closing += MainWindow_OnClosing;

        if (!string.IsNullOrWhiteSpace(m_settings.RepositoryRootPath))
            RepositoryRootTextBox.Text = m_settings.RepositoryRootPath;
        if (!string.IsNullOrWhiteSpace(m_settings.LocalReviewRepositoryPath))
            LocalRepositoryFolderTextBox.Text = m_settings.LocalReviewRepositoryPath;
        LocalBaseBranchTextBox.Text = string.IsNullOrWhiteSpace(m_settings.LocalReviewBaseBranch)
            ? "main"
            : m_settings.LocalReviewBaseBranch;
        ReviewModeToggleSwitch.IsChecked = m_settings.UseLocalCommittedReview;
        LogListBox.ItemsSource = m_logLines;

        ApplyReviewModeUi();
        _ = UpdatePullRequestPreviewAsync();
        UpdateActionButtonStates();
    }

    private async void BrowseRepositoryRootButton_OnClick(object sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null)
            return;

        var startLocation = await GetStartFolderAsync(topLevel, RepositoryRootTextBox.Text);

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose repository root folder",
                AllowMultiple = false,
                SuggestedStartLocation = startLocation
            });

        var selectedFolder = folders.FirstOrDefault();
        var path = selectedFolder?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            RepositoryRootTextBox.Text = path;
            PersistRepositoryRootPath(path);
        }
    }

    private async void BrowseLocalRepositoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null)
            return;

        var startLocation = await GetStartFolderAsync(topLevel, LocalRepositoryFolderTextBox.Text);
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose local repository folder",
                AllowMultiple = false,
                SuggestedStartLocation = startLocation
            });

        var selectedFolder = folders.FirstOrDefault();
        var path = selectedFolder?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        LocalRepositoryFolderTextBox.Text = path;
        PersistLocalReviewRepositoryPath(path);
    }

    private void ReviewModeToggleSwitch_OnChanged(object sender, RoutedEventArgs e)
    {
        var isLocalMode = ReviewModeToggleSwitch.IsChecked == true;
        if (isLocalMode)
        {
            m_latestPullRequest = null;
            UpdatePullRequestReviewState(null);
        }

        ApplyReviewModeUi();
        PersistUseLocalCommittedReview(isLocalMode);
        _ = UpdatePullRequestPreviewAsync();
        UpdateActionButtonStates();
    }

    private void PullRequestUrlTextBox_OnDragOver(object sender, DragEventArgs e)
    {
        e.DragEffects = TryExtractPullRequestUrl(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void PullRequestUrlTextBox_OnDrop(object sender, DragEventArgs e)
    {
        if (TryExtractPullRequestUrl(e.Data, out var url))
        {
            PullRequestUrlTextBox.Text = url;
            SetStatus("Pull request URL captured from drop.");
        }

        e.Handled = true;
    }

    private void PullRequestUrlTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        OnInputTextChanged();

    private void RepositoryRootTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        UpdateActionButtonStates();

    private void LocalRepositoryFolderTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        UpdateActionButtonStates();

    private void LocalBaseBranchTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        UpdateActionButtonStates();

    private async void PrepareReviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsLocalCommittedReviewMode())
        {
            await PrepareLocalCommittedReviewAsync();
            return;
        }

        if (!TryGetPullRequestInputs(out var repositoryRoot, out var prUrlText))
            return;

        PersistRepositoryRootPath(repositoryRoot);

        if (!BitbucketPrUrlParser.TryParse(prUrlText, out var pullRequest, out var parseError))
        {
            SetStatus(parseError);
            AppendLog($"Input error: {parseError}");
            DialogService.Instance.ShowMessage("Invalid pull request URL", parseError, null);
            return;
        }

        m_latestPullRequest = pullRequest;

        CodeSmellReport report = null;
        await ExecuteBusyActionAsync(
            "Reviewing pull request...",
            async () =>
            {
                m_latestSolutionPath = null;
                UpdateActionButtonStates();

                var metadata = await m_pullRequestMetadataClient.TryGetMetadataAsync(pullRequest);
                UpdatePullRequestReviewState(metadata);
                UpdateActionButtonStates();
                if (m_previewPullRequestIsOpen == false)
                {
                    NotifyNonOpenPullRequestIfNeeded(pullRequest);
                    return;
                }

                var changedPaths = await m_pullRequestMetadataClient.TryGetChangedPathsAsync(pullRequest);

                AppendLog($"PR detected: {pullRequest.SourceUrl}");
                AppendLog($"PR title: {FormatMetadataText(metadata?.Title)}");
                AppendLog($"PR author: {FormatMetadataText(metadata?.Author)}");
                AppendLog($"PR modified files: {(changedPaths.Count > 0 ? changedPaths.Count.ToString() : "N/A")}");

                var result = await m_orchestrator.PrepareReviewAsync(repositoryRoot, pullRequest, changedPaths, AppendLog);
                m_latestReviewWorktreePath = result.ReviewWorktreePath;

                AppendLog($"Review worktree ready: {result.ReviewWorktreePath}");

                if (!string.IsNullOrWhiteSpace(result.SolutionPath))
                {
                    m_latestSolutionPath = result.SolutionPath;
                    AppendLog($"Solution selected: {result.SolutionPath}");
                }
                else
                {
                    AppendLog("No .sln file found in review checkout.");
                }

                UpdateActionButtonStates();
                report = await RunCodeSmellScanAsync(result.ReviewWorktreePath, metadata?.TargetBranch);

                SetStatus("Review complete.");
            });

        if (report?.Findings.Count > 0)
            await ShowReviewResultsWindowAsync(report);
    }

    private async Task PrepareLocalCommittedReviewAsync()
    {
        if (!TryGetLocalCommittedReviewInputs(out var localRepositoryPath, out var baseBranch))
            return;

        PersistLocalReviewRepositoryPath(localRepositoryPath);
        PersistLocalReviewBaseBranch(baseBranch);

        m_latestPullRequest = null;
        UpdatePullRequestReviewState(null);

        CodeSmellReport report = null;
        await ExecuteBusyActionAsync(
            "Reviewing local committed changes...",
            async () =>
            {
                m_latestReviewWorktreePath = localRepositoryPath;
                m_latestSolutionPath = FindTopLevelSolutionFile(localRepositoryPath);
                UpdateActionButtonStates();

                AppendLog($"Local review repository: {localRepositoryPath}");
                AppendLog($"Comparing current branch changes against: origin/{baseBranch}");

                if (!string.IsNullOrWhiteSpace(m_latestSolutionPath))
                    AppendLog($"Solution selected: {m_latestSolutionPath}");
                else
                    AppendLog("No .sln file found in local repository.");

                var changedFileSource = new GitBranchComparisonChangedFileSource(
                    m_gitCommandRunner,
                    localRepositoryPath,
                    baseBranch,
                    fetchTargetBranch: true);

                report = await RunCodeSmellScanAsync(changedFileSource);
                SetStatus("Local review complete.");
            });

        if (report?.Findings.Count > 0)
            await ShowReviewResultsWindowAsync(report);
    }

    private async Task<CodeSmellReport> RunCodeSmellScanAsync(string reviewWorktreePath, string targetBranch)
    {
        AppendLog("Code review scan starting...");
        var report = await m_codeSmellReportAnalyzer.AnalyzeAsync(reviewWorktreePath, targetBranch);
        return ProcessCodeSmellReport(report);
    }

    private async Task<CodeSmellReport> RunCodeSmellScanAsync(ICodeReviewChangedFileSource changedFileSource)
    {
        AppendLog("Code review scan starting...");
        var report = await m_codeSmellReportAnalyzer.AnalyzeAsync(changedFileSource);
        return ProcessCodeSmellReport(report);
    }

    private CodeSmellReport ProcessCodeSmellReport(CodeSmellReport report)
    {
        foreach (var info in report.Info)
            AppendLog(info);

        if (DidAnalyzeChangedFiles(report))
            LogCodeSmellCheckStatuses(report);

        if (report.Findings.Count == 0)
        {
            AppendLog("Code review scan: No findings.");
            return report;
        }

        AppendLog($"Code review scan: {report.Findings.Count} finding(s).");
        foreach (var finding in report.Findings)
        {
            var severity = finding.Severity.ToString().ToUpperInvariant();
            var location = finding.LineNumber > 0 ? $"{finding.FilePath}:{finding.LineNumber}" : finding.FilePath;
            AppendLog($"{severity}: [{location}] {finding.Message}");
        }

        return report;
    }

    private void LogCodeSmellCheckStatuses(CodeSmellReport report)
    {
        foreach (var check in m_codeSmellReportAnalyzer.Checks)
        {
            var count = report.Findings.Count(finding => finding.RuleId.Equals(check.RuleId, StringComparison.OrdinalIgnoreCase));
            if (count == 0)
                AppendLog($"CHECK PASS: {check.DisplayName}");
        }
    }

    private static bool DidAnalyzeChangedFiles(CodeSmellReport report) =>
        report?.Info?.Any(info => info.StartsWith("Code review scan: Analyzing ", StringComparison.OrdinalIgnoreCase)) == true;

    private async Task ShowReviewResultsWindowAsync(CodeSmellReport report)
    {
        var canOpenInVsCode = TryDetectVsCode(out _, out _);
        var canCommentInBitbucket = !IsLocalCommittedReviewMode() && m_latestPullRequest != null;
        var canFixLocally = m_settings.UseLocalCommittedReview;
        var findingFixer = canFixLocally ? new CodeReviewFixDispatcher(m_codeSmellReportAnalyzer.Checks) : null;
        var resultsWindow = new ReviewResultsWindow(
            report?.Findings ?? [],
            canOpenInVsCode,
            canCommentInBitbucket,
            canFixLocally,
            findingFixer,
            OpenReviewFindingInVsCode,
            CommentOnReviewFindingAsync,
            ResolveReviewFindingPath,
            ResampleLocalFindingsForFileAsync);
        await resultsWindow.ShowDialog(this);
    }

    private void OpenReviewFindingInVsCode(CodeSmellFinding finding)
    {
        if (finding == null)
            return;

        var lineNumber = finding.LineNumber > 0 ? finding.LineNumber : 1;
        if (!TryResolveLogPath(finding.FilePath, out var resolvedPath))
        {
            var error = $"Could not resolve file path: {finding.FilePath}";
            AppendLog($"WARNING: {error}");
            SetStatus(error);
            return;
        }

        if (!TryLaunchVsCodeAtLine(resolvedPath, lineNumber, out var launchError))
        {
            AppendLog($"WARNING: {launchError}");
            SetStatus(launchError);
            return;
        }

        SetStatus($"Opened in VS Code: {Path.GetFileName(resolvedPath)}:{lineNumber}");
    }

    private string ResolveReviewFindingPath(CodeSmellFinding finding)
    {
        if (finding == null)
            return null;

        return TryResolveLogPath(finding.FilePath, out var resolvedPath) ? resolvedPath : null;
    }

    private async Task<IReadOnlyList<CodeSmellFinding>> ResampleLocalFindingsForFileAsync(string filePath)
    {
        if (!IsLocalCommittedReviewMode() || string.IsNullOrWhiteSpace(filePath))
            return [];

        var localRepositoryPath = LocalRepositoryFolderTextBox.Text?.Trim();
        var baseBranch = LocalBaseBranchTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(localRepositoryPath) ||
            string.IsNullOrWhiteSpace(baseBranch) ||
            !Directory.Exists(localRepositoryPath))
        {
            return [];
        }

        var changedFileSource = new GitBranchComparisonChangedFileSource(
            m_gitCommandRunner,
            localRepositoryPath,
            baseBranch,
            fetchTargetBranch: false);
        var sourceResult = await changedFileSource.LoadAsync();
        var changedFiles = sourceResult?.Files ?? [];
        var targetFile = changedFiles
            .FirstOrDefault(file => AreSameRepoPath(file?.Path, filePath));
        if (targetFile == null)
            return [];

        var report = m_codeSmellReportAnalyzer.AnalyzeFiles([targetFile]);
        return report.Findings
            .Where(finding => finding != null)
            .Where(finding => AreSameRepoPath(finding.FilePath, filePath))
            .OrderBy(finding => finding.LineNumber)
            .ToArray();
    }

    private static bool AreSameRepoPath(string leftPath, string rightPath)
    {
        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
            return false;

        return string.Equals(NormalizeRepoPath(leftPath), NormalizeRepoPath(rightPath), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRepoPath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim();

    private async Task<bool> CommentOnReviewFindingAsync(CodeSmellFinding finding)
    {
        if (finding == null || string.IsNullOrWhiteSpace(finding.FilePath) || finding.LineNumber < 1)
        {
            SetStatus("Selected finding has no valid file/line for commenting.");
            return false;
        }

        if (m_latestPullRequest == null)
        {
            SetStatus("Pull request context is unavailable for posting comments.");
            return false;
        }

        var commentText = finding.Message ?? string.Empty;
        var result = await m_pullRequestMetadataClient.TryAddInlineCommentAsync(
            m_latestPullRequest,
            finding.FilePath,
            finding.LineNumber,
            commentText);

        if (result.Success)
        {
            AppendLog($"HINT: Posted Bitbucket comment at [{finding.FilePath}:{finding.LineNumber}].");
            SetStatus("Comment posted to Bitbucket.");
            return true;
        }

        var errorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? "Failed to post Bitbucket comment."
            : result.ErrorMessage;
        AppendLog($"WARNING: Could not post Bitbucket comment at [{finding.FilePath}:{finding.LineNumber}]. {errorMessage}");
        SetStatus("Failed to post comment. See log for details.");
        return false;
    }

    private async Task ExecuteBusyActionAsync(string statusText, Func<Task> action)
    {
        if (m_busy)
            return;

        try
        {
            SetBusyState(true);
            SetStatus(statusText);
            await action();
        }
        catch (Exception exception)
        {
            SetStatus("Operation failed. See log for details.");
            AppendLog($"ERROR: {exception.Message}");
            DialogService.Instance.ShowMessage("Operation failed", exception.Message, null);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private static async Task<IStorageFolder> GetStartFolderAsync(TopLevel topLevel, string existingPath)
    {
        if (string.IsNullOrWhiteSpace(existingPath) || !Directory.Exists(existingPath))
            return null;

        return await topLevel.StorageProvider.TryGetFolderFromPathAsync(existingPath);
    }

    private bool TryGetPullRequestInputs(out string repositoryRoot, out string prUrlText)
    {
        repositoryRoot = RepositoryRootTextBox.Text?.Trim();
        prUrlText = PullRequestUrlTextBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            SetStatus("Set repo root folder first.");
            DialogService.Instance.ShowMessage("Repository root required", "Set the repo root folder before preparing a review checkout.", null);
            return false;
        }

        if (!Directory.Exists(repositoryRoot))
        {
            SetStatus($"Repo root folder does not exist: {repositoryRoot}");
            DialogService.Instance.ShowMessage("Repository root not found", repositoryRoot, null);
            return false;
        }

        if (string.IsNullOrWhiteSpace(prUrlText))
        {
            SetStatus("Paste or drop a Bitbucket pull request URL.");
            DialogService.Instance.ShowMessage("Pull request URL required", "Paste or drop a Bitbucket pull request URL.", null);
            return false;
        }

        return true;
    }

    private bool TryGetLocalCommittedReviewInputs(out string localRepositoryPath, out string baseBranch)
    {
        localRepositoryPath = LocalRepositoryFolderTextBox.Text?.Trim();
        baseBranch = LocalBaseBranchTextBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(localRepositoryPath))
        {
            SetStatus("Set local repository folder first.");
            DialogService.Instance.ShowMessage("Local repository required", "Choose the local repository folder to review.", null);
            return false;
        }

        if (!Directory.Exists(localRepositoryPath))
        {
            SetStatus($"Local repository folder does not exist: {localRepositoryPath}");
            DialogService.Instance.ShowMessage("Local repository not found", localRepositoryPath, null);
            return false;
        }

        if (!LooksLikeGitRepository(localRepositoryPath))
        {
            SetStatus("Selected folder is not a Git repository.");
            DialogService.Instance.ShowMessage("Invalid repository folder", "The selected folder does not appear to contain a Git repository.", null);
            return false;
        }

        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            SetStatus("Enter a base branch (for example: main).");
            DialogService.Instance.ShowMessage("Base branch required", "Enter the branch to compare against (for example: main or develop).", null);
            return false;
        }

        return true;
    }

    private void OnInputTextChanged()
    {
        if (!IsLocalCommittedReviewMode())
            NormalizePullRequestUrlInTextBoxIfNeeded();
        _ = UpdatePullRequestPreviewAsync();
        UpdateActionButtonStates();
    }

    private void NormalizePullRequestUrlInTextBoxIfNeeded()
    {
        if (m_normalizingPullRequestUrl)
            return;

        var urlText = PullRequestUrlTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(urlText))
            return;

        if (!BitbucketPrUrlParser.TryParse(urlText, out var pullRequest, out _))
            return;

        if (string.Equals(urlText, pullRequest.SourceUrl, StringComparison.Ordinal))
            return;

        m_normalizingPullRequestUrl = true;
        try
        {
            PullRequestUrlTextBox.Text = pullRequest.SourceUrl;
            PullRequestUrlTextBox.CaretIndex = pullRequest.SourceUrl.Length;
        }
        finally
        {
            m_normalizingPullRequestUrl = false;
        }
    }

    private async Task UpdatePullRequestPreviewAsync()
    {
        m_previewUpdateCancellation?.Cancel();
        m_previewUpdateCancellation?.Dispose();
        m_previewUpdateCancellation = new CancellationTokenSource();
        var cancellationToken = m_previewUpdateCancellation.Token;

        if (IsLocalCommittedReviewMode())
        {
            UpdatePullRequestReviewState(null);
            PullRequestPreviewTextBlock.IsVisible = false;
            PullRequestPreviewTextBlock.Text = string.Empty;
            PullRequestMetadataTextBlock.IsVisible = false;
            PullRequestMetadataTextBlock.Text = string.Empty;
            UpdateActionButtonStates();
            return;
        }

        var urlText = PullRequestUrlTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(urlText))
        {
            UpdatePullRequestReviewState(null);
            UpdateActionButtonStates();
            PullRequestPreviewTextBlock.IsVisible = false;
            PullRequestPreviewTextBlock.Text = string.Empty;
            PullRequestMetadataTextBlock.IsVisible = false;
            PullRequestMetadataTextBlock.Text = string.Empty;
            return;
        }

        if (!BitbucketPrUrlParser.TryParse(urlText, out var pullRequest, out _))
        {
            UpdatePullRequestReviewState(null);
            UpdateActionButtonStates();
            PullRequestPreviewTextBlock.Text = "Preview: Invalid Bitbucket PR URL";
            PullRequestPreviewTextBlock.IsVisible = true;
            PullRequestMetadataTextBlock.IsVisible = false;
            PullRequestMetadataTextBlock.Text = string.Empty;
            return;
        }

        PullRequestPreviewTextBlock.Text = $"Preview: {pullRequest.ProjectKey}/{pullRequest.RepoSlug} PR #{pullRequest.PullRequestId} (loading details...)";
        PullRequestPreviewTextBlock.IsVisible = true;
        PullRequestMetadataTextBlock.IsVisible = false;
        PullRequestMetadataTextBlock.Text = string.Empty;

        var metadata = await m_pullRequestMetadataClient.TryGetMetadataAsync(pullRequest, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return;

        UpdatePullRequestReviewState(metadata);
        UpdateActionButtonStates();

        var previewPrefix = $"Preview: {pullRequest.ProjectKey}/{pullRequest.RepoSlug} PR #{pullRequest.PullRequestId}";

        if (metadata == null)
        {
            PullRequestPreviewTextBlock.Text = previewPrefix;
            PullRequestPreviewTextBlock.IsVisible = true;
            PullRequestMetadataTextBlock.IsVisible = false;
            PullRequestMetadataTextBlock.Text = string.Empty;
            return;
        }

        PullRequestPreviewTextBlock.Text = HasBranchInfo(metadata)
            ? $"{previewPrefix} ({metadata.SourceBranch} -> {metadata.TargetBranch})"
            : previewPrefix;
        PullRequestPreviewTextBlock.IsVisible = true;

        PullRequestMetadataTextBlock.Text = BuildPullRequestMetadataText(metadata);
        PullRequestMetadataTextBlock.IsVisible = true;

        if (m_previewPullRequestIsOpen == false)
            NotifyNonOpenPullRequestIfNeeded(pullRequest);
    }

    private static bool HasBranchInfo(BitbucketPullRequestMetadata metadata) =>
        metadata != null &&
        !string.IsNullOrWhiteSpace(metadata.SourceBranch) &&
        !string.IsNullOrWhiteSpace(metadata.TargetBranch);

    private static string BuildPullRequestMetadataText(BitbucketPullRequestMetadata metadata)
    {
        if (metadata == null)
            return string.Empty;

        var title = string.IsNullOrWhiteSpace(metadata.Title) ? "(no title)" : metadata.Title.Trim();
        var author = string.IsNullOrWhiteSpace(metadata.Author) ? "Unknown author" : metadata.Author.Trim();
        var state = FormatPullRequestState(metadata.State);

        return $"Title: {title} | Author: {author} | State: {state}";
    }

    private static string FormatMetadataText(string value) =>
        string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();

    private void SetBusyState(bool isBusy)
    {
        m_busy = isBusy;
        BusyIndicatorSpinner.IsVisible = isBusy;
        UpdateActionButtonStates();
    }

    private bool IsLocalCommittedReviewMode() => ReviewModeToggleSwitch.IsChecked == true;

    private void ApplyReviewModeUi()
    {
        var isLocalMode = IsLocalCommittedReviewMode();

        PullRequestUrlLabelTextBlock.IsVisible = !isLocalMode;
        PullRequestUrlTextBox.IsVisible = !isLocalMode;
        OpenPullRequestButton.IsVisible = !isLocalMode;
        LocalReviewOptionsGrid.IsVisible = isLocalMode;

        if (isLocalMode)
        {
            PullRequestPreviewTextBlock.IsVisible = false;
            PullRequestMetadataTextBlock.IsVisible = false;
        }

        PrepareReviewButton.Content = isLocalMode ? "Review Local" : "Review PR";
    }

    private void UpdateActionButtonStates()
    {
        var canReviewCurrentPullRequest = m_previewPullRequestIsOpen != false;
        var isLocalMode = IsLocalCommittedReviewMode();
        m_latestSolutionPath = ResolveAvailableSolutionPath();
        PrepareReviewButton.IsEnabled = !m_busy &&
                                        m_isGitAvailable &&
                                        (isLocalMode
                                            ? HasValidLocalPrepareInputs()
                                            : canReviewCurrentPullRequest && HasValidPullRequestPrepareInputs());
        OpenPullRequestButton.IsEnabled = !m_busy && !isLocalMode && HasValidPullRequestInput();
        OpenSolutionButton.IsEnabled = !m_busy &&
                                      !string.IsNullOrWhiteSpace(m_latestSolutionPath) &&
                                      File.Exists(m_latestSolutionPath);
    }

    private string ResolveAvailableSolutionPath()
    {
        if (!string.IsNullOrWhiteSpace(m_latestSolutionPath) && File.Exists(m_latestSolutionPath))
            return m_latestSolutionPath;

        if (!string.IsNullOrWhiteSpace(m_latestReviewWorktreePath) && Directory.Exists(m_latestReviewWorktreePath))
        {
            var worktreeSolution = FindTopLevelSolutionFile(m_latestReviewWorktreePath);
            if (!string.IsNullOrWhiteSpace(worktreeSolution) && File.Exists(worktreeSolution))
                return worktreeSolution;
        }

        var localRepositoryPath = LocalRepositoryFolderTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(localRepositoryPath) && Directory.Exists(localRepositoryPath))
        {
            var localSolution = FindTopLevelSolutionFile(localRepositoryPath);
            if (!string.IsNullOrWhiteSpace(localSolution) && File.Exists(localSolution))
                return localSolution;
        }

        var repositoryRootPath = RepositoryRootTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(repositoryRootPath) && Directory.Exists(repositoryRootPath))
        {
            var rootSolution = FindTopLevelSolutionFile(repositoryRootPath);
            if (!string.IsNullOrWhiteSpace(rootSolution) && File.Exists(rootSolution))
                return rootSolution;
        }

        return null;
    }

    private bool HasValidPullRequestInput()
    {
        var prUrlText = PullRequestUrlTextBox.Text?.Trim();
        return !string.IsNullOrWhiteSpace(prUrlText) &&
               BitbucketPrUrlParser.TryParse(prUrlText, out _, out _);
    }

    private bool HasValidPullRequestPrepareInputs()
    {
        var repositoryRoot = RepositoryRootTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return false;

        var prUrlText = PullRequestUrlTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prUrlText))
            return false;

        return BitbucketPrUrlParser.TryParse(prUrlText, out _, out _);
    }

    private bool HasValidLocalPrepareInputs()
    {
        var localRepositoryPath = LocalRepositoryFolderTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(localRepositoryPath) || !Directory.Exists(localRepositoryPath))
            return false;

        if (!LooksLikeGitRepository(localRepositoryPath))
            return false;

        var baseBranch = LocalBaseBranchTextBox.Text?.Trim();
        return !string.IsNullOrWhiteSpace(baseBranch);
    }

    private void SetStatus(string status)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetStatus(status));
            return;
        }

        StatusTextBlock.Text = status;
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendLog(message));
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var normalized = (message ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var lineText = index == 0 ? $"[{timestamp}] {lines[index]}" : lines[index];
            var brush = SelectLogLineBrush(lineText);
            var entry = new LogLineEntry(lineText, brush);
            m_logLines.Add(entry);
            LogListBox.ScrollIntoView(entry);
        }
    }

    private static IBrush SelectLogLineBrush(string line)
    {
        if (ContainsErrorMarker(line))
            return ErrorLogBrush;
        if (ContainsPassMarker(line))
            return PassLogBrush;
        if (ContainsWarningMarker(line))
            return WarningLogBrush;
        if (ContainsHintMarker(line))
            return HintLogBrush;

        return HasTimestampPrefix(line) ? TimestampedLogBrush : DetailLogBrush;
    }

    private static bool ContainsErrorMarker(string line) =>
        line?.Contains("ERROR", StringComparison.OrdinalIgnoreCase) == true ||
        line?.Contains("fatal", StringComparison.OrdinalIgnoreCase) == true ||
        line?.Contains("IMPORTANT:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsWarningMarker(string line) =>
        line?.Contains("CHECK WARNING:", StringComparison.OrdinalIgnoreCase) == true ||
        line?.Contains("SUGGESTION:", StringComparison.OrdinalIgnoreCase) == true ||
        line?.Contains("WARNING:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsHintMarker(string line) =>
        line?.Contains("HINT:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsPassMarker(string line) =>
        line?.Contains("CHECK PASS:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasTimestampPrefix(string line) =>
        !string.IsNullOrEmpty(line) &&
        line.Length >= 10 &&
        line[0] == '[' &&
        line[3] == ':' &&
        line[6] == ':' &&
        line[9] == ']';

    private async void CopyLogLineButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: LogLineEntry entry })
            return;

        var clipboard = Clipboard;
        if (clipboard == null)
            return;

        try
        {
            await clipboard.SetTextAsync(entry.Text ?? string.Empty);
            SetStatus("Log line copied to clipboard.");
        }
        catch (Exception exception)
        {
            AppendLog($"WARNING: Could not copy log line. {exception.Message}");
        }
    }

    private void LogListBox_OnPointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount < 2 || !TryGetLogEntryFromSource(e.Source, out var entry))
            return;

        if (IsClickInsideCopyButton(e.Source))
            return;

        if (!TryParseLogLocation(entry.Text, out var filePath, out var lineNumber))
            return;

        if (!TryResolveLogPath(filePath, out var resolvedPath))
        {
            SetStatus($"Could not resolve file path: {filePath}");
            return;
        }

        if (!TryLaunchVsCodeAtLine(resolvedPath, lineNumber, out var launchError))
        {
            SetStatus(launchError);
            return;
        }

        SetStatus($"Opened in VS Code: {Path.GetFileName(resolvedPath)}:{lineNumber}");
        e.Handled = true;
    }

    private static bool IsClickInsideCopyButton(object source)
    {
        if (source is not InputElement sourceElement)
            return false;

        var button = sourceElement.FindAncestorOfType<Button>() ?? sourceElement as Button;
        return button?.Classes.Contains("logCopyButton") == true;
    }

    private static bool TryGetLogEntryFromSource(object source, out LogLineEntry entry)
    {
        entry = null;

        if (source is not InputElement sourceElement)
            return false;

        if (sourceElement.DataContext is LogLineEntry directEntry)
        {
            entry = directEntry;
            return true;
        }

        var listBoxItem = sourceElement.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem?.DataContext is not LogLineEntry ancestorEntry)
            return false;

        entry = ancestorEntry;
        return true;
    }

    private static bool TryParseLogLocation(string text, out string filePath, out int lineNumber)
    {
        filePath = null;
        lineNumber = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = LogLocationRegex.Match(text);
        if (!match.Success)
            return false;

        var pathValue = match.Groups["path"].Value.Trim();
        if (!int.TryParse(match.Groups["line"].Value, out lineNumber) || lineNumber < 1)
            return false;

        filePath = pathValue;
        return true;
    }

    private bool TryResolveLogPath(string pathFromLog, out string resolvedPath)
    {
        resolvedPath = null;

        if (string.IsNullOrWhiteSpace(pathFromLog))
            return false;

        var normalizedRelativePath = pathFromLog
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalizedRelativePath))
        {
            if (!File.Exists(normalizedRelativePath))
                return false;

            resolvedPath = normalizedRelativePath;
            return true;
        }

        if (string.IsNullOrWhiteSpace(m_latestReviewWorktreePath))
            return false;

        var candidatePath = Path.Combine(m_latestReviewWorktreePath, normalizedRelativePath);
        if (!File.Exists(candidatePath))
            return false;

        resolvedPath = candidatePath;
        return true;
    }

    private bool TryLaunchVsCodeAtLine(string filePath, int lineNumber, out string error)
    {
        error = null;
        if (!TryDetectVsCode(out var vsCodePath, out var useCommandShell))
        {
            error = "VS Code was not detected. Install VS Code and add 'code' to PATH.";
            return false;
        }

        var target = $"{filePath}:{lineNumber}";
        Exception lastException = null;
        foreach (var startInfo in BuildVsCodeLaunchAttempts(vsCodePath, useCommandShell, target))
        {
            try
            {
                var process = Process.Start(startInfo);
                if (process != null)
                    return true;
            }
            catch (Exception exception)
            {
                lastException = exception;
            }
        }

        error = lastException == null
            ? "VS Code was detected but could not be launched."
            : $"VS Code was detected but could not be launched. {lastException.Message}";
        return false;
    }

    private bool TryDetectVsCode(out string vsCodePath, out bool useCommandShell)
    {
        if (m_vsCodeDetectionAttempted)
        {
            vsCodePath = m_vsCodeExecutablePath;
            useCommandShell = m_vsCodeUsesCommandShell;
            return !string.IsNullOrWhiteSpace(vsCodePath);
        }

        m_vsCodeDetectionAttempted = true;
        foreach (var candidatePath in GetVsCodeCandidates())
        {
            if (!File.Exists(candidatePath))
                continue;

            m_vsCodeExecutablePath = candidatePath;
            m_vsCodeUsesCommandShell = candidatePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                                       candidatePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            break;
        }

        vsCodePath = m_vsCodeExecutablePath;
        useCommandShell = m_vsCodeUsesCommandShell;
        return !string.IsNullOrWhiteSpace(vsCodePath);
    }

    private static IEnumerable<string> GetVsCodeCandidates()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmedDirectory = directory.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(trimmedDirectory))
                    continue;

                if (isWindows)
                {
                    yield return Path.Combine(trimmedDirectory, "code.cmd");
                    yield return Path.Combine(trimmedDirectory, "code.exe");
                    yield return Path.Combine(trimmedDirectory, "code.bat");
                    continue;
                }

                yield return Path.Combine(trimmedDirectory, "code");
            }
        }

        yield return "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code";
        yield return "/Applications/Visual Studio Code - Insiders.app/Contents/Resources/app/bin/code";
        yield return "/usr/local/bin/code";
        yield return "/opt/homebrew/bin/code";
        yield return "/snap/bin/code";

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "Microsoft VS Code", "Code.exe");

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return Path.Combine(programFilesX86, "Microsoft VS Code", "Code.exe");
    }

    private static IEnumerable<ProcessStartInfo> BuildVsCodeLaunchAttempts(string vsCodePath, bool useCommandShell, string target)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (useCommandShell)
            {
                yield return new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c \"\"{vsCodePath}\" --goto \"{target}\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                yield return new ProcessStartInfo(vsCodePath)
                {
                    Arguments = $"--goto \"{target}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                yield return new ProcessStartInfo("cmd.exe")
                {
                    Arguments = $"/c \"\"{vsCodePath}\" --goto \"{target}\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            yield break;
        }

        yield return new ProcessStartInfo(vsCodePath)
        {
            Arguments = $"--goto \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }
    
    private sealed class LogLineEntry
    {
        public LogLineEntry(string text, IBrush foreground)
        {
            Text = text;
            Foreground = foreground;
        }

        public string Text { get; }

        public IBrush Foreground { get; }
    }

    private void RepositoryRootTextBox_OnLostFocus(object sender, RoutedEventArgs e) =>
        PersistRepositoryRootPath(RepositoryRootTextBox.Text);

    private void LocalRepositoryFolderTextBox_OnLostFocus(object sender, RoutedEventArgs e) =>
        PersistLocalReviewRepositoryPath(LocalRepositoryFolderTextBox.Text);

    private void LocalBaseBranchTextBox_OnLostFocus(object sender, RoutedEventArgs e) =>
        PersistLocalReviewBaseBranch(LocalBaseBranchTextBox.Text);

    private async void MainWindow_OnOpened(object sender, EventArgs e)
    {
        await EnsureGitIsAvailableOnStartupAsync();
        if (!m_isGitAvailable)
            return;

        await RunStartupCodeReviewCleanupAsync();
        await TryPrefillPullRequestUrlFromClipboardAsync();
    }

    private async void MainWindow_OnActivated(object sender, EventArgs e) =>
        await TryPrefillPullRequestUrlFromClipboardAsync();

    private void MainWindow_OnClosing(object sender, WindowClosingEventArgs e)
    {
        m_previewUpdateCancellation?.Cancel();
        m_previewUpdateCancellation?.Dispose();
        m_previewUpdateCancellation = null;
        m_pullRequestMetadataClient.Dispose();

        PersistRepositoryRootPath(RepositoryRootTextBox.Text);
        PersistLocalReviewRepositoryPath(LocalRepositoryFolderTextBox.Text);
        PersistLocalReviewBaseBranch(LocalBaseBranchTextBox.Text);
        PersistUseLocalCommittedReview(IsLocalCommittedReviewMode());
        m_settings.Dispose();
    }

    private async Task EnsureGitIsAvailableOnStartupAsync()
    {
        if (m_gitAvailabilityChecked)
            return;

        m_gitAvailabilityChecked = true;

        string failureDetails;
        try
        {
            var versionResult = await m_gitCommandRunner.RunAsync(AppContext.BaseDirectory, "--version");
            if (versionResult.IsSuccess)
            {
                m_isGitAvailable = true;
                return;
            }

            failureDetails = versionResult.GetCombinedOutput();
        }
        catch (Exception exception)
        {
            failureDetails = exception.Message;
        }

        m_isGitAvailable = false;
        UpdateActionButtonStates();

        const string actionText = "Install Git for Windows (https://git-scm.com/download/win), ensure git.exe is on PATH, then restart ReviewG33k.";
        SetStatus("Git is missing. Install Git and restart ReviewG33k.");
        AppendLog("ERROR: Git is not available.");
        if (!string.IsNullOrWhiteSpace(failureDetails))
            AppendLog($"Details: {failureDetails}");

        var dialogDetail = string.IsNullOrWhiteSpace(failureDetails)
            ? actionText
            : $"{actionText}{Environment.NewLine}{Environment.NewLine}Details: {failureDetails}";
        DialogService.Instance.ShowMessage("Git not found", dialogDetail, null);
    }

    private async Task RunStartupCodeReviewCleanupAsync()
    {
        var repositoryRoot = RepositoryRootTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return;

        await ExecuteBusyActionAsync(
            "Clearing previous CodeReview folders...",
            async () =>
            {
                await m_orchestrator.ClearCodeReviewFolderAsync(repositoryRoot, AppendLog, logWhenMissing: false);
                SetStatus("Ready.");
            });
    }

    private async Task TryPrefillPullRequestUrlFromClipboardAsync()
    {
        if (IsLocalCommittedReviewMode())
            return;

        if (!string.IsNullOrWhiteSpace(PullRequestUrlTextBox.Text))
            return;

        if (Clipboard == null)
            return;

        string clipboardText;
        try
        {
            clipboardText = await Clipboard.GetTextAsync();
        }
        catch
        {
            return;
        }

        if (!BitbucketPrUrlParser.TryParse(clipboardText, out var pullRequest, out _))
            return;

        PullRequestUrlTextBox.Text = pullRequest.SourceUrl;
        SetStatus("Pull request URL loaded from clipboard.");
    }

    private void PersistRepositoryRootPath(string repositoryRootPath)
    {
        var normalizedPath = repositoryRootPath?.Trim();
        if (string.Equals(m_settings.RepositoryRootPath, normalizedPath, StringComparison.Ordinal))
            return;

        m_settings.RepositoryRootPath = normalizedPath;

        try
        {
            m_settings.Save();
        }
        catch (Exception exception)
        {
            AppendLog($"Warning: could not save app settings. {exception.Message}");
        }
    }

    private void PersistLocalReviewRepositoryPath(string localRepositoryPath)
    {
        var normalizedPath = localRepositoryPath?.Trim();
        if (string.Equals(m_settings.LocalReviewRepositoryPath, normalizedPath, StringComparison.Ordinal))
            return;

        m_settings.LocalReviewRepositoryPath = normalizedPath;
        SaveSettingsSafely();
    }

    private void PersistLocalReviewBaseBranch(string baseBranch)
    {
        var normalizedBranch = string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch.Trim();
        if (string.Equals(m_settings.LocalReviewBaseBranch, normalizedBranch, StringComparison.Ordinal))
            return;

        m_settings.LocalReviewBaseBranch = normalizedBranch;
        SaveSettingsSafely();
    }

    private void PersistUseLocalCommittedReview(bool useLocalCommittedReview)
    {
        if (m_settings.UseLocalCommittedReview == useLocalCommittedReview)
            return;

        m_settings.UseLocalCommittedReview = useLocalCommittedReview;
        SaveSettingsSafely();
    }

    private void SaveSettingsSafely()
    {
        try
        {
            m_settings.Save();
        }
        catch (Exception exception)
        {
            AppendLog($"Warning: could not save app settings. {exception.Message}");
        }
    }

    private static bool LooksLikeGitRepository(string folderPath) =>
        !string.IsNullOrWhiteSpace(folderPath) &&
        (Directory.Exists(Path.Combine(folderPath, ".git")) || File.Exists(Path.Combine(folderPath, ".git")));

    private static string FindTopLevelSolutionFile(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
            return null;

        var rootDepth = rootFolder.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);
        return Directory
            .EnumerateFiles(rootFolder, "*.sln", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                Depth = path.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar) - rootDepth
            })
            .OrderBy(candidate => candidate.Depth)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private void OpenPullRequestButton_OnClick(object sender, RoutedEventArgs e)
    {
        var prUrlText = PullRequestUrlTextBox.Text?.Trim();
        if (!BitbucketPrUrlParser.TryParse(prUrlText, out var pullRequest, out var parseError))
        {
            SetStatus(parseError);
            AppendLog($"Input error: {parseError}");
            DialogService.Instance.ShowMessage("Invalid pull request URL", parseError, null);
            return;
        }

        OpenFileWithShell(pullRequest.SourceUrl);
        SetStatus("Pull request opened in browser.");
    }

    private void UpdatePullRequestReviewState(BitbucketPullRequestMetadata metadata)
    {
        if (metadata == null)
        {
            m_previewPullRequestState = null;
            m_previewPullRequestIsOpen = null;
            return;
        }

        m_previewPullRequestState = metadata.State?.Trim();
        m_previewPullRequestIsOpen = string.IsNullOrWhiteSpace(m_previewPullRequestState)
            ? null
            : string.Equals(m_previewPullRequestState, "OPEN", StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyNonOpenPullRequestIfNeeded(BitbucketPullRequestReference pullRequest)
    {
        if (pullRequest == null || m_previewPullRequestIsOpen != false)
            return;

        var state = FormatPullRequestState(m_previewPullRequestState);
        var noticeKey = $"{pullRequest.SourceUrl}|{state}";
        if (string.Equals(noticeKey, m_lastNonOpenPullRequestNoticeKey, StringComparison.Ordinal))
            return;

        m_lastNonOpenPullRequestNoticeKey = noticeKey;
        var message = $"PR #{pullRequest.PullRequestId} is {state}. Review checkout requires an OPEN pull request.";
        AppendLog($"WARNING: {message}");
        SetStatus(message);
        DialogService.Instance.ShowMessage(
            "Pull request is not open",
            $"{message}{Environment.NewLine}{Environment.NewLine}You can still click 'Open PR' to inspect it in Bitbucket.",
            null);
    }

    private static string FormatPullRequestState(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return "N/A";

        return state.Trim().ToUpperInvariant();
    }

    private void OpenSolutionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(m_latestSolutionPath) || !File.Exists(m_latestSolutionPath))
        {
            UpdateActionButtonStates();
            SetStatus("No solution is available from the latest review.");
            return;
        }

        OpenFileWithShell(m_latestSolutionPath);
        SetStatus("Opened solution in default application.");
        AppendLog($"Opened solution: {m_latestSolutionPath}");
    }

    private static void OpenFileWithShell(string filePath)
    {
        Process.Start(new ProcessStartInfo(filePath)
        {
            UseShellExecute = true
        });
    }

    private static bool TryExtractPullRequestUrl(IDataObject data, out string url)
    {
        url = null;

        foreach (var candidateText in GetCandidateTextValues(data))
        {
            if (!BitbucketPrUrlParser.TryParse(candidateText, out var pr, out _))
                continue;

            url = pr.SourceUrl;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> GetCandidateTextValues(IDataObject data)
    {
        if (data == null)
            yield break;

        if (data.Contains(DataFormats.Text))
        {
            var textObject = data.Get(DataFormats.Text);
            foreach (var value in ExtractStrings(textObject))
                yield return value;
        }

        foreach (var format in data.GetDataFormats())
        {
            object formatValue;
            try
            {
                formatValue = data.Get(format);
            }
            catch
            {
                continue;
            }

            foreach (var value in ExtractStrings(formatValue))
                yield return value;
        }
    }

    private static IEnumerable<string> ExtractStrings(object value)
    {
        switch (value)
        {
            case null:
                yield break;
            case string text when !string.IsNullOrWhiteSpace(text):
                yield return text;
                yield break;
            case Uri uri:
                yield return uri.ToString();
                yield break;
            case byte[] bytes when bytes.Length > 0:
                yield return DecodeBytes(bytes);
                yield break;
            case MemoryStream stream when stream.Length > 0:
                yield return DecodeBytes(stream.ToArray());
                yield break;
            case IEnumerable<object> enumerable:
                foreach (var item in enumerable)
                {
                    foreach (var extracted in ExtractStrings(item))
                        yield return extracted;
                }

                yield break;
            default:
                var stringValue = value.ToString();
                if (!string.IsNullOrWhiteSpace(stringValue))
                    yield return stringValue;

                yield break;
        }
    }

    private static string DecodeBytes(byte[] bytes)
    {
        var utf8 = Encoding.UTF8.GetString(bytes);
        if (BitbucketPrUrlParser.TryParse(utf8, out _, out _))
            return utf8;

        return Encoding.Unicode.GetString(bytes);
    }
}
