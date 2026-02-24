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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DTC.Core.UI;
using ReviewG33k.Models;
using ReviewG33k.Services;
using ReviewG33k.ViewModels;

namespace ReviewG33k.Views;

public partial class MainWindow : Window
{
    private static readonly IBrush TimestampedLogBrush = Brushes.Gainsboro;
    private static readonly IBrush DetailLogBrush = Brushes.Gray;
    private static readonly IBrush ErrorLogBrush = Brushes.IndianRed;

    private readonly GitCommandRunner m_gitCommandRunner = new();
    private readonly CodeReviewOrchestrator m_orchestrator;
    private readonly BitbucketPullRequestMetadataClient m_pullRequestMetadataClient = new();
    private readonly Settings m_settings = Settings.Instance;
    private readonly ObservableCollection<LogLineEntry> m_logLines = new();
    private CancellationTokenSource m_previewUpdateCancellation;
    private bool m_busy;
    private bool m_isGitAvailable = true;
    private bool m_gitAvailabilityChecked;
    private bool m_normalizingPullRequestUrl;

    public MainWindow()
    {
        m_orchestrator = new CodeReviewOrchestrator(m_gitCommandRunner);
        InitializeComponent();
        PullRequestUrlTextBox.AddHandler(DragDrop.DragOverEvent, PullRequestUrlTextBox_OnDragOver);
        PullRequestUrlTextBox.AddHandler(DragDrop.DropEvent, PullRequestUrlTextBox_OnDrop);
        PullRequestUrlTextBox.TextChanged += PullRequestUrlTextBox_OnTextChanged;
        RepositoryRootTextBox.TextChanged += RepositoryRootTextBox_OnTextChanged;
        AutoOpenSolutionCheckBox.IsCheckedChanged += AutoOpenSolutionCheckBox_OnIsCheckedChanged;
        RepositoryRootTextBox.LostFocus += RepositoryRootTextBox_OnLostFocus;
        Opened += MainWindow_OnOpened;
        Activated += MainWindow_OnActivated;
        Closing += MainWindow_OnClosing;

        if (!string.IsNullOrWhiteSpace(m_settings.RepositoryRootPath))
            RepositoryRootTextBox.Text = m_settings.RepositoryRootPath;
        AutoOpenSolutionCheckBox.IsChecked = m_settings.AutoOpenSolutionFile;
        LogListBox.ItemsSource = m_logLines;

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

    private async void PrepareReviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetInputs(out var repositoryRoot, out var prUrlText))
            return;

        PersistRepositoryRootPath(repositoryRoot);

        if (!BitbucketPrUrlParser.TryParse(prUrlText, out var pullRequest, out var parseError))
        {
            SetStatus(parseError);
            AppendLog($"Input error: {parseError}");
            DialogService.Instance.ShowMessage("Invalid pull request URL", parseError, null);
            return;
        }

        await ExecuteBusyActionAsync(
            "Preparing review checkout...",
            async () =>
            {
                var metadata = await m_pullRequestMetadataClient.TryGetMetadataAsync(pullRequest);
                var changedPaths = await m_pullRequestMetadataClient.TryGetChangedPathsAsync(pullRequest);

                AppendLog($"PR detected: {pullRequest.SourceUrl}");
                AppendLog($"PR title: {FormatMetadataText(metadata?.Title)}");
                AppendLog($"PR author: {FormatMetadataText(metadata?.Author)}");
                AppendLog($"PR updated: {FormatMetadataUpdated(metadata?.UpdatedAt)}");
                AppendLog($"PR modified files: {(changedPaths.Count > 0 ? changedPaths.Count.ToString() : "N/A")}");

                var result = await m_orchestrator.PrepareReviewAsync(repositoryRoot, pullRequest, changedPaths, AppendLog);

                AppendLog($"Review worktree ready: {result.ReviewWorktreePath}");

                if (!string.IsNullOrWhiteSpace(result.SolutionPath))
                {
                    AppendLog($"Solution selected: {result.SolutionPath}");

                    if (AutoOpenSolutionCheckBox.IsChecked == true)
                    {
                        OpenFileWithShell(result.SolutionPath);
                        AppendLog("Opened solution in default application.");
                    }
                }
                else
                {
                    AppendLog("No .sln file found in review checkout.");
                }

                SetStatus("Review checkout is ready.");
            });
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

