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
using System.Linq;
using DTC.Core.ViewModels;

namespace ReviewG33k.ViewModels;

/// <summary>
/// View-model state for the main ReviewG33k window.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private const int PullRequestReviewModeIndex = 0;
    private const int LocalCommittedReviewModeIndex = 1;
    private const int LocalUncommittedReviewModeIndex = 2;

    private readonly Settings m_settings;
    private string m_repositoryRootPath;
    private string m_localRepositoryPath;
    private string m_localBaseBranch;
    private string m_pullRequestUrl = string.Empty;
    private string m_pullRequestMetadataText = string.Empty;
    private string m_statusText = "Ready.";
    private string m_previewPullRequestState;
    private string m_previewPullRequestTitle;
    private int m_reviewModeIndex;
    private int m_scanScopeIndex;
    private bool m_isBusy;
    private bool? m_previewPullRequestIsOpen;
    private bool m_isGitAvailable = true;
    private bool m_canReviewCurrentPullRequest = true;
    private bool m_hasValidPullRequestInput;
    private bool m_hasValidPullRequestPrepareInputs;
    private bool m_hasValidLocalPrepareInputs;
    private bool m_hasAvailableSolution;
    private bool m_canCancelCurrentOperation;
    private bool m_isCancellationRequested;
    private bool m_busyProgressIsIndeterminate = true;
    private double m_busyProgressMaximum = 1;
    private double m_busyProgressValue;

    public ObservableCollection<string> LocalBaseBranchOptions { get; } = [];

    public MainWindowViewModel()
    {
    }
    
    public MainWindowViewModel(Settings settings)
    {
        m_settings = settings ?? throw new ArgumentNullException(nameof(settings));
        m_repositoryRootPath = (m_settings.RepositoryRootPath ?? string.Empty).Trim();
        m_localRepositoryPath = (m_settings.LocalReviewRepositoryPath ?? string.Empty).Trim();
        m_localBaseBranch = NormalizeBaseBranch(m_settings.LocalReviewBaseBranch);
        m_reviewModeIndex = ResolveInitialReviewModeIndex(m_settings);
        m_scanScopeIndex = m_settings.IncludeFullModifiedFiles ? 1 : 0;
        LocalBaseBranchOptions.Add(m_localBaseBranch);
    }

    public string RepositoryRootPath
    {
        get => m_repositoryRootPath;
        set
        {
            var normalizedValue = (value ?? string.Empty).Trim();
            if (!SetField(ref m_repositoryRootPath, normalizedValue))
                return;

            m_settings.RepositoryRootPath = normalizedValue;
            SaveSettingsSafely();
        }
    }

    public string LocalRepositoryPath
    {
        get => m_localRepositoryPath;
        set
        {
            var normalizedValue = (value ?? string.Empty).Trim();
            if (!SetField(ref m_localRepositoryPath, normalizedValue))
                return;

            m_settings.LocalReviewRepositoryPath = normalizedValue;
            SaveSettingsSafely();
        }
    }

    public string LocalBaseBranch
    {
        get => m_localBaseBranch;
        set
        {
            var normalizedValue = NormalizeBaseBranch(value);
            if (!SetField(ref m_localBaseBranch, normalizedValue))
                return;

            m_settings.LocalReviewBaseBranch = normalizedValue;
            SaveSettingsSafely();
        }
    }

    public int ReviewModeIndex
    {
        get => m_reviewModeIndex;
        set
        {
            var normalizedValue = value is >= 0 and <= 2 ? value : 0;
            if (!SetField(ref m_reviewModeIndex, normalizedValue))
                return;

            m_settings.ReviewModeIndex = normalizedValue;
            m_settings.UseLocalCommittedReview = normalizedValue == 1;
            SaveSettingsSafely();
            OnReviewModeUiChanged();
            OnActionStateChanged();
        }
    }

    public int ScanScopeIndex
    {
        get => m_scanScopeIndex;
        set
        {
            var normalizedValue = value == 1 ? 1 : 0;
            if (!SetField(ref m_scanScopeIndex, normalizedValue))
                return;

            m_settings.IncludeFullModifiedFiles = normalizedValue == 1;
            SaveSettingsSafely();
            OnPropertyChanged(nameof(ScanScopeInfoTooltip));
        }
    }

    public string PullRequestUrl
    {
        get => m_pullRequestUrl;
        set => SetField(ref m_pullRequestUrl, value ?? string.Empty);
    }

    public string PullRequestMetadataText
    {
        get => m_pullRequestMetadataText;
        set
        {
            var normalizedValue = value ?? string.Empty;
            if (!SetField(ref m_pullRequestMetadataText, normalizedValue))
                return;

            OnPropertyChanged(nameof(ShowPullRequestMetadata));
        }
    }

    public string StatusText
    {
        get => m_statusText;
        set => SetField(ref m_statusText, value ?? string.Empty);
    }

    public string PreviewPullRequestTitle
    {
        get => m_previewPullRequestTitle;
        private set => SetField(ref m_previewPullRequestTitle, value);
    }

    public string PreviewPullRequestState
    {
        get => m_previewPullRequestState;
        private set
        {
            if (!SetField(ref m_previewPullRequestState, value))
                return;

            OnPropertyChanged(nameof(PreviewPullRequestStateDisplay));
        }
    }

    public bool? PreviewPullRequestIsOpen
    {
        get => m_previewPullRequestIsOpen;
        private set => SetField(ref m_previewPullRequestIsOpen, value);
    }

    public string PreviewPullRequestStateDisplay =>
        string.IsNullOrWhiteSpace(m_previewPullRequestState)
            ? "N/A"
            : m_previewPullRequestState.Trim().ToUpperInvariant();

    public bool IsBusy
    {
        get => m_isBusy;
        set
        {
            if (!SetField(ref m_isBusy, value))
                return;

            OnActionStateChanged();
        }
    }

    public bool ShowPullRequestInputs => m_reviewModeIndex == PullRequestReviewModeIndex;

    public bool ShowPullRequestMetadata =>
        ShowPullRequestInputs && !string.IsNullOrWhiteSpace(m_pullRequestMetadataText);

    public bool ShowLocalBaseBranch => m_reviewModeIndex == LocalCommittedReviewModeIndex;

    public string PrepareReviewButtonText => m_reviewModeIndex == PullRequestReviewModeIndex
        ? "Review PR"
        : "Review Local";

    public string ReviewModeInfoTooltip => m_reviewModeIndex switch
    {
        LocalCommittedReviewModeIndex =>
            "Reviews committed changes in your local repo against the selected base branch (for example origin/main).",
        LocalUncommittedReviewModeIndex =>
            "Reviews your uncommitted working tree changes, without requiring commits.",
        _ =>
            "Reviews a Bitbucket pull request by preparing an isolated local worktree for that PR."
    };

    public string ScanScopeInfoTooltip => m_scanScopeIndex == 1
        ? "Runs checks across entire modified files. More thorough, but slower."
        : "Runs checks only on newly added lines. Faster for targeted reviews.";

    public bool CanPrepareReview => !m_isBusy &&
                                    m_isGitAvailable &&
                                    (ShowPullRequestInputs
                                        ? m_canReviewCurrentPullRequest && m_hasValidPullRequestPrepareInputs
                                        : m_hasValidLocalPrepareInputs);

    public bool CanOpenPullRequest => !m_isBusy &&
                                      ShowPullRequestInputs &&
                                      m_hasValidPullRequestInput;

    public bool CanOpenSolution => !m_isBusy && m_hasAvailableSolution;

    public bool CanCancelProcessing => m_isBusy &&
                                       !m_isCancellationRequested &&
                                       m_canCancelCurrentOperation;

    public bool ShowCancelStoppingText => m_isBusy && m_isCancellationRequested;

    public bool BusyProgressIsIndeterminate
    {
        get => m_busyProgressIsIndeterminate;
        private set => SetField(ref m_busyProgressIsIndeterminate, value);
    }

    public double BusyProgressMaximum
    {
        get => m_busyProgressMaximum;
        private set => SetField(ref m_busyProgressMaximum, value);
    }

    public double BusyProgressValue
    {
        get => m_busyProgressValue;
        private set => SetField(ref m_busyProgressValue, value);
    }

    public void AddLocalBaseBranchOption(string baseBranch)
    {
        var normalizedBranch = NormalizeBranchOption(baseBranch);
        if (string.IsNullOrWhiteSpace(normalizedBranch))
            return;

        if (!LocalBaseBranchOptions.Any(branch => branch.Equals(normalizedBranch, StringComparison.OrdinalIgnoreCase)))
            LocalBaseBranchOptions.Add(normalizedBranch);
    }

    public void SetLocalBaseBranchOptions(IEnumerable<string> branchOptions, string selectedBaseBranch)
    {
        var normalizedOptions = (branchOptions ?? [])
            .Select(NormalizeBranchOption)
            .Where(branch => !string.IsNullOrWhiteSpace(branch))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var normalizedSelection = NormalizeBranchOption(selectedBaseBranch);

        if (!string.IsNullOrWhiteSpace(normalizedSelection) &&
            !normalizedOptions.Any(branch => branch.Equals(normalizedSelection, StringComparison.OrdinalIgnoreCase)))
        {
            normalizedOptions.Add(normalizedSelection);
        }

        if (normalizedOptions.Count == 0)
            normalizedOptions.Add("main");

        LocalBaseBranchOptions.Clear();
        foreach (var branchOption in normalizedOptions)
            LocalBaseBranchOptions.Add(branchOption);

        LocalBaseBranch = normalizedSelection ?? LocalBaseBranchOptions.First();
    }

    public void UpdateActionStateInputs(
        bool isGitAvailable,
        bool canReviewCurrentPullRequest,
        bool hasValidPullRequestInput,
        bool hasValidPullRequestPrepareInputs,
        bool hasValidLocalPrepareInputs,
        bool hasAvailableSolution,
        bool canCancelCurrentOperation,
        bool isCancellationRequested)
    {
        var hasAnyChange = false;
        hasAnyChange |= SetIfDifferent(ref m_isGitAvailable, isGitAvailable);
        hasAnyChange |= SetIfDifferent(ref m_canReviewCurrentPullRequest, canReviewCurrentPullRequest);
        hasAnyChange |= SetIfDifferent(ref m_hasValidPullRequestInput, hasValidPullRequestInput);
        hasAnyChange |= SetIfDifferent(ref m_hasValidPullRequestPrepareInputs, hasValidPullRequestPrepareInputs);
        hasAnyChange |= SetIfDifferent(ref m_hasValidLocalPrepareInputs, hasValidLocalPrepareInputs);
        hasAnyChange |= SetIfDifferent(ref m_hasAvailableSolution, hasAvailableSolution);
        hasAnyChange |= SetIfDifferent(ref m_canCancelCurrentOperation, canCancelCurrentOperation);
        hasAnyChange |= SetIfDifferent(ref m_isCancellationRequested, isCancellationRequested);

        if (hasAnyChange)
            OnActionStateChanged();
    }

    public void UpdatePullRequestReviewState(string title, string state)
    {
        PreviewPullRequestTitle = string.IsNullOrWhiteSpace(title)
            ? null
            : title.Trim();
        PreviewPullRequestState = state?.Trim();
        PreviewPullRequestIsOpen = string.IsNullOrWhiteSpace(PreviewPullRequestState)
            ? null
            : string.Equals(PreviewPullRequestState, "OPEN", StringComparison.OrdinalIgnoreCase);
    }

    public void UpdatePullRequestMetadataPreview(string title, string author, string state)
    {
        UpdatePullRequestReviewState(title, state);

        if (string.IsNullOrWhiteSpace(title) &&
            string.IsNullOrWhiteSpace(author) &&
            string.IsNullOrWhiteSpace(state))
        {
            PullRequestMetadataText = string.Empty;
            return;
        }

        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "(no title)" : title.Trim();
        var normalizedAuthor = string.IsNullOrWhiteSpace(author) ? "Unknown author" : author.Trim();
        PullRequestMetadataText = $"Title: {normalizedTitle} | Author: {normalizedAuthor} | State: {PreviewPullRequestStateDisplay}";
    }

    public void ResetBusyProgress()
    {
        BusyProgressIsIndeterminate = true;
        BusyProgressMaximum = 1;
        BusyProgressValue = 0;
    }

    public void SetBusyProgressIndeterminate() =>
        ResetBusyProgress();

    public void UpdateBusyProgress(int completed, int total)
    {
        if (total <= 0)
        {
            SetBusyProgressIndeterminate();
            return;
        }

        BusyProgressIsIndeterminate = false;
        BusyProgressMaximum = total;
        BusyProgressValue = Math.Clamp(completed, 0, total);
    }

    private static int ResolveInitialReviewModeIndex(Settings settings)
    {
        var configuredIndex = settings.ReviewModeIndex;
        if (configuredIndex is >= PullRequestReviewModeIndex and <= LocalUncommittedReviewModeIndex)
            return configuredIndex;

        return settings.UseLocalCommittedReview ? 1 : 0;
    }

    private static string NormalizeBaseBranch(string baseBranch) =>
        string.IsNullOrWhiteSpace(baseBranch)
            ? "main"
            : baseBranch.Trim();

    private static string NormalizeBranchOption(string baseBranch)
    {
        var normalizedBranch = baseBranch?.Trim();
        return string.IsNullOrWhiteSpace(normalizedBranch)
            ? null
            : normalizedBranch;
    }

    private void SaveSettingsSafely()
    {
        try
        {
            m_settings.Save();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to persist settings: {exception}");
        }
    }

    private void OnReviewModeUiChanged()
    {
        OnPropertyChanged(nameof(ShowPullRequestInputs));
        OnPropertyChanged(nameof(ShowPullRequestMetadata));
        OnPropertyChanged(nameof(ShowLocalBaseBranch));
        OnPropertyChanged(nameof(PrepareReviewButtonText));
        OnPropertyChanged(nameof(ReviewModeInfoTooltip));
    }

    private void OnActionStateChanged()
    {
        OnPropertyChanged(nameof(CanPrepareReview));
        OnPropertyChanged(nameof(CanOpenPullRequest));
        OnPropertyChanged(nameof(CanOpenSolution));
        OnPropertyChanged(nameof(CanCancelProcessing));
        OnPropertyChanged(nameof(ShowCancelStoppingText));
    }

    private static bool SetIfDifferent<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        return true;
    }
}
