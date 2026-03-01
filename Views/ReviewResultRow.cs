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
    public enum ActionAvailability
    {
        Hidden,
        Disabled,
        Enabled
    }

    private static readonly IBrush IncludedIssueBrush = new SolidColorBrush(Color.Parse("#E8F0FF"));
    private static readonly IBrush ExcludedIssueBrush = new SolidColorBrush(Color.Parse("#6F7D9A"));
    private static readonly IBrush IncludedLocationBrush = new SolidColorBrush(Color.Parse("#9FB0D0"));
    private static readonly IBrush ExcludedLocationBrush = new SolidColorBrush(Color.Parse("#5D6A84"));

    private readonly ActionAvailability m_commentAvailability;
    private readonly ActionAvailability m_fixAvailability;
    private readonly ActionAvailability m_codexPromptAvailability;

    private bool m_isIncluded = true;
    private bool m_isPostingComment;
    private bool m_hasPostedComment;
    private bool m_isFixing;
    private bool m_hasBeenFixed;

    public ReviewResultRow(
        CodeSmellFinding finding,
        bool canOpen,
        ActionAvailability commentAvailability,
        ActionAvailability fixAvailability,
        ActionAvailability codexPromptAvailability,
        bool hasExistingComment = false)
    {
        Finding = finding;
        RuleId = finding?.RuleId ?? string.Empty;
        SeverityText = finding == null ? string.Empty : finding.Severity.ToString().ToUpperInvariant();
        IssueSummary = finding?.Message ?? string.Empty;
        IssueFull = IssueSummary;
        IssueLocation = finding == null
            ? string.Empty
            : finding.LineNumber > 0
                ? $"{finding.FilePath}:{finding.LineNumber}"
                : finding.FilePath ?? string.Empty;
        CanOpen = canOpen;
        m_commentAvailability = commentAvailability;
        m_fixAvailability = fixAvailability;
        m_codexPromptAvailability = codexPromptAvailability;
        m_hasPostedComment = hasExistingComment;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public CodeSmellFinding Finding { get; }

    public string RuleId { get; }

    public string SeverityText { get; }

    public string IssueSummary { get; }

    public string IssueFull { get; }

    public string IssueLocation { get; }

    public bool IsImportantSeverity => Finding?.Severity == CodeReviewFindingSeverity.Important;

    public bool IsSuggestionSeverity => Finding?.Severity == CodeReviewFindingSeverity.Suggestion;

    public bool CanOpen { get; }

    public bool CanComment => m_commentAvailability == ActionAvailability.Enabled;

    public bool ShowCommentButton => m_commentAvailability != ActionAvailability.Hidden;

    public bool ShowFixButton => m_fixAvailability != ActionAvailability.Hidden;

    public bool CanFix => m_fixAvailability == ActionAvailability.Enabled;

    public bool ShowCodexPromptButton => m_codexPromptAvailability != ActionAvailability.Hidden;

    public bool CanCreateCodexPrompt => m_codexPromptAvailability == ActionAvailability.Enabled;

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
