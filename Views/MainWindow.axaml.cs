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
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private readonly CodeLocationOpener m_codeLocationOpener;
    private readonly LogNavigationService m_logNavigationService;
    private readonly ReviewFindingInteractionService m_reviewFindingInteractionService;
    private readonly PullRequestUrlExtractionService m_pullRequestUrlExtractionService;
    private readonly MainWindowActionStateService m_actionStateService;
    private readonly LocalBaseBranchService m_localBaseBranchService;
    private readonly LocalFindingResampleService m_localFindingResampleService;
    private readonly PullRequestPreviewService m_pullRequestPreviewService;
    private readonly CodeSmellReportAnalyzer m_codeSmellReportAnalyzer;
    private readonly BitbucketPullRequestMetadataClient m_pullRequestMetadataClient;
    private readonly MainWindowStartupService m_startupService;
    private readonly MainWindowUiService m_uiService;
    private readonly MainWindowReviewWorkflowService m_reviewWorkflowService;
    private readonly Settings m_settings;
    private readonly MainWindowViewModel m_viewModel;
    private CancellationTokenSource m_previewUpdateCancellation;
    private CancellationTokenSource m_busyActionCancellation;
    private string m_lastNonOpenPullRequestNoticeKey;
    private bool m_isInitializing;

    public MainWindow()
        : this(MainWindowCompositionRoot.CreateDependencies())
    {
    }

    internal MainWindow(MainWindowDependencies dependencies)
    {
        if (dependencies == null)
            throw new ArgumentNullException(nameof(dependencies));

        m_settings = dependencies.Settings;
        m_codeLocationOpener = dependencies.CodeLocationOpener;
        m_logNavigationService = dependencies.LogNavigationService;
        var logFeedService = dependencies.LogFeedService;
        m_reviewFindingInteractionService = dependencies.ReviewFindingInteractionService;
        m_pullRequestUrlExtractionService = dependencies.PullRequestUrlExtractionService;
        m_actionStateService = dependencies.ActionStateService;
        m_localBaseBranchService = dependencies.LocalBaseBranchService;
        m_localFindingResampleService = dependencies.LocalFindingResampleService;
        m_pullRequestPreviewService = dependencies.PullRequestPreviewService;
        m_codeSmellReportAnalyzer = dependencies.CodeSmellReportAnalyzer;
        m_pullRequestMetadataClient = dependencies.PullRequestMetadataClient;
        m_startupService = dependencies.StartupService;
        m_reviewWorkflowService = dependencies.ReviewWorkflowService;
        m_viewModel = new MainWindowViewModel(m_settings);
        m_viewModel.ConfigureCommands(
            BrowseRepositoryRootAsync,
            BrowseLocalRepositoryAsync,
            ExecutePrepareReviewAsync,
            CancelProcessing,
            OpenPullRequest,
            OpenSolution);
        m_isInitializing = true;
        InitializeComponent();
        DataContext = m_viewModel;
        Title = BuildWindowTitle();
        m_uiService = new MainWindowUiService(
            m_viewModel,
            logFeedService,
            () => Dispatcher.UIThread.CheckAccess(),
            action => Dispatcher.UIThread.Post(action),
            entry => LogListBox.ScrollIntoView(entry),
            (title, message) => DialogService.Instance.ShowMessage(title, message, null));
        PullRequestUrlTextBox.AddHandler(DragDrop.DragOverEvent, PullRequestUrlTextBox_OnDragOver);
        PullRequestUrlTextBox.AddHandler(DragDrop.DropEvent, PullRequestUrlTextBox_OnDrop);
        LocalRepositoryFolderTextBox.LostFocus += LocalRepositoryFolderTextBox_OnLostFocus;
        LogListBox.AddHandler(PointerPressedEvent, LogListBox_OnPointerPressed, RoutingStrategies.Bubble);
        Opened += MainWindow_OnOpened;
        Activated += MainWindow_OnActivated;
        Closing += MainWindow_OnClosing;

        var persistedBaseBranch = LocalBaseBranchService.NormalizeBranchName(m_viewModel.LocalBaseBranch) ?? "main";
        m_viewModel.SetLocalBaseBranchOptions([persistedBaseBranch], persistedBaseBranch);
        m_viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        LogListBox.ItemsSource = logFeedService.Entries;
        m_isInitializing = false;

        _ = UpdatePullRequestPreviewAsync();
        UpdateActionButtonStates();
    }

    private async Task BrowseRepositoryRootAsync()
    {
        var path = await BrowseFolderAsync("Choose repository root folder", m_viewModel.RepositoryRootPath);
        if (!string.IsNullOrWhiteSpace(path))
            m_viewModel.RepositoryRootPath = path;
    }

    private async Task BrowseLocalRepositoryAsync()
    {
        var path = await BrowseFolderAsync("Choose local repository folder", m_viewModel.LocalRepositoryPath);
        if (string.IsNullOrWhiteSpace(path))
            return;

        m_viewModel.LocalRepositoryPath = path;
        await TryAutoDetectLocalBaseBranchAsync(logWhenUpdated: true);
    }

    private static string BuildWindowTitle()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(MainWindow).Assembly;
        var displayVersion = assembly.GetDisplayVersion();
        return string.IsNullOrWhiteSpace(displayVersion)
            ? ApplicationTitle
            : $"{ApplicationTitle} v{displayVersion}";
    }

    private void PullRequestUrlTextBox_OnDragOver(object sender, DragEventArgs e)
    {
        e.DragEffects = m_pullRequestUrlExtractionService.TryExtractPullRequestUrl(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void PullRequestUrlTextBox_OnDrop(object sender, DragEventArgs e)
    {
        if (m_pullRequestUrlExtractionService.TryExtractPullRequestUrl(e.Data, out var url))
        {
            m_viewModel.PullRequestUrl = url;
            SetStatus("Pull request URL captured from drop.");
        }

        e.Handled = true;
    }

    private async Task ExecutePrepareReviewAsync()
    {
        if (m_viewModel.IsLocalCommittedReviewMode)
            await TryAutoDetectLocalBaseBranchAsync(logWhenUpdated: true);

        if (m_viewModel.IsAnyLocalReviewMode)
        {
            InvalidateLocalReviewChangedFilesCache();
            m_viewModel.ClearPullRequestReviewContext();
        }

        MainWindowReviewPreparationResult preparationResult = null;
        await ExecuteBusyActionAsync(
            m_reviewWorkflowService.GetPrepareReviewStatusText(
                m_viewModel.IsPullRequestReviewMode,
                m_viewModel.IsLocalCommittedReviewMode,
                m_viewModel.IsLocalRepositoryReviewMode),
            async cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetBusyProgressIndeterminate();
                preparationResult = await m_reviewWorkflowService.PrepareReviewByModeAsync(
                    m_viewModel.IsPullRequestReviewMode,
                    m_viewModel.IsLocalCommittedReviewMode,
                    m_viewModel.IsLocalRepositoryReviewMode,
                    m_viewModel.RepositoryRootPath?.Trim(),
                    m_viewModel.PullRequestUrl?.Trim(),
                    m_viewModel.LocalRepositoryPath?.Trim(),
                    LocalBaseBranchService.NormalizeBranchName(m_viewModel.LocalBaseBranch),
                    m_viewModel.IncludeFullModifiedFiles,
                    AppendLog,
                    UpdateBusyProgress,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            });

        if (preparationResult == null)
            return;

        if (!preparationResult.IsSuccess)
        {
            HandlePreparationFailure(preparationResult.Error);
            return;
        }

        var applyResult = m_reviewWorkflowService.BuildApplyResult(
            preparationResult,
            m_viewModel.LocalRepositoryPath?.Trim());
        await ApplyReviewWorkflowResultAsync(applyResult);
    }

    private async Task ApplyReviewWorkflowResultAsync(MainWindowReviewWorkflowApplyResult applyResult)
    {
        m_viewModel.ApplyReviewWorkflowResult(applyResult);
        UpdateActionButtonStates();

        if (applyResult.LocalFindingCacheUpdate is { } cacheUpdate)
        {
            m_localFindingResampleService.SetCachedFiles(
                cacheUpdate.RepositoryPath,
                cacheUpdate.BaseBranch,
                cacheUpdate.Mode,
                cacheUpdate.Files);
        }

        if (!string.IsNullOrWhiteSpace(applyResult.LogMessage))
            AppendLog(applyResult.LogMessage);

        if (!string.IsNullOrWhiteSpace(applyResult.StatusMessage))
            SetStatus(applyResult.StatusMessage);

        if (applyResult.NotifyNonOpenPullRequest && applyResult.PullRequest != null)
        {
            NotifyNonOpenPullRequestIfNeeded(applyResult.PullRequest);
            return;
        }

        if (applyResult.HasReportFindings)
            await ShowReviewResultsWindowAsync(applyResult.Report);
    }

    private void HandlePreparationFailure(MainWindowReviewPreparationError? error)
    {
        if (error is not { } failure)
            return;

        SetStatus(failure.StatusMessage);
        if (!string.IsNullOrWhiteSpace(failure.LogMessage))
            AppendLog(failure.LogMessage);
        ShowMessage(failure.DialogTitle, failure.DialogMessage);
    }

    private async Task ShowReviewResultsWindowAsync(CodeSmellReport report)
    {
        var canOpenCodeLocation = m_codeLocationOpener.TargetDefinitions.Any(definition => IsOpenTargetAvailable(definition.Target));
        var canCommentInBitbucket = m_viewModel.IsPullRequestReviewMode && m_viewModel.LatestPullRequest != null;
        var canFixLocally = m_viewModel.IsAnyLocalReviewMode;
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
        => m_reviewFindingInteractionService.IsTargetAvailable(target, Clipboard != null);

    private async Task<(bool Success, string Message)> OpenReviewFindingAsync(
        CodeSmellFinding finding,
        CodeLocationOpenTarget target)
    {
        var result = await m_reviewFindingInteractionService.OpenFindingAsync(
            finding,
            target,
            m_viewModel.LatestReviewWorktreePath,
            Clipboard != null,
            async text =>
            {
                if (Clipboard != null)
                    await Clipboard.SetTextAsync(text);
            });
        ApplyInteractionResult(result.StatusMessage, result.LogMessage);
        return (result.Success, result.Success ? null : result.StatusMessage);
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
        var result = m_reviewFindingInteractionService.OpenFindingInVsCode(finding, m_viewModel.LatestReviewWorktreePath);
        ApplyInteractionResult(result.StatusMessage, result.LogMessage);
    }

    private string ResolveReviewFindingPath(CodeSmellFinding finding)
        => m_reviewFindingInteractionService.ResolveFindingPath(finding, m_viewModel.LatestReviewWorktreePath);

    private async Task<IReadOnlyList<CodeSmellFinding>> ResampleLocalFindingsForFileAsync(string filePath)
    {
        if (!m_viewModel.IsAnyLocalReviewMode || string.IsNullOrWhiteSpace(filePath))
            return [];

        var reviewMode = m_viewModel.IsLocalCommittedReviewMode
            ? LocalReviewResampleMode.Committed
            : m_viewModel.IsLocalUncommittedReviewMode
                ? LocalReviewResampleMode.Uncommitted
                : (LocalReviewResampleMode?)null;
        if (reviewMode == null)
            return [];

        var localRepositoryPath = m_viewModel.LocalRepositoryPath?.Trim();
        var baseBranch = reviewMode == LocalReviewResampleMode.Committed
            ? LocalBaseBranchService.NormalizeBranchName(m_viewModel.LocalBaseBranch)
            : null;
        return await m_localFindingResampleService.ResampleFindingsForFileAsync(
            filePath,
            localRepositoryPath,
            baseBranch,
            reviewMode.Value,
            m_viewModel.IncludeFullModifiedFiles,
            AppendLog);
    }

    private void InvalidateLocalReviewChangedFilesCache()
    {
        m_localFindingResampleService.InvalidateCache();
        UpdateActionButtonStates();
    }

    private async Task<bool> CommentOnReviewFindingAsync(CodeSmellFinding finding)
    {
        var result = await m_reviewFindingInteractionService.CommentOnFindingAsync(
            finding,
            m_viewModel.LatestPullRequest);
        ApplyInteractionResult(result.StatusMessage, result.LogMessage);
        return result.Success;
    }

    private async Task ExecuteBusyActionAsync(string statusText, Func<CancellationToken, Task> action)
    {
        if (m_viewModel.IsBusy)
            return;

        m_busyActionCancellation?.Dispose();
        m_busyActionCancellation = new CancellationTokenSource();
        var cancellationToken = m_busyActionCancellation.Token;

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
            ShowMessage("Operation failed", exception.Message);
        }
        finally
        {
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

    private async Task<string> BrowseFolderAsync(string title, string existingPath)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null)
            return null;

        var startLocation = await GetStartFolderAsync(topLevel, existingPath);
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = startLocation
            });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnPullRequestInputChanged()
    {
        _ = UpdatePullRequestPreviewAsync();
        UpdateActionButtonStates();
    }

    private void OnReviewModeChanged()
    {
        if (m_viewModel.IsAnyLocalReviewMode)
        {
            m_viewModel.ClearPullRequestReviewContext();
            InvalidateLocalReviewChangedFilesCache();
        }

        _ = UpdatePullRequestPreviewAsync();
        UpdateActionButtonStates();
    }

    private async Task UpdatePullRequestPreviewAsync()
    {
        m_previewUpdateCancellation?.Cancel();
        m_previewUpdateCancellation?.Dispose();
        m_previewUpdateCancellation = new CancellationTokenSource();
        var cancellationToken = m_previewUpdateCancellation.Token;

        var previewResult = await m_viewModel.RefreshPullRequestPreviewAsync(
            m_pullRequestPreviewService,
            cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return;

        UpdateActionButtonStates();

        if (previewResult.PullRequest != null && previewResult.Metadata != null)
            NotifyNonOpenPullRequestIfNeeded(previewResult.PullRequest);
    }

    private void SetBusyState(bool isBusy)
    {
        m_viewModel.IsBusy = isBusy;

        if (!isBusy)
            m_viewModel.ResetBusyProgress();

        UpdateActionButtonStates();
    }

    private void SetBusyProgressIndeterminate()
        => m_uiService.SetBusyProgressIndeterminate();

    private void UpdateBusyProgress(int completed, int total, string _)
        => m_uiService.UpdateBusyProgress(completed, total);

    private void UpdateActionButtonStates()
    {
        m_viewModel.RefreshActionState(
            m_actionStateService,
            canCancelCurrentOperation: m_busyActionCancellation is { IsCancellationRequested: false },
            isCancellationRequested: m_busyActionCancellation is { IsCancellationRequested: true });
    }

    private void SetStatus(string status)
        => m_uiService.SetStatus(status);

    private void AppendLog(string message)
        => m_uiService.AppendLog(message);

    private void ApplyInteractionResult(string statusMessage, string logMessage)
    {
        if (!string.IsNullOrWhiteSpace(logMessage))
            AppendLog(logMessage);
        if (!string.IsNullOrWhiteSpace(statusMessage))
            SetStatus(statusMessage);
    }

    private void ShowMessage(string title, string message) =>
        m_uiService.ShowMessage(title, message);

    private void LogListBox_OnPointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount < 2 || !TryGetLogEntryFromSource(e.Source, out var entry))
            return;

        if (!m_logNavigationService.TryParseLogLocation(entry.Text, out var filePath, out var lineNumber))
            return;

        if (!m_logNavigationService.TryResolveLogFile(filePath, m_viewModel.LatestReviewWorktreePath, out var resolvedFile))
        {
            SetStatus($"Could not resolve file path: {filePath}");
            return;
        }

        if (!m_codeLocationOpener.TryOpenAtLocation(CodeLocationOpenTarget.VsCode, resolvedFile.FullName, lineNumber, out var launchError))
        {
            SetStatus(launchError);
            return;
        }

        SetStatus($"Opened in VS Code: {resolvedFile.Name}:{lineNumber}");
        e.Handled = true;
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

    private async void LocalRepositoryFolderTextBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        await TryAutoDetectLocalBaseBranchAsync(logWhenUpdated: true);
    }

    private async void MainWindow_OnOpened(object sender, EventArgs e)
    {
        await EnsureGitIsAvailableOnStartupAsync();
        if (!m_viewModel.IsGitAvailable)
            return;

        await TryAutoDetectLocalBaseBranchAsync(logWhenUpdated: false);
        await RunStartupCodeReviewCleanupAsync();
        await TryPrefillPullRequestUrlFromClipboardAsync();
    }

    private async void MainWindow_OnActivated(object sender, EventArgs e) =>
        await TryPrefillPullRequestUrlFromClipboardAsync();

    private void MainWindow_OnClosing(object sender, WindowClosingEventArgs e)
    {
        m_viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        m_busyActionCancellation?.Cancel();
        m_busyActionCancellation?.Dispose();
        m_busyActionCancellation = null;
        m_previewUpdateCancellation?.Cancel();
        m_previewUpdateCancellation?.Dispose();
        m_previewUpdateCancellation = null;
        m_pullRequestMetadataClient.Dispose();

        m_settings.Dispose();
    }

    private void ViewModel_OnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (m_isInitializing)
            return;

        if (e.PropertyName == nameof(MainWindowViewModel.PullRequestUrl))
        {
            OnPullRequestInputChanged();
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.RepositoryRootPath))
        {
            UpdateActionButtonStates();
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.LocalRepositoryPath) ||
            e.PropertyName == nameof(MainWindowViewModel.LocalBaseBranch) ||
            e.PropertyName == nameof(MainWindowViewModel.ScanScopeIndex))
        {
            InvalidateLocalReviewChangedFilesCache();
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.ReviewModeIndex))
            OnReviewModeChanged();
    }

    private async Task EnsureGitIsAvailableOnStartupAsync()
    {
        if (!m_viewModel.MarkGitAvailabilityChecked())
            return;

        var startupResult = await m_startupService.CheckGitAvailabilityAsync(AppContext.BaseDirectory);
        m_viewModel.IsGitAvailable = startupResult.IsGitAvailable;
        if (startupResult.IsGitAvailable)
        {
            return;
        }

        UpdateActionButtonStates();
        SetStatus(startupResult.StatusMessage);
        foreach (var logMessage in startupResult.LogMessages ?? [])
            AppendLog(logMessage);

        if (!string.IsNullOrWhiteSpace(startupResult.DialogTitle) &&
            !string.IsNullOrWhiteSpace(startupResult.DialogMessage))
        {
            ShowMessage(startupResult.DialogTitle, startupResult.DialogMessage);
        }
    }

    private async Task TryAutoDetectLocalBaseBranchAsync(bool logWhenUpdated)
    {
        var didUpdateSelection = await m_viewModel.TryAutoDetectLocalBaseBranchAsync(
            m_localBaseBranchService,
            logWhenUpdated,
            AppendLog);
        if (didUpdateSelection)
            InvalidateLocalReviewChangedFilesCache();
    }

    private async Task RunStartupCodeReviewCleanupAsync()
    {
        var repositoryRoot = m_viewModel.RepositoryRootPath?.Trim();
        if (!m_startupService.CanRunStartupCleanup(repositoryRoot))
            return;

        await ExecuteBusyActionAsync(
            "Clearing previous CodeReview folders...",
            async cancellationToken =>
            {
                await m_startupService.RunStartupCleanupAsync(repositoryRoot, AppendLog, cancellationToken);
                SetStatus("Ready.");
            });
    }

    private void CancelProcessing()
    {
        if (!m_viewModel.IsBusy || m_busyActionCancellation == null || m_busyActionCancellation.IsCancellationRequested)
            return;

        AppendLog("Cancel requested by user.");
        SetStatus("Cancel requested...");
        m_busyActionCancellation.Cancel();
        UpdateActionButtonStates();
    }

    private async Task TryPrefillPullRequestUrlFromClipboardAsync()
    {
        if (Clipboard == null)
            return;

        var didApply = await m_startupService.TryPrefillPullRequestUrlFromClipboardAsync(
            () => Clipboard.GetTextAsync(),
            m_viewModel.TryApplyPullRequestUrlFromClipboard);
        if (didApply)
            SetStatus("Pull request URL loaded from clipboard.");
    }

    private void OpenPullRequest()
    {
        if (!m_viewModel.TryParsePullRequestUrl(out var pullRequest, out var parseError))
        {
            SetStatus(parseError);
            AppendLog($"Input error: {parseError}");
            ShowMessage("Invalid pull request URL", parseError);
            return;
        }

        new Uri(pullRequest.SourceUrl).Open();
        SetStatus("Pull request opened in browser.");
    }

    private void NotifyNonOpenPullRequestIfNeeded(BitbucketPullRequestReference pullRequest)
    {
        if (!PullRequestStateNotice.TryCreateNonOpenPullRequestNotice(
                pullRequest,
                m_viewModel.PreviewPullRequestIsOpen,
                m_viewModel.PreviewPullRequestStateDisplay,
                m_lastNonOpenPullRequestNoticeKey,
                out var noticeKey,
                out var message))
        {
            return;
        }

        m_lastNonOpenPullRequestNoticeKey = noticeKey;
        AppendLog($"WARNING: {message}");
        SetStatus(message);
        ShowMessage(
            "Pull request is not open",
            $"{message}{Environment.NewLine}{Environment.NewLine}You can still click 'Open PR' to inspect it in Bitbucket.");
    }

    private void OpenSolution()
    {
        if (string.IsNullOrWhiteSpace(m_viewModel.LatestSolutionPath) || !m_viewModel.LatestSolutionPath.ToFile().Exists())
        {
            UpdateActionButtonStates();
            SetStatus("No solution is available from the latest review.");
            return;
        }

        m_viewModel.LatestSolutionPath.ToFile().OpenWithDefaultViewer();
        SetStatus("Opened solution in default application.");
        AppendLog($"Opened solution: {m_viewModel.LatestSolutionPath}");
    }

}
