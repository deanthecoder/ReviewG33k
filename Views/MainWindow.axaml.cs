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
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
using DTC.Core.Extensions;
using DTC.Core.UI;
using ReviewG33k.Models;
using ReviewG33k.Services;
using ReviewG33k.Services.Checks.Support;
using ReviewG33k.ViewModels;

namespace ReviewG33k.Views;

public partial class MainWindow : Window
{
    private const string ApplicationTitle = "ReviewG33k";
    private enum ReviewMode
    {
        PullRequest,
        LocalCommittedChanges,
        LocalUncommittedChanges
    }

    private const string CheckErrorInfoPrefix = "CHECK ERROR:";
    private static readonly Regex LogLocationRegex = new(@"\[(?<path>.+?\.[^:\]]+):(?<line>\d+)\]", RegexOptions.Compiled);
    private static readonly IBrush TimestampedLogBrush = Brushes.Gainsboro;
    private static readonly IBrush DetailLogBrush = Brushes.Gray;
    private static readonly IBrush ErrorLogBrush = Brushes.IndianRed;
    private static readonly IBrush WarningLogBrush = Brushes.Orange;
    private static readonly IBrush HintLogBrush = Brushes.LightSteelBlue;
    private static readonly IBrush PassLogBrush = Brushes.LimeGreen;

    private readonly GitCommandRunner m_gitCommandRunner = new();
    private readonly CodeLocationOpener m_codeLocationOpener = new();
    private readonly CodeReviewOrchestrator m_orchestrator;
    private readonly CodeSmellReportAnalyzer m_codeSmellReportAnalyzer;
    private readonly BitbucketPullRequestMetadataClient m_pullRequestMetadataClient = new();
    private readonly Settings m_settings = Settings.Instance;
    private readonly MainWindowViewModel m_viewModel;
    private readonly ObservableCollection<LogLineEntry> m_logLines = [];
    private CancellationTokenSource m_previewUpdateCancellation;
    private CancellationTokenSource m_busyActionCancellation;
    private bool m_busyCancellationRequested;
    private BitbucketPullRequestReference m_latestPullRequest;
    private string m_latestReviewWorktreePath;
    private string m_latestSolutionPath;
    private string m_lastNonOpenPullRequestNoticeKey;
    private string m_vsCodeExecutablePath;
    private bool m_isGitAvailable = true;
    private bool m_gitAvailabilityChecked;
    private bool m_vsCodeDetectionAttempted;
    private bool m_vsCodeUsesCommandShell;
    private bool m_normalizingPullRequestUrl;
    private Dictionary<string, CodeReviewChangedFile> m_localReviewChangedFilesByPath;
    private string m_localReviewCacheRepositoryPath;
    private string m_localReviewCacheBaseBranch;
    private ReviewMode? m_localReviewCacheMode;

    public MainWindow()
    {
        m_orchestrator = new CodeReviewOrchestrator(m_gitCommandRunner);
        m_codeSmellReportAnalyzer = new CodeSmellReportAnalyzer(m_gitCommandRunner);
        m_viewModel = new MainWindowViewModel(m_settings);
        InitializeComponent();
        DataContext = m_viewModel;
        Title = BuildWindowTitle();
        PullRequestUrlTextBox.AddHandler(DragDrop.DragOverEvent, PullRequestUrlTextBox_OnDragOver);
        PullRequestUrlTextBox.AddHandler(DragDrop.DropEvent, PullRequestUrlTextBox_OnDrop);
        PullRequestUrlTextBox.TextChanged += PullRequestUrlTextBox_OnTextChanged;
        RepositoryRootTextBox.TextChanged += RepositoryRootTextBox_OnTextChanged;
        LocalRepositoryFolderTextBox.TextChanged += LocalRepositoryFolderTextBox_OnTextChanged;
        LocalRepositoryFolderTextBox.LostFocus += LocalRepositoryFolderTextBox_OnLostFocus;
        LogListBox.AddHandler(PointerPressedEvent, LogListBox_OnPointerPressed, RoutingStrategies.Bubble);
        Opened += MainWindow_OnOpened;
        Activated += MainWindow_OnActivated;
        Closing += MainWindow_OnClosing;

        var persistedBaseBranch = NormalizeBranchName(m_viewModel.LocalBaseBranch) ?? "main";
        SetLocalBaseBranchOptions([persistedBaseBranch], persistedBaseBranch);
        EnsureLocalBaseBranchSelection();
        LogListBox.ItemsSource = m_logLines;

        _ = UpdatePullRequestPreviewAsync();
        UpdateActionButtonStates();
    }

    private async void BrowseRepositoryRootButton_OnClick(object sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null)
            return;

