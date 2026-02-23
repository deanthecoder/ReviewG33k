using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ReviewG33k.Services;

namespace ReviewG33k.Views;

public partial class MainWindow : Window
{
    private readonly CodeReviewOrchestrator m_orchestrator = new(new GitCommandRunner());
    private readonly UserSettingsStore m_userSettingsStore = new();
    private UserSettings m_userSettings;
    private bool m_busy;

    public MainWindow()
    {
        InitializeComponent();
        PullRequestUrlTextBox.AddHandler(DragDrop.DragOverEvent, PullRequestUrlTextBox_OnDragOver);
        PullRequestUrlTextBox.AddHandler(DragDrop.DropEvent, PullRequestUrlTextBox_OnDrop);
        RepositoryRootTextBox.LostFocus += RepositoryRootTextBox_OnLostFocus;
        Closing += MainWindow_OnClosing;

        m_userSettings = m_userSettingsStore.Load();
        if (!string.IsNullOrWhiteSpace(m_userSettings.RepositoryRootPath))
            RepositoryRootTextBox.Text = m_userSettings.RepositoryRootPath;
    }

    private async void BrowseRepositoryRootButton_OnClick(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
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

    private async void PrepareReviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetInputs(out var repositoryRoot, out var prUrlText))
            return;

        PersistRepositoryRootPath(repositoryRoot);

        if (!BitbucketPrUrlParser.TryParse(prUrlText, out var pullRequest, out var parseError))
        {
            SetStatus(parseError);
            AppendLog($"Input error: {parseError}");
            return;
        }

        await ExecuteBusyActionAsync(
            "Preparing review checkout...",
            async () =>
            {
                AppendLog($"PR detected: {pullRequest.SourceUrl}");
                var result = await m_orchestrator.PrepareReviewAsync(repositoryRoot, pullRequest, AppendLog);

                AppendLog($"Review worktree ready: {result.ReviewWorktreePath}");

                if (!string.IsNullOrWhiteSpace(result.SolutionPath))
                {
                    AppendLog($"Top-level solution found: {result.SolutionPath}");

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

    private async void ClearCodeReviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        var repositoryRoot = RepositoryRootTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            SetStatus("Set repo root folder first.");
            return;
        }

        PersistRepositoryRootPath(repositoryRoot);

        await ExecuteBusyActionAsync(
            "Clearing CodeReview folder...",
            async () =>
            {
                await m_orchestrator.ClearCodeReviewFolderAsync(repositoryRoot, AppendLog);
                SetStatus("CodeReview folder cleared.");
            });
    }

    private async Task ExecuteBusyActionAsync(string statusText, Func<Task> action)
    {
        if (m_busy)
            return;

        try
        {
            m_busy = true;
            SetBusyState(true);
            SetStatus(statusText);
            await action();
        }
        catch (Exception exception)
        {
            SetStatus("Operation failed. See log for details.");
            AppendLog($"ERROR: {exception.Message}");
        }
        finally
        {
            SetBusyState(false);
            m_busy = false;
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
            return false;
        }

        if (!Directory.Exists(repositoryRoot))
        {
            SetStatus($"Repo root folder does not exist: {repositoryRoot}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(prUrlText))
        {
            SetStatus("Paste or drop a Bitbucket pull request URL.");
            return false;
        }

        return true;
    }

    private void SetBusyState(bool isBusy)
    {
        PrepareReviewButton.IsEnabled = !isBusy;
        ClearCodeReviewButton.IsEnabled = !isBusy;
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
        var line = $"[{timestamp}] {message}";

        if (string.IsNullOrWhiteSpace(LogTextBox.Text))
        {
            LogTextBox.Text = line;
        }
        else
        {
            LogTextBox.Text = $"{LogTextBox.Text}{Environment.NewLine}{line}";
        }

        LogTextBox.CaretIndex = LogTextBox.Text.Length;
    }

    private void RepositoryRootTextBox_OnLostFocus(object sender, RoutedEventArgs e) =>
        PersistRepositoryRootPath(RepositoryRootTextBox.Text);

    private void MainWindow_OnClosing(object sender, WindowClosingEventArgs e) =>
        PersistRepositoryRootPath(RepositoryRootTextBox.Text);

    private void PersistRepositoryRootPath(string repositoryRootPath)
    {
        var normalizedPath = repositoryRootPath?.Trim();
        if (m_userSettings == null)
            m_userSettings = new UserSettings();

        if (string.Equals(m_userSettings.RepositoryRootPath, normalizedPath, StringComparison.Ordinal))
            return;

        m_userSettings.RepositoryRootPath = normalizedPath;

        try
        {
            m_userSettingsStore.Save(m_userSettings);
        }
        catch (Exception exception)
        {
            AppendLog($"Warning: could not save app settings. {exception.Message}");
        }
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
