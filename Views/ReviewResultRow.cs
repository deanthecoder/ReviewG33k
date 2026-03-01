// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using ReviewG33k.Services;

namespace ReviewG33k.Views;

public sealed class ReviewResultRow : INotifyPropertyChanged
{
    private static readonly IBrush IncludedIssueBrush = Brushes.Gainsboro;
    private static readonly IBrush ExcludedIssueBrush = Brushes.Gray;
    private static readonly IBrush IncludedLocationBrush = Brushes.Gray;
    private static readonly IBrush ExcludedLocationBrush = new SolidColorBrush(Color.Parse("#6B6B6B"));

    private bool m_isIncluded = true;
    private bool m_isPostingComment;
    private bool m_hasPostedComment;
    private bool m_isFixing;
    private bool m_hasBeenFixed;

    public ReviewResultRow(
        CodeSmellFinding finding,
        string ruleId,
        string severityText,
        string issueSummary,
        string issueFull,
        string issueLocation,
        bool canOpen,
        bool canComment,
        bool showCommentButton,
        bool showFixButton,
        bool canFix,
        bool showCodexPromptButton,
        bool canCreateCodexPrompt,
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
        ShowFixButton = showFixButton;
        CanFix = canFix;
        ShowCodexPromptButton = showCodexPromptButton;
        CanCreateCodexPrompt = canCreateCodexPrompt;
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

    public bool ShowFixButton { get; }

    public bool CanFix { get; }

    public bool ShowCodexPromptButton { get; }

    public bool CanCreateCodexPrompt { get; }

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
            RaisePropertyChanged(nameof(CanFixActive));
            RaisePropertyChanged(nameof(CanCodexPromptActive));
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
        }
    }

    public bool IsFixing
    {
        get => m_isFixing;
        set
        {
            if (m_isFixing == value)
                return;

            m_isFixing = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CanFixActive));
        }
    }

    public bool HasBeenFixed
    {
        get => m_hasBeenFixed;
        set
        {
            if (m_hasBeenFixed == value)
                return;

            m_hasBeenFixed = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CanFixActive));
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
        }
    }

    public bool CanOpenActive => CanOpen && IsIncluded;

    public bool CanCommentActive => CanComment && IsIncluded && !IsPostingComment && !HasPostedComment;

    public bool CanFixActive => CanFix && IsIncluded && !IsFixing && !HasBeenFixed;

    public bool CanCodexPromptActive => CanCreateCodexPrompt && IsIncluded;

    public string UntickSameTypeMenuHeader =>
        string.IsNullOrWhiteSpace(RuleId)
            ? "Untick issues of this type"
            : $"Untick all `{RuleId}` issues";

    public IBrush IssueForeground => IsIncluded ? IncludedIssueBrush : ExcludedIssueBrush;

    public IBrush IssueLocationForeground => IsIncluded ? IncludedLocationBrush : ExcludedLocationBrush;

    private void RaisePropertyChanged([CallerMemberName] string propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