    private bool TryGetInputs(out string repositoryRoot, out string prUrlText)
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

    private void OnInputTextChanged()
    {
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

        var urlText = PullRequestUrlTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(urlText))
        {
            PullRequestPreviewTextBlock.IsVisible = false;
            PullRequestPreviewTextBlock.Text = string.Empty;
            PullRequestMetadataTextBlock.IsVisible = false;
            PullRequestMetadataTextBlock.Text = string.Empty;
            return;
        }

        if (!BitbucketPrUrlParser.TryParse(urlText, out var pullRequest, out _))
        {
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
        var updated = metadata.UpdatedAt.HasValue
            ? metadata.UpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "Unknown";

        return $"Title: {title} | Author: {author} | Updated: {updated}";
    }

    private static string FormatMetadataText(string value) =>
        string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();

    private static string FormatMetadataUpdated(DateTimeOffset? updatedAt) =>
        updatedAt.HasValue ? updatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "N/A";

    private void SetBusyState(bool isBusy)
    {
        m_busy = isBusy;
        UpdateActionButtonStates();
    }

    private void UpdateActionButtonStates()
    {
        PrepareReviewButton.IsEnabled = !m_busy && m_isGitAvailable && HasValidPrepareInputs();
        OpenPullRequestButton.IsEnabled = !m_busy && HasValidPullRequestInput();
    }

    private bool HasValidPullRequestInput()
    {
        var prUrlText = PullRequestUrlTextBox.Text?.Trim();
        return !string.IsNullOrWhiteSpace(prUrlText) &&
               BitbucketPrUrlParser.TryParse(prUrlText, out _, out _);
    }

    private bool HasValidPrepareInputs()
    {
        var repositoryRoot = RepositoryRootTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return false;

        var prUrlText = PullRequestUrlTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prUrlText))
            return false;

        return BitbucketPrUrlParser.TryParse(prUrlText, out _, out _);
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

        return HasTimestampPrefix(line) ? TimestampedLogBrush : DetailLogBrush;
    }

    private static bool ContainsErrorMarker(string line) =>
        line?.Contains("ERROR", StringComparison.OrdinalIgnoreCase) == true ||
        line?.Contains("fatal", StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasTimestampPrefix(string line) =>
        !string.IsNullOrEmpty(line) &&
        line.Length >= 10 &&
        line[0] == '[' &&
        line[3] == ':' &&
        line[6] == ':' &&
        line[9] == ']';
    
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

    private void AutoOpenSolutionCheckBox_OnIsCheckedChanged(object sender, RoutedEventArgs e) =>
        PersistAutoOpenSolutionPreference(AutoOpenSolutionCheckBox.IsChecked);

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
        PersistAutoOpenSolutionPreference(AutoOpenSolutionCheckBox.IsChecked);
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
        if (!string.IsNullOrWhiteSpace(PullRequestUrlTextBox.Text))
            return;

        var topLevel = GetTopLevel(this);
        if (topLevel?.Clipboard == null)
            return;

        string clipboardText;
        try
        {
            clipboardText = await topLevel.Clipboard.GetTextAsync();
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

    private void PersistAutoOpenSolutionPreference(bool? autoOpenSolution)
    {
        var normalizedValue = autoOpenSolution != false;
        if (m_settings.AutoOpenSolutionFile == normalizedValue)
            return;

        m_settings.AutoOpenSolutionFile = normalizedValue;

        try
        {
            m_settings.Save();
        }
        catch (Exception exception)
        {
            AppendLog($"Warning: could not save app settings. {exception.Message}");
        }
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