        var startLocation = await GetStartFolderAsync(topLevel, m_viewModel.RepositoryRootPath);

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
            m_viewModel.RepositoryRootPath = path;
    }

    private async void BrowseLocalRepositoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null)
            return;

        var startLocation = await GetStartFolderAsync(topLevel, m_viewModel.LocalRepositoryPath);
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

        m_viewModel.LocalRepositoryPath = path;
        await TryAutoDetectLocalBaseBranchAsync(logWhenUpdated: true);
    }

    private void ReviewModeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isLocalMode = IsAnyLocalReviewMode();
        if (isLocalMode)
        {
            m_latestPullRequest = null;
            m_viewModel.UpdatePullRequestReviewState(null, null);
        }

        EnsureLocalBaseBranchSelection();
        _ = UpdatePullRequestPreviewAsync();
        UpdateActionButtonStates();
    }

    private static string BuildWindowTitle()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(MainWindow).Assembly;
        var displayVersion = assembly.GetDisplayVersion();
        return string.IsNullOrWhiteSpace(displayVersion)
            ? ApplicationTitle
            : $"{ApplicationTitle} v{displayVersion}";
    }

    private static void PullRequestUrlTextBox_OnDragOver(object sender, DragEventArgs e)
    {
        e.DragEffects = TryExtractPullRequestUrl(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void PullRequestUrlTextBox_OnDrop(object sender, DragEventArgs e)
    {
        if (TryExtractPullRequestUrl(e.Data, out var url))
        {
            m_viewModel.PullRequestUrl = url;
            SetStatus("Pull request URL captured from drop.");
        }

        e.Handled = true;
    }

    private void PullRequestUrlTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        OnInputTextChanged();

    private void RepositoryRootTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        UpdateActionButtonStates();

    private void LocalRepositoryFolderTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        InvalidateLocalReviewChangedFilesCache();

    private void LocalBaseBranchComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidateLocalReviewChangedFilesCache();
    }

    private async void PrepareReviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (IsLocalCommittedReviewMode())
        {
            await PrepareLocalCommittedReviewAsync();
            return;
        }
        if (IsLocalUncommittedReviewMode())
        {
            await PrepareLocalUncommittedReviewAsync();
            return;
        }

        if (!TryGetPullRequestInputs(out var repositoryRoot, out var prUrlText))
            return;

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
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                m_latestSolutionPath = null;
                UpdateActionButtonStates();

                var metadata = await m_pullRequestMetadataClient.TryGetMetadataAsync(pullRequest, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                m_viewModel.UpdatePullRequestReviewState(metadata?.Title, metadata?.State);
                UpdateActionButtonStates();
                if (m_viewModel.PreviewPullRequestIsOpen == false)
                {
                    NotifyNonOpenPullRequestIfNeeded(pullRequest);
                    return;
                }

                var changedPaths = await m_pullRequestMetadataClient.TryGetChangedPathsAsync(pullRequest, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                AppendLog($"PR detected: {pullRequest.SourceUrl}");
                AppendLog($"PR title: {FormatMetadataText(metadata?.Title)}");
                AppendLog($"PR author: {FormatMetadataText(metadata?.Author)}");
                AppendLog($"PR modified files: {(changedPaths.Count > 0 ? changedPaths.Count.ToString() : "N/A")}");

                var result = await m_orchestrator.PrepareReviewAsync(repositoryRoot, pullRequest, changedPaths, AppendLog, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
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
                report = await RunCodeSmellScanAsync(result.ReviewWorktreePath, metadata?.TargetBranch, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                SetStatus("Review complete.");
            });

        if (report?.Findings.Count > 0)
            await ShowReviewResultsWindowAsync(report);
    }

    private async Task PrepareLocalCommittedReviewAsync()
    {
        if (!TryGetLocalRepositoryInput(out var localRepositoryPath))
            return;
        await TryAutoDetectLocalBaseBranchAsync(logWhenUpdated: true);
        if (!TryGetLocalCommittedReviewInputs(out localRepositoryPath, out var baseBranch))
            return;

        InvalidateLocalReviewChangedFilesCache();
        baseBranch = await ResolveLocalBaseBranchAsync(localRepositoryPath, baseBranch, logWhenChanged: true);
        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            SetStatus("Enter a base branch (for example: main).");
            DialogService.Instance.ShowMessage("Base branch required", "Enter the branch to compare against (for example: main or develop).", null);
            return;
        }

        SelectLocalBaseBranch(baseBranch);

        m_latestPullRequest = null;
        m_viewModel.UpdatePullRequestReviewState(null, null);

        CodeSmellReport report = null;
        await ExecuteBusyActionAsync(
            "Reviewing local committed changes...",
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                m_latestReviewWorktreePath = localRepositoryPath;
                m_latestSolutionPath = RepositoryUtilities.FindTopLevelSolutionFile(localRepositoryPath);
                UpdateActionButtonStates();

                AppendLog($"Local review repository: {localRepositoryPath}");
                AppendLog($"Comparing current branch changes against: origin/{baseBranch}");

                AppendLog(string.IsNullOrWhiteSpace(m_latestSolutionPath) ? "No .sln file found in local repository." : $"Solution selected: {m_latestSolutionPath}");

                var changedFileSource = new GitBranchComparisonChangedFileSource(
                    m_gitCommandRunner,
                    localRepositoryPath,
                    baseBranch,
                    fetchTargetBranch: true);

                var sourceResult = await changedFileSource.LoadAsync(AppendLog);
                cancellationToken.ThrowIfCancellationRequested();
                SetLocalReviewChangedFilesCache(localRepositoryPath, baseBranch, ReviewMode.LocalCommittedChanges, sourceResult?.Files);
                report = await RunCodeSmellScanAsync(sourceResult, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                SetStatus("Local review complete.");
            });

        if (report?.Findings.Count > 0)
            await ShowReviewResultsWindowAsync(report);
    }

    private async Task PrepareLocalUncommittedReviewAsync()
    {
        if (!TryGetLocalRepositoryInput(out var localRepositoryPath))
            return;

        InvalidateLocalReviewChangedFilesCache();

        m_latestPullRequest = null;
        m_viewModel.UpdatePullRequestReviewState(null, null);

        CodeSmellReport report = null;
        await ExecuteBusyActionAsync(
            "Reviewing local uncommitted changes...",
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                m_latestReviewWorktreePath = localRepositoryPath;
                m_latestSolutionPath = RepositoryUtilities.FindTopLevelSolutionFile(localRepositoryPath);
                UpdateActionButtonStates();

                AppendLog($"Local review repository: {localRepositoryPath}");
                AppendLog("Comparing local uncommitted/untracked changes against: HEAD");

                AppendLog(string.IsNullOrWhiteSpace(m_latestSolutionPath) ? "No .sln file found in local repository." : $"Solution selected: {m_latestSolutionPath}");

                var changedFileSource = new GitWorkingTreeChangedFileSource(m_gitCommandRunner, localRepositoryPath);
                var sourceResult = await changedFileSource.LoadAsync(AppendLog);
                cancellationToken.ThrowIfCancellationRequested();
                SetLocalReviewChangedFilesCache(localRepositoryPath, null, ReviewMode.LocalUncommittedChanges, sourceResult?.Files);
                report = await RunCodeSmellScanAsync(sourceResult, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                SetStatus("Local review complete.");
            });

        if (report?.Findings.Count > 0)
            await ShowReviewResultsWindowAsync(report);
    }

    private async Task<CodeSmellReport> RunCodeSmellScanAsync(string reviewWorktreePath, string targetBranch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendLog("Code review scan starting...");
        SetBusyProgressIndeterminate();
        var report = await m_codeSmellReportAnalyzer.AnalyzeAsync(
            reviewWorktreePath,
            targetBranch,
            AppendLog,
            UpdateBusyProgress,
            ShouldIncludeFullModifiedFiles(),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ProcessCodeSmellReport(report);
    }

    private async Task<CodeSmellReport> RunCodeSmellScanAsync(CodeReviewChangedFileSourceResult sourceResult, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendLog("Code review scan starting...");
        SetBusyProgressIndeterminate();
        var report = await m_codeSmellReportAnalyzer.AnalyzeLoadedFilesAsync(
            sourceResult,
            AppendLog,
            UpdateBusyProgress,
            ShouldIncludeFullModifiedFiles(),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
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
        var failedRuleIds = GetFailedCheckRuleIds(report);
        foreach (var check in m_codeSmellReportAnalyzer.Checks)
        {
            if (failedRuleIds.Contains(check.RuleId))
                continue;

            var count = report.Findings.Count(finding => finding.RuleId.Equals(check.RuleId, StringComparison.OrdinalIgnoreCase));
            if (count == 0)
                AppendLog($"CHECK PASS: {check.DisplayName}");
        }
    }

    private static ISet<string> GetFailedCheckRuleIds(CodeSmellReport report)
    {
        var failedRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in report?.Info ?? [])
        {
            if (string.IsNullOrWhiteSpace(info) ||
                !info.StartsWith(CheckErrorInfoPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var openBracketIndex = info.IndexOf('[');
            var closeBracketIndex = info.IndexOf(']', openBracketIndex + 1);
            if (openBracketIndex < 0 || closeBracketIndex <= openBracketIndex + 1)
                continue;

            var ruleId = info[(openBracketIndex + 1)..closeBracketIndex].Trim();
            if (!string.IsNullOrWhiteSpace(ruleId))
                failedRuleIds.Add(ruleId);
        }

        return failedRuleIds;
    }

    private static bool DidAnalyzeChangedFiles(CodeSmellReport report) =>
        report?.Info?.Any(info => info.StartsWith("Code review scan: Analyzing ", StringComparison.OrdinalIgnoreCase)) == true;

    private async Task ShowReviewResultsWindowAsync(CodeSmellReport report)
    {
        var canOpenCodeLocation = m_codeLocationOpener.TargetDefinitions.Any(definition => IsOpenTargetAvailable(definition.Target));
        var canCommentInBitbucket = IsPullRequestReviewMode() && m_latestPullRequest != null;
        var canFixLocally = IsAnyLocalReviewMode();
        var reviewWindowPullRequestTitle = canCommentInBitbucket
            ? m_viewModel.PreviewPullRequestTitle
            : null;
        var findingFixer = canFixLocally ? new CodeReviewFixDispatcher(m_codeSmellReportAnalyzer.Checks) : null;
        var initialOpenTarget = GetConfiguredCodeOpenTarget();
        if (!IsOpenTargetAvailable(initialOpenTarget))
        {
            initialOpenTarget = m_codeLocationOpener.AllTargets
                .FirstOrDefault(IsOpenTargetAvailable);
        }

        var resultsWindow = new ReviewResultsWindow(
            report?.Findings ?? [],
            canOpenCodeLocation,
            canCommentInBitbucket,
            canFixLocally,
            findingFixer,
            OpenReviewFindingInVsCode,
            OpenReviewFindingAsync,
            IsOpenTargetAvailable,
            PersistCodeOpenTarget,
            initialOpenTarget,
            m_codeLocationOpener.TargetDefinitions,
            CommentOnReviewFindingAsync,
            ResolveReviewFindingPath,
            ResampleLocalFindingsForFileAsync,
            reviewWindowPullRequestTitle);
        await resultsWindow.ShowDialog(this);
    }

    private bool IsOpenTargetAvailable(CodeLocationOpenTarget target)
    {
        if (target == CodeLocationOpenTarget.Clipboard)
            return Clipboard != null;

        return m_codeLocationOpener.IsTargetAvailable(target);
    }

    private async Task<(bool Success, string Message)> OpenReviewFindingAsync(
        CodeSmellFinding finding,
        CodeLocationOpenTarget target)
    {
        if (finding == null)
            return (false, "No finding selected.");

        var lineNumber = finding.LineNumber > 0 ? finding.LineNumber : 1;
        if (!TryResolveLogFile(finding.FilePath, out var resolvedFile))
        {
            var error = $"Could not resolve file path: {finding.FilePath}";
            AppendLog($"WARNING: {error}");
            SetStatus(error);
            return (false, error);
        }

        if (target == CodeLocationOpenTarget.Clipboard)
        {
            if (Clipboard == null)
            {
                const string clipboardError = "Clipboard is unavailable.";
                AppendLog($"WARNING: {clipboardError}");
                SetStatus(clipboardError);
                return (false, clipboardError);
            }

            try
            {
                await Clipboard.SetTextAsync(resolvedFile.FullName);
                SetStatus($"Copied path to clipboard: {resolvedFile.FullName}");
                return (true, null);
            }
            catch (Exception exception)
            {
                var clipboardError = $"Failed to copy to clipboard: {exception.Message}";
                AppendLog($"WARNING: {clipboardError}");
                SetStatus(clipboardError);
                return (false, clipboardError);
            }
        }

        if (!m_codeLocationOpener.TryOpenAtLocation(target, resolvedFile.FullName, lineNumber, out var launchError))
        {
            AppendLog($"WARNING: {launchError}");
            SetStatus(launchError);
            return (false, launchError);
        }

        var targetName = m_codeLocationOpener.GetDisplayName(target);
        SetStatus($"Opened in {targetName}: {resolvedFile.Name}:{lineNumber}");
        return (true, null);
    }

    private CodeLocationOpenTarget GetConfiguredCodeOpenTarget()
    {
        var configuredValue = m_settings.CodeViewOpenTarget?.Trim();
        if (string.IsNullOrWhiteSpace(configuredValue))
            return CodeLocationOpenTarget.VsCode;

        if (configuredValue.Equals("VSCode", StringComparison.OrdinalIgnoreCase))
            return CodeLocationOpenTarget.VsCode;

        if (Enum.TryParse(configuredValue, ignoreCase: true, out CodeLocationOpenTarget parsedTarget))
            return parsedTarget;

        return CodeLocationOpenTarget.VsCode;
    }

    private void PersistCodeOpenTarget(CodeLocationOpenTarget target)
    {
        m_settings.CodeViewOpenTarget = target == CodeLocationOpenTarget.VsCode
            ? "VSCode"
            : target.ToString();

        try
        {
            m_settings.Save();
        }
        catch (Exception exception)
        {
            AppendLog($"WARNING: Failed to persist code open target. {exception.Message}");
        }
    }

    private void OpenReviewFindingInVsCode(CodeSmellFinding finding)
    {
        if (finding == null)
            return;

        var lineNumber = finding.LineNumber > 0 ? finding.LineNumber : 1;
        if (!TryResolveLogFile(finding.FilePath, out var resolvedFile))
        {
            var error = $"Could not resolve file path: {finding.FilePath}";
            AppendLog($"WARNING: {error}");
            SetStatus(error);
            return;
        }

        if (!TryLaunchVsCodeAtLine(resolvedFile.FullName, lineNumber, out var launchError))
        {
            AppendLog($"WARNING: {launchError}");
            SetStatus(launchError);
            return;
        }

        SetStatus($"Opened in VS Code: {resolvedFile.Name}:{lineNumber}");
    }

    private string ResolveReviewFindingPath(CodeSmellFinding finding)
    {
        if (finding == null)
            return null;

        return TryResolveLogFile(finding.FilePath, out var resolvedFile) ? resolvedFile.FullName : null;
    }

    private async Task<IReadOnlyList<CodeSmellFinding>> ResampleLocalFindingsForFileAsync(string filePath)
    {
        if (!IsAnyLocalReviewMode() || string.IsNullOrWhiteSpace(filePath))
            return [];

        var reviewMode = GetSelectedReviewMode();
        var localRepositoryPath = m_viewModel.LocalRepositoryPath?.Trim();
        var baseBranch = reviewMode == ReviewMode.LocalCommittedChanges
            ? GetSelectedLocalBaseBranch()
            : null;
        if (string.IsNullOrWhiteSpace(localRepositoryPath) || !localRepositoryPath.ToDir().Exists())
        {
            return [];
        }
        if (reviewMode == ReviewMode.LocalCommittedChanges && string.IsNullOrWhiteSpace(baseBranch))
            return [];

        var changedFilesByPath = await GetOrCreateLocalReviewChangedFilesCacheAsync(localRepositoryPath, baseBranch, reviewMode);
        if (changedFilesByPath == null || changedFilesByPath.Count == 0)
            return [];

        if (!changedFilesByPath.TryGetValue(RepositoryUtilities.NormalizeRepoPath(filePath), out var targetFile) || targetFile == null)
            return [];

        var refreshedTargetFile = await RefreshChangedFileFromDiskAsync(targetFile);
        if (refreshedTargetFile == null)
            return [];

        changedFilesByPath[RepositoryUtilities.NormalizeRepoPath(refreshedTargetFile.Path)] = refreshedTargetFile;

        var report = m_codeSmellReportAnalyzer.AnalyzeFiles(
            [refreshedTargetFile],
            ShouldIncludeFullModifiedFiles());
        return report.Findings
            .Where(finding => finding != null)
            .Where(finding => RepositoryUtilities.AreSameRepoPath(finding.FilePath, filePath))
            .OrderBy(finding => finding.LineNumber)
            .ToArray();
    }

    private async Task<Dictionary<string, CodeReviewChangedFile>> GetOrCreateLocalReviewChangedFilesCacheAsync(
        string localRepositoryPath,
        string baseBranch,
        ReviewMode reviewMode)
    {
        if (!IsLocalReviewCacheValid(localRepositoryPath, baseBranch, reviewMode))
        {
            ICodeReviewChangedFileSource changedFileSource =
                reviewMode == ReviewMode.LocalCommittedChanges
                    ? new GitBranchComparisonChangedFileSource(
                        m_gitCommandRunner,
                        localRepositoryPath,
                        baseBranch,
                        fetchTargetBranch: false)
                    : reviewMode == ReviewMode.LocalUncommittedChanges
                        ? new GitWorkingTreeChangedFileSource(
                            m_gitCommandRunner,
                            localRepositoryPath)
                        : null;
            if (changedFileSource == null)
                return null;

            var sourceResult = await changedFileSource.LoadAsync(AppendLog);
            SetLocalReviewChangedFilesCache(localRepositoryPath, baseBranch, reviewMode, sourceResult?.Files);
        }

        return m_localReviewChangedFilesByPath;
    }

    private bool IsLocalReviewCacheValid(string localRepositoryPath, string baseBranch, ReviewMode reviewMode)
    {
        if (m_localReviewChangedFilesByPath == null)
            return false;

        return string.Equals(m_localReviewCacheRepositoryPath, localRepositoryPath?.Trim(), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(m_localReviewCacheBaseBranch, baseBranch?.Trim(), StringComparison.OrdinalIgnoreCase) &&
               m_localReviewCacheMode == reviewMode;
    }

    private void SetLocalReviewChangedFilesCache(
        string localRepositoryPath,
        string baseBranch,
        ReviewMode reviewMode,
        IReadOnlyList<CodeReviewChangedFile> changedFiles)
    {
        m_localReviewChangedFilesByPath = (changedFiles ?? [])
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.Path))
            .GroupBy(file => RepositoryUtilities.NormalizeRepoPath(file.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        m_localReviewCacheRepositoryPath = localRepositoryPath?.Trim();
        m_localReviewCacheBaseBranch = baseBranch?.Trim();
        m_localReviewCacheMode = reviewMode;
    }

    private void InvalidateLocalReviewChangedFilesCache()
    {
        m_localReviewChangedFilesByPath = null;
        m_localReviewCacheRepositoryPath = null;
        m_localReviewCacheBaseBranch = null;
        m_localReviewCacheMode = null;
        UpdateActionButtonStates();
    }

    private static async Task<CodeReviewChangedFile> RefreshChangedFileFromDiskAsync(CodeReviewChangedFile sourceFile)
    {
        if (sourceFile == null || string.IsNullOrWhiteSpace(sourceFile.FullPath))
            return null;

        var fullPath = sourceFile.FullPath.ToFile();
        if (!fullPath.Exists())
            return null;

        var text = await File.ReadAllTextAsync(fullPath.FullName);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return new CodeReviewChangedFile(
            sourceFile.Status,
            sourceFile.Path,
            sourceFile.FullPath,
            text,
            lines,
            sourceFile.AddedLineNumbers);
    }

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

    private async Task ExecuteBusyActionAsync(string statusText, Func<CancellationToken, Task> action)
    {
        if (m_viewModel.IsBusy)
            return;

        m_busyActionCancellation?.Dispose();
        m_busyActionCancellation = new CancellationTokenSource();
        var cancellationToken = m_busyActionCancellation.Token;
        m_busyCancellationRequested = false;

        try
        {
            SetBusyState(true);
            SetStatus(statusText);
            await action(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("Operation canceled.");
            AppendLog("Operation canceled by user.");
        }
        catch (Exception exception)
        {
            SetStatus("Operation failed. See log for details.");
            AppendLog($"ERROR: {exception.Message}");
            DialogService.Instance.ShowMessage("Operation failed", exception.Message, null);
        }
        finally
        {
            m_busyCancellationRequested = false;
            SetBusyState(false);
            m_busyActionCancellation?.Dispose();
            m_busyActionCancellation = null;
        }
    }

    private static async Task<IStorageFolder> GetStartFolderAsync(TopLevel topLevel, string existingPath)
    {
        if (string.IsNullOrWhiteSpace(existingPath) || !existingPath.ToDir().Exists())
            return null;

        return await topLevel.StorageProvider.TryGetFolderFromPathAsync(existingPath);
    }

    private bool TryGetPullRequestInputs(out string repositoryRoot, out string prUrlText)
    {
        repositoryRoot = m_viewModel.RepositoryRootPath?.Trim();
        prUrlText = m_viewModel.PullRequestUrl?.Trim();

        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            SetStatus("Set repo root folder first.");
            DialogService.Instance.ShowMessage("Repository root required", "Set the repo root folder before preparing a review checkout.", null);
            return false;
        }

        if (!repositoryRoot.ToDir().Exists())
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
        baseBranch = GetSelectedLocalBaseBranch();
        if (!TryGetLocalRepositoryInput(out localRepositoryPath))
            return false;

        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            SetStatus("Enter a base branch (for example: main).");
            DialogService.Instance.ShowMessage("Base branch required", "Enter the branch to compare against (for example: main or develop).", null);
            return false;
        }

        return true;
    }

    private string GetSelectedLocalBaseBranch() =>
        NormalizeBranchName(m_viewModel.LocalBaseBranch);

    private void SelectLocalBaseBranch(string baseBranch)
    {
        var normalizedBaseBranch = NormalizeBranchName(baseBranch);
        if (string.IsNullOrWhiteSpace(normalizedBaseBranch))
            return;

        m_viewModel.AddLocalBaseBranchOption(normalizedBaseBranch);
        m_viewModel.LocalBaseBranch = normalizedBaseBranch;
    }

    private void SetLocalBaseBranchOptions(IReadOnlyList<string> branchOptions, string selectedBaseBranch)
    {
        var normalizedOptions = (branchOptions ?? [])
            .Select(NormalizeBranchName)
            .Where(branch => !string.IsNullOrWhiteSpace(branch))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var normalizedSelection = NormalizeBranchName(selectedBaseBranch);
        if (!string.IsNullOrWhiteSpace(normalizedSelection) &&
            !normalizedOptions.Any(branch => branch.Equals(normalizedSelection, StringComparison.OrdinalIgnoreCase)))
        {
            normalizedOptions.Add(normalizedSelection);
        }

        if (normalizedOptions.Count == 0)
            normalizedOptions.Add("main");

        m_viewModel.SetLocalBaseBranchOptions(normalizedOptions, normalizedSelection);
        EnsureLocalBaseBranchSelection();
    }

    private void EnsureLocalBaseBranchSelection()
    {
        if (!IsLocalCommittedReviewMode())
            return;

        if (LocalBaseBranchComboBox?.SelectedItem is string selectedBranch &&
            !string.IsNullOrWhiteSpace(selectedBranch))
        {
            return;
        }

        var fallbackBranch = m_viewModel.LocalBaseBranchOptions
            .FirstOrDefault(branch => !string.IsNullOrWhiteSpace(branch));
        if (string.IsNullOrWhiteSpace(fallbackBranch))
            fallbackBranch = "main";

        m_viewModel.AddLocalBaseBranchOption(fallbackBranch);
        m_viewModel.LocalBaseBranch = fallbackBranch;

        if (LocalBaseBranchComboBox != null)
            LocalBaseBranchComboBox.SelectedItem = fallbackBranch;
    }

    private bool TryGetLocalRepositoryInput(out string localRepositoryPath)
    {
        localRepositoryPath = m_viewModel.LocalRepositoryPath?.Trim();

        if (string.IsNullOrWhiteSpace(localRepositoryPath))
        {
            SetStatus("Set local repository folder first.");
            DialogService.Instance.ShowMessage("Local repository required", "Choose the local repository folder to review.", null);
            return false;
        }

        if (!localRepositoryPath.ToDir().Exists())
        {
            SetStatus($"Local repository folder does not exist: {localRepositoryPath}");
            DialogService.Instance.ShowMessage("Local repository not found", localRepositoryPath, null);
            return false;
        }

        if (!RepositoryUtilities.IsGitRepository(localRepositoryPath))
        {
            SetStatus("Selected folder is not a Git repository.");
            DialogService.Instance.ShowMessage("Invalid repository folder", "The selected folder does not appear to contain a Git repository.", null);
            return false;
        }

        return true;
    }

    private void OnInputTextChanged()
    {
        if (IsPullRequestReviewMode())
            NormalizePullRequestUrlInTextBoxIfNeeded();
        _ = UpdatePullRequestPreviewAsync();
        UpdateActionButtonStates();
    }

    private void NormalizePullRequestUrlInTextBoxIfNeeded()
    {
        if (m_normalizingPullRequestUrl)
            return;

        var urlText = m_viewModel.PullRequestUrl?.Trim();
        if (string.IsNullOrWhiteSpace(urlText))
            return;

        if (!BitbucketPrUrlParser.TryParse(urlText, out var pullRequest, out _))
            return;

        if (string.Equals(urlText, pullRequest.SourceUrl, StringComparison.Ordinal))
            return;

        m_normalizingPullRequestUrl = true;
        try
        {
            m_viewModel.PullRequestUrl = pullRequest.SourceUrl;
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

        if (IsAnyLocalReviewMode())
        {
            m_viewModel.UpdatePullRequestMetadataPreview(null, null, null);
            UpdateActionButtonStates();
            return;
        }

        var urlText = m_viewModel.PullRequestUrl?.Trim();
        if (string.IsNullOrWhiteSpace(urlText))
        {
            m_viewModel.UpdatePullRequestMetadataPreview(null, null, null);
            UpdateActionButtonStates();
            return;
        }

        if (!BitbucketPrUrlParser.TryParse(urlText, out var pullRequest, out _))
        {
            m_viewModel.UpdatePullRequestMetadataPreview(null, null, null);
            UpdateActionButtonStates();
            return;
        }

        m_viewModel.UpdatePullRequestMetadataPreview(null, null, null);

        var metadata = await m_pullRequestMetadataClient.TryGetMetadataAsync(pullRequest, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return;

        m_viewModel.UpdatePullRequestMetadataPreview(metadata?.Title, metadata?.Author, metadata?.State);
        UpdateActionButtonStates();

        if (metadata != null && m_viewModel.PreviewPullRequestIsOpen == false)
            NotifyNonOpenPullRequestIfNeeded(pullRequest);
    }

    private static string FormatMetadataText(string value) =>
        string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();

    private void SetBusyState(bool isBusy)
    {
        m_viewModel.IsBusy = isBusy;

        if (!isBusy)
            m_viewModel.ResetBusyProgress();

        UpdateActionButtonStates();
    }

    private void SetBusyProgressIndeterminate()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(SetBusyProgressIndeterminate);
            return;
        }

        if (!m_viewModel.IsBusy)
            return;

        m_viewModel.SetBusyProgressIndeterminate();
    }

    private void UpdateBusyProgress(int completed, int total, string _)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => UpdateBusyProgress(completed, total, null));
            return;
        }

        if (!m_viewModel.IsBusy || total <= 0)
        {
            m_viewModel.SetBusyProgressIndeterminate();
            return;
        }

        m_viewModel.UpdateBusyProgress(completed, total);
    }

    private ReviewMode GetSelectedReviewMode()
    {
        var selectedIndex = m_viewModel.ReviewModeIndex;
        if (selectedIndex < (int)ReviewMode.PullRequest || selectedIndex > (int)ReviewMode.LocalUncommittedChanges)
            return ReviewMode.PullRequest;

        return (ReviewMode)selectedIndex;
    }

    private bool IsPullRequestReviewMode() => GetSelectedReviewMode() == ReviewMode.PullRequest;

    private bool IsLocalCommittedReviewMode() => GetSelectedReviewMode() == ReviewMode.LocalCommittedChanges;

    private bool IsLocalUncommittedReviewMode() => GetSelectedReviewMode() == ReviewMode.LocalUncommittedChanges;

    private bool IsAnyLocalReviewMode() => GetSelectedReviewMode() != ReviewMode.PullRequest;

    private bool ShouldIncludeFullModifiedFiles() => m_viewModel.ScanScopeIndex == 1;

    private void UpdateActionButtonStates()
    {
        var canReviewCurrentPullRequest = m_viewModel.PreviewPullRequestIsOpen != false;
        var reviewMode = GetSelectedReviewMode();
        var isLocalMode = reviewMode != ReviewMode.PullRequest;
        m_latestSolutionPath = ResolveAvailableSolutionPath();
        var hasValidLocalPrepareInputs = HasValidLocalPrepareInputs(reviewMode);
        var hasValidPullRequestPrepareInputs = HasValidPullRequestPrepareInputs();
        var hasValidPullRequestInput = HasValidPullRequestInput();
        var hasAvailableSolution = !string.IsNullOrWhiteSpace(m_latestSolutionPath) &&
                                   m_latestSolutionPath.ToFile().Exists();
        var canCancelCurrentOperation = m_busyActionCancellation is { IsCancellationRequested: false };

        m_viewModel.UpdateActionStateInputs(
            isGitAvailable: m_isGitAvailable,
            canReviewCurrentPullRequest: canReviewCurrentPullRequest,
            hasValidPullRequestInput: hasValidPullRequestInput,
            hasValidPullRequestPrepareInputs: hasValidPullRequestPrepareInputs,
            hasValidLocalPrepareInputs: isLocalMode && hasValidLocalPrepareInputs,
            hasAvailableSolution: hasAvailableSolution,
            canCancelCurrentOperation: canCancelCurrentOperation,
            isCancellationRequested: m_busyCancellationRequested);
    }

    private string ResolveAvailableSolutionPath()
    {
        if (!string.IsNullOrWhiteSpace(m_latestSolutionPath) && m_latestSolutionPath.ToFile().Exists())
            return m_latestSolutionPath;

        if (!string.IsNullOrWhiteSpace(m_latestReviewWorktreePath) && m_latestReviewWorktreePath.ToDir().Exists())
        {
            var worktreeSolution = RepositoryUtilities.FindTopLevelSolutionFile(m_latestReviewWorktreePath);
            if (!string.IsNullOrWhiteSpace(worktreeSolution) && worktreeSolution.ToFile().Exists())
                return worktreeSolution;
        }

        var localRepositoryPath = m_viewModel.LocalRepositoryPath?.Trim();
        if (!string.IsNullOrWhiteSpace(localRepositoryPath) && localRepositoryPath.ToDir().Exists())
        {
            var localSolution = RepositoryUtilities.FindTopLevelSolutionFile(localRepositoryPath);
            if (!string.IsNullOrWhiteSpace(localSolution) && localSolution.ToFile().Exists())
                return localSolution;
        }

        var repositoryRootPath = m_viewModel.RepositoryRootPath?.Trim();
        if (!string.IsNullOrWhiteSpace(repositoryRootPath) && repositoryRootPath.ToDir().Exists())
        {
            var rootSolution = RepositoryUtilities.FindTopLevelSolutionFile(repositoryRootPath);
            if (!string.IsNullOrWhiteSpace(rootSolution) && rootSolution.ToFile().Exists())
                return rootSolution;
        }

        return null;
    }

    private bool HasValidPullRequestInput()
    {
        var prUrlText = m_viewModel.PullRequestUrl?.Trim();
        return !string.IsNullOrWhiteSpace(prUrlText) &&
               BitbucketPrUrlParser.TryParse(prUrlText, out _, out _);
    }

    private bool HasValidPullRequestPrepareInputs()
    {
        var repositoryRoot = m_viewModel.RepositoryRootPath?.Trim();
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !repositoryRoot.ToDir().Exists())
            return false;

        var prUrlText = m_viewModel.PullRequestUrl?.Trim();
        if (string.IsNullOrWhiteSpace(prUrlText))
            return false;

        return BitbucketPrUrlParser.TryParse(prUrlText, out _, out _);
    }

    private bool HasValidLocalPrepareInputs(ReviewMode reviewMode)
    {
        var localRepositoryPath = m_viewModel.LocalRepositoryPath?.Trim();
        if (string.IsNullOrWhiteSpace(localRepositoryPath) || !localRepositoryPath.ToDir().Exists())
            return false;

        if (!RepositoryUtilities.IsGitRepository(localRepositoryPath))
            return false;

        if (reviewMode == ReviewMode.LocalUncommittedChanges)
            return true;

        var baseBranch = GetSelectedLocalBaseBranch();
        return !string.IsNullOrWhiteSpace(baseBranch);
    }

    private void SetStatus(string status)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetStatus(status));
            return;
        }

        m_viewModel.StatusText = status;
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

        if (!TryResolveLogFile(filePath, out var resolvedFile))
        {
            SetStatus($"Could not resolve file path: {filePath}");
            return;
        }

        if (!TryLaunchVsCodeAtLine(resolvedFile.FullName, lineNumber, out var launchError))
        {
            SetStatus(launchError);
            return;
        }

        SetStatus($"Opened in VS Code: {resolvedFile.Name}:{lineNumber}");
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

    private bool TryResolveLogFile(string pathFromLog, out FileInfo resolvedFile)
    {
        resolvedFile = null;

        if (string.IsNullOrWhiteSpace(pathFromLog))
            return false;

        var normalizedRelativePath = pathFromLog
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalizedRelativePath))
        {
            var absoluteFile = normalizedRelativePath.ToFile();
            if (!absoluteFile.Exists())
                return false;

            resolvedFile = absoluteFile;
            return true;
        }

        if (string.IsNullOrWhiteSpace(m_latestReviewWorktreePath))
            return false;

        var candidatePath = m_latestReviewWorktreePath.ToDir().GetFile(normalizedRelativePath);
        if (!candidatePath.Exists())
            return false;

        resolvedFile = candidatePath;
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
            if (!candidatePath.ToFile().Exists())
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
                    var folder = trimmedDirectory.ToDir();
                    yield return folder.GetFile("code.cmd").FullName;
                    yield return folder.GetFile("code.exe").FullName;
                    yield return folder.GetFile("code.bat").FullName;
                    continue;
                }

                yield return trimmedDirectory.ToDir().GetFile("code").FullName;
            }
        }

        yield return "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code";
        yield return "/Applications/Visual Studio Code - Insiders.app/Contents/Resources/app/bin/code";
        yield return "/usr/local/bin/code";
        yield return "/opt/homebrew/bin/code";
        yield return "/snap/bin/code";

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return localAppData.ToDir().GetDir("Programs").GetDir("Microsoft VS Code").GetFile("Code.exe").FullName;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return programFiles.ToDir().GetDir("Microsoft VS Code").GetFile("Code.exe").FullName;

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            yield return programFilesX86.ToDir().GetDir("Microsoft VS Code").GetFile("Code.exe").FullName;
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

    private async void LocalRepositoryFolderTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        await TryAutoDetectLocalBaseBranchAsync(logWhenUpdated: true);
    }

    private async void MainWindow_OnOpened(object sender, EventArgs e)
    {
        await EnsureGitIsAvailableOnStartupAsync();
        if (!m_isGitAvailable)
            return;

        await TryAutoDetectLocalBaseBranchAsync(logWhenUpdated: false);
        await RunStartupCodeReviewCleanupAsync();
        await TryPrefillPullRequestUrlFromClipboardAsync();
    }

    private async void MainWindow_OnActivated(object sender, EventArgs e) =>
        await TryPrefillPullRequestUrlFromClipboardAsync();

    private void MainWindow_OnClosing(object sender, WindowClosingEventArgs e)
    {
        m_busyCancellationRequested = true;
        m_busyActionCancellation?.Cancel();
        m_busyActionCancellation?.Dispose();
        m_busyActionCancellation = null;
        m_previewUpdateCancellation?.Cancel();
        m_previewUpdateCancellation?.Dispose();
        m_previewUpdateCancellation = null;
        m_pullRequestMetadataClient.Dispose();

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

    private async Task TryAutoDetectLocalBaseBranchAsync(bool logWhenUpdated)
    {
        if (!m_isGitAvailable)
            return;

        var localRepositoryPath = m_viewModel.LocalRepositoryPath?.Trim();
        if (!RepositoryUtilities.IsGitRepository(localRepositoryPath))
        {
            var fallbackBaseBranch = NormalizeBranchName(m_viewModel.LocalBaseBranch) ?? "main";
            SetLocalBaseBranchOptions([fallbackBaseBranch], fallbackBaseBranch);
            return;
        }

        var currentBaseBranch = GetSelectedLocalBaseBranch();
        var resolvedBaseBranch = await ResolveLocalBaseBranchAsync(localRepositoryPath, currentBaseBranch, logWhenChanged: logWhenUpdated);
        if (string.IsNullOrWhiteSpace(resolvedBaseBranch))
            return;

        var branchOptions = await TryGetLocalBaseBranchOptionsAsync(localRepositoryPath);
        if (!branchOptions.Any())
            branchOptions = [resolvedBaseBranch];

        if (!branchOptions.Any(branch => branch.Equals(resolvedBaseBranch, StringComparison.OrdinalIgnoreCase)))
            branchOptions.Add(resolvedBaseBranch);

        SetLocalBaseBranchOptions(branchOptions, resolvedBaseBranch);
        m_viewModel.LocalBaseBranch = resolvedBaseBranch;
        InvalidateLocalReviewChangedFilesCache();
    }

    private async Task<List<string>> TryGetLocalBaseBranchOptionsAsync(string localRepositoryPath)
    {
        var branchOptions = new List<string>();
        var seenBranches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddBranchOption(string branchName)
        {
            var normalizedBranchName = NormalizeBranchName(branchName);
            if (string.IsNullOrWhiteSpace(normalizedBranchName) || !seenBranches.Add(normalizedBranchName))
                return;

            branchOptions.Add(normalizedBranchName);
        }

        var defaultBranch = await TryGetDefaultRemoteBranchAsync(localRepositoryPath);
        var stopBranchCandidates = new List<string>();
        AddStopBranchCandidate(defaultBranch);
        AddStopBranchCandidate("main");
        AddStopBranchCandidate("master");

        var currentBranchResult = await m_gitCommandRunner.RunAsync(
            localRepositoryPath,
            "symbolic-ref",
            "--quiet",
            "--short",
            "HEAD");
        if (currentBranchResult.IsSuccess)
            AddBranchOption(currentBranchResult.StandardOutput);

        var stopBranchRef = await ResolveFirstExistingBranchReferenceAsync(localRepositoryPath, stopBranchCandidates);
        var stopCommitHash = await TryGetMergeBaseCommitAsync(localRepositoryPath, stopBranchRef);
        var branchTipsByCommit = await LoadBranchTipsByCommitAsync(localRepositoryPath);

        var historyArguments = string.IsNullOrWhiteSpace(stopCommitHash)
            ? new[] { "rev-list", "--first-parent", "HEAD" }
            : new[] { "rev-list", "--first-parent", $"{stopCommitHash}..HEAD" };
        var historyResult = await m_gitCommandRunner.RunAsync(localRepositoryPath, historyArguments);
        if (historyResult.IsSuccess)
        {
            foreach (var commitHash in ParseGitOutputLines(historyResult.StandardOutput))
            {
                if (branchTipsByCommit.TryGetValue(commitHash, out var branchesAtCommit))
                {
                    foreach (var branchAtCommit in branchesAtCommit)
                        AddBranchOption(branchAtCommit);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(stopCommitHash) &&
            branchTipsByCommit.TryGetValue(stopCommitHash, out var stopCommitBranches))
        {
            foreach (var stopCommitBranch in stopCommitBranches)
                AddBranchOption(stopCommitBranch);
        }

        var normalizedStopBranch = NormalizeBranchName(stopBranchRef);
        if (!string.IsNullOrWhiteSpace(normalizedStopBranch))
            AddBranchOption(normalizedStopBranch);
        else if (!string.IsNullOrWhiteSpace(defaultBranch))
            AddBranchOption(defaultBranch);

        return branchOptions;

        void AddStopBranchCandidate(string branchName)
        {
            var normalizedBranchName = NormalizeBranchName(branchName);
            if (string.IsNullOrWhiteSpace(normalizedBranchName))
                return;

            if (!stopBranchCandidates.Any(candidate => candidate.Equals(normalizedBranchName, StringComparison.OrdinalIgnoreCase)))
                stopBranchCandidates.Add(normalizedBranchName);
        }
    }

    private async Task<Dictionary<string, List<string>>> LoadBranchTipsByCommitAsync(string localRepositoryPath)
    {
        var branchTipsByCommit = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var refsResult = await m_gitCommandRunner.RunAsync(
            localRepositoryPath,
            "for-each-ref",
            "--format=%(objectname)%09%(refname:short)",
            "refs/heads",
            "refs/remotes/origin");
        if (!refsResult.IsSuccess)
            return branchTipsByCommit;

        foreach (var line in ParseGitOutputLines(refsResult.StandardOutput))
        {
            var separatorIndex = line.IndexOf('\t');
            if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
                continue;

            var commitHash = line[..separatorIndex].Trim();
            var branchName = NormalizeBranchName(line[(separatorIndex + 1)..]);
            if (string.IsNullOrWhiteSpace(commitHash) || string.IsNullOrWhiteSpace(branchName))
                continue;

            if (!branchTipsByCommit.TryGetValue(commitHash, out var branchesAtCommit))
            {
                branchesAtCommit = [];
                branchTipsByCommit[commitHash] = branchesAtCommit;
            }

            if (!branchesAtCommit.Any(candidate => candidate.Equals(branchName, StringComparison.OrdinalIgnoreCase)))
                branchesAtCommit.Add(branchName);
        }

        return branchTipsByCommit;
    }

    private async Task<string> ResolveFirstExistingBranchReferenceAsync(string localRepositoryPath, IReadOnlyList<string> branchNames)
    {
        foreach (var branchName in branchNames ?? [])
        {
            var branchReference = await ResolveBranchReferenceAsync(localRepositoryPath, branchName);
            if (!string.IsNullOrWhiteSpace(branchReference))
                return branchReference;
        }

        return null;
    }

    private async Task<string> ResolveBranchReferenceAsync(string localRepositoryPath, string branchName)
    {
        var normalizedBranchName = NormalizeBranchName(branchName);
        if (string.IsNullOrWhiteSpace(normalizedBranchName))
            return null;

        if (await HasRemoteTrackingBranchAsync(localRepositoryPath, normalizedBranchName))
            return $"origin/{normalizedBranchName}";
        if (await HasLocalBranchAsync(localRepositoryPath, normalizedBranchName))
            return normalizedBranchName;

        return null;
    }

    private async Task<string> TryGetMergeBaseCommitAsync(string localRepositoryPath, string branchReference)
    {
        if (string.IsNullOrWhiteSpace(branchReference))
            return null;

        var result = await m_gitCommandRunner.RunAsync(
            localRepositoryPath,
            "merge-base",
            "HEAD",
            branchReference);
        if (!result.IsSuccess)
            return null;

        return ParseGitOutputLines(result.StandardOutput).FirstOrDefault();
    }

    private async Task<string> ResolveLocalBaseBranchAsync(string localRepositoryPath, string requestedBaseBranch, bool logWhenChanged)
    {
        if (!RepositoryUtilities.IsGitRepository(localRepositoryPath))
            return requestedBaseBranch?.Trim();

        var normalizedRequestedBranch = requestedBaseBranch?.Trim();
        var detectedDefaultBranch = await TryGetDefaultRemoteBranchAsync(localRepositoryPath);
        if (string.IsNullOrWhiteSpace(detectedDefaultBranch))
            return normalizedRequestedBranch;

        if (string.IsNullOrWhiteSpace(normalizedRequestedBranch))
        {
            if (logWhenChanged)
                AppendLog($"Detected default base branch: {detectedDefaultBranch}");
            return detectedDefaultBranch;
        }

        var hasRequestedBranch = await HasRemoteTrackingBranchAsync(localRepositoryPath, normalizedRequestedBranch) ||
                                 await HasLocalBranchAsync(localRepositoryPath, normalizedRequestedBranch);
        if (!hasRequestedBranch)
        {
            if (logWhenChanged)
                AppendLog($"Detected default base branch: {detectedDefaultBranch} (replacing '{normalizedRequestedBranch}')");
            return detectedDefaultBranch;
        }

        if (string.Equals(normalizedRequestedBranch, "main", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedRequestedBranch, detectedDefaultBranch, StringComparison.OrdinalIgnoreCase))
        {
            if (logWhenChanged)
                AppendLog($"Detected default base branch: {detectedDefaultBranch} (replacing '{normalizedRequestedBranch}')");
            return detectedDefaultBranch;
        }

        return normalizedRequestedBranch;
    }

    private async Task<string> TryGetDefaultRemoteBranchAsync(string localRepositoryPath)
    {
        if (!RepositoryUtilities.IsGitRepository(localRepositoryPath))
            return null;

        var symbolicRefResult = await m_gitCommandRunner.RunAsync(
            localRepositoryPath,
            "symbolic-ref",
            "--quiet",
            "--short",
            "refs/remotes/origin/HEAD");
        if (symbolicRefResult.IsSuccess)
        {
            var branch = ParseRemoteHeadBranch(symbolicRefResult.StandardOutput);
            if (!string.IsNullOrWhiteSpace(branch))
                return branch;
        }

        if (await HasRemoteTrackingBranchAsync(localRepositoryPath, "main"))
            return "main";
        if (await HasRemoteTrackingBranchAsync(localRepositoryPath, "master"))
            return "master";

        return null;
    }

    private async Task<bool> HasRemoteTrackingBranchAsync(string localRepositoryPath, string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return false;

        var result = await m_gitCommandRunner.RunAsync(
            localRepositoryPath,
            "show-ref",
            "--verify",
            "--quiet",
            $"refs/remotes/origin/{branchName}");
        return result.IsSuccess;
    }

    private async Task<bool> HasLocalBranchAsync(string localRepositoryPath, string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return false;

        var result = await m_gitCommandRunner.RunAsync(
            localRepositoryPath,
            "show-ref",
            "--verify",
            "--quiet",
            $"refs/heads/{branchName}");
        return result.IsSuccess;
    }

    private static List<string> ParseGitOutputLines(string output) =>
        (output ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    private static string NormalizeBranchName(string branchName)
    {
        var normalized = branchName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        while (true)
        {
            var updated = normalized;
            updated = StripPrefix(updated, "refs/heads/");
            updated = StripPrefix(updated, "refs/remotes/");
            updated = StripPrefix(updated, "heads/");
            updated = StripPrefix(updated, "remotes/");
            updated = StripPrefix(updated, "origin/");

            if (updated.Equals(normalized, StringComparison.Ordinal))
                break;

            normalized = updated;
        }

        return normalized.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("origin", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;

        static string StripPrefix(string text, string prefix) =>
            text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? text[prefix.Length..]
                : text;
    }

    private static string ParseRemoteHeadBranch(string rawOutput)
    {
        var normalized = rawOutput?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        const string longPrefix = "remotes/origin/";
        if (normalized.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
            return normalized[longPrefix.Length..];

        const string shortPrefix = "origin/";
        return normalized.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[shortPrefix.Length..]
            : normalized;
    }

    private async Task RunStartupCodeReviewCleanupAsync()
    {
        var repositoryRoot = m_viewModel.RepositoryRootPath?.Trim();
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !repositoryRoot.ToDir().Exists())
            return;

        await ExecuteBusyActionAsync(
            "Clearing previous CodeReview folders...",
            async cancellationToken =>
            {
                await m_orchestrator.ClearCodeReviewFolderAsync(repositoryRoot, AppendLog, logWhenMissing: false, cancellationToken);
                SetStatus("Ready.");
            });
    }

    private void CancelProcessingButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!m_viewModel.IsBusy || m_busyActionCancellation == null || m_busyActionCancellation.IsCancellationRequested)
            return;

        m_busyCancellationRequested = true;
        AppendLog("Cancel requested by user.");
        SetStatus("Cancel requested...");
        m_busyActionCancellation.Cancel();
        UpdateActionButtonStates();
    }

    private async Task TryPrefillPullRequestUrlFromClipboardAsync()
    {
        if (!IsPullRequestReviewMode())
            return;

        if (!string.IsNullOrWhiteSpace(m_viewModel.PullRequestUrl))
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

        m_viewModel.PullRequestUrl = pullRequest.SourceUrl;
        SetStatus("Pull request URL loaded from clipboard.");
    }

    private void OpenPullRequestButton_OnClick(object sender, RoutedEventArgs e)
    {
        var prUrlText = m_viewModel.PullRequestUrl?.Trim();
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

    private void NotifyNonOpenPullRequestIfNeeded(BitbucketPullRequestReference pullRequest)
    {
        if (pullRequest == null || m_viewModel.PreviewPullRequestIsOpen != false)
            return;

        var state = m_viewModel.PreviewPullRequestStateDisplay;
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

    private void OpenSolutionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(m_latestSolutionPath) || !m_latestSolutionPath.ToFile().Exists())
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
            case byte[] { Length: > 0 } bytes:
                yield return DecodeBytes(bytes);
                yield break;
            case MemoryStream { Length: > 0 } stream:
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


