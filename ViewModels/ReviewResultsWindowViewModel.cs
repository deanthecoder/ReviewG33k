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
using System.Threading.Tasks;
using System.Windows.Input;
using DTC.Core.Commands;
using DTC.Core.ViewModels;
using Material.Icons;
using ReviewG33k.Services;
using ReviewG33k.Views;

namespace ReviewG33k.ViewModels;

internal sealed class ReviewResultsWindowViewModel : ViewModelBase
{
    private string m_summaryText = "No review findings";
    private string m_previewHeaderText = "Preview";
    private string m_previewText = "Select an issue to preview surrounding file content.";
    private string m_previewFileName;
    private bool m_canCopyPreviewFileName;
    private string m_copyPreviewFileNameToolTip = "Copy file name to clipboard";
    private bool m_canToggleAll;
    private bool m_canExportToClipboard;
    private bool m_canCommentSelected;
    private bool m_canOpenSelected;
    private ReviewResultRow m_selectedRow;
    private bool m_isSelectedOpenTargetAvailable = true;
    private string m_openSelectedTargetText = "Open";
    private MaterialIconKind m_openSelectedTargetIconKind = MaterialIconKind.CodeTags;
    private string m_openSelectedToolTip = "Open selected finding using the chosen app";
    private CommandBase m_fixFindingCommandImpl;
    private CommandBase m_createCodexPromptCommandImpl;
    private CommandBase m_commentFindingCommandImpl;
    private CommandBase m_toggleAllCommandImpl;
    private CommandBase m_commentSelectedCommandImpl;
    private CommandBase m_exportToClipboardCommandImpl;
    private CommandBase m_copyPreviewFileNameCommandImpl;
    private CommandBase m_openSelectedCommandImpl;
    private CommandBase m_tickSameTypeCommandImpl;
    private CommandBase m_untickSameTypeCommandImpl;

    public ObservableCollection<ReviewResultRow> Rows { get; } = [];
    public ICommand FixFindingCommand { get; private set; }
    public ICommand CreateCodexPromptCommand { get; private set; }
    public ICommand CommentFindingCommand { get; private set; }
    public ICommand ToggleAllCommand { get; private set; }
    public ICommand CommentSelectedCommand { get; private set; }
    public ICommand ExportToClipboardCommand { get; private set; }
    public ICommand CopyPreviewFileNameCommand { get; private set; }
    public ICommand OpenSelectedCommand { get; private set; }
    public ICommand TickSameTypeCommand { get; private set; }
    public ICommand UntickSameTypeCommand { get; private set; }

    public ReviewResultsWindowViewModel()
    {
        InitializeDisabledCommands();
    }

    public string SummaryText
    {
        get => m_summaryText;
        set => SetField(ref m_summaryText, value ?? string.Empty);
    }

    public string PreviewHeaderText
    {
        get => m_previewHeaderText;
        set => SetField(ref m_previewHeaderText, value ?? "Preview");
    }

    public string PreviewText
    {
        get => m_previewText;
        set => SetField(ref m_previewText, value ?? string.Empty);
    }

    public string PreviewFileName
    {
        get => m_previewFileName;
        set
        {
            var normalizedName = string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
            if (!SetField(ref m_previewFileName, normalizedName))
                return;

            var hasPreviewFileName = !string.IsNullOrWhiteSpace(normalizedName);
            CanCopyPreviewFileName = hasPreviewFileName;
            CopyPreviewFileNameToolTip = hasPreviewFileName
                ? $"Copy '{normalizedName}' to clipboard"
                : "Copy file name to clipboard";
        }
    }

    public bool CanCopyPreviewFileName
    {
        get => m_canCopyPreviewFileName;
        set
        {
            if (!SetField(ref m_canCopyPreviewFileName, value))
                return;

            RaiseCommandCanExecuteChanged();
        }
    }

    public string CopyPreviewFileNameToolTip
    {
        get => m_copyPreviewFileNameToolTip;
        set => SetField(ref m_copyPreviewFileNameToolTip, value ?? "Copy file name to clipboard");
    }

    public bool CanToggleAll
    {
        get => m_canToggleAll;
        set
        {
            if (!SetField(ref m_canToggleAll, value))
                return;

            RaiseCommandCanExecuteChanged();
        }
    }

    public bool CanExportToClipboard
    {
        get => m_canExportToClipboard;
        set
        {
            if (!SetField(ref m_canExportToClipboard, value))
                return;

            RaiseCommandCanExecuteChanged();
        }
    }

    public bool CanCommentSelected
    {
        get => m_canCommentSelected;
        set
        {
            if (!SetField(ref m_canCommentSelected, value))
                return;

            RaiseCommandCanExecuteChanged();
        }
    }

    public bool CanOpenSelected
    {
        get => m_canOpenSelected;
        set
        {
            if (!SetField(ref m_canOpenSelected, value))
                return;

            RaiseCommandCanExecuteChanged();
        }
    }

    public ReviewResultRow SelectedRow
    {
        get => m_selectedRow;
        set
        {
            if (!SetField(ref m_selectedRow, value))
                return;

            RefreshOpenSelectedState();
            RaiseCommandCanExecuteChanged();
        }
    }

    public string OpenSelectedTargetText
    {
        get => m_openSelectedTargetText;
        set => SetField(ref m_openSelectedTargetText, value ?? "Open");
    }

    public MaterialIconKind OpenSelectedTargetIconKind
    {
        get => m_openSelectedTargetIconKind;
        set => SetField(ref m_openSelectedTargetIconKind, value);
    }

    public string OpenSelectedToolTip
    {
        get => m_openSelectedToolTip;
        set => SetField(ref m_openSelectedToolTip, value ?? "Open selected finding using the chosen app");
    }

    public void SetRows(IEnumerable<ReviewResultRow> rows)
    {
        Rows.Clear();
        foreach (var row in rows ?? [])
            Rows.Add(row);
    }

    public void ApplySummaryText(string summaryText) =>
        SummaryText = string.IsNullOrWhiteSpace(summaryText)
            ? "No review findings"
            : summaryText.Trim();

    public void ApplyBatchActionState(ReviewResultsBatchActionState actionState)
    {
        CanToggleAll = actionState.CanToggleAll;
        CanExportToClipboard = actionState.CanExportToClipboard;
        CanCommentSelected = actionState.CanCommentSelected;
    }

    public void ConfigureCommands(
        Func<ReviewResultRow, Task> fixFindingAsync,
        Func<ReviewResultRow, Task> createCodexPromptAsync,
        Func<ReviewResultRow, Task> commentFindingAsync,
        Action toggleAll,
        Func<Task> commentSelectedAsync,
        Func<Task> exportToClipboardAsync,
        Func<Task> copyPreviewFileNameAsync,
        Func<Task> openSelectedAsync,
        Action<ReviewResultRow> tickSameType,
        Action<ReviewResultRow> untickSameType)
    {
        ArgumentNullException.ThrowIfNull(fixFindingAsync);
        ArgumentNullException.ThrowIfNull(createCodexPromptAsync);
        ArgumentNullException.ThrowIfNull(commentFindingAsync);
        ArgumentNullException.ThrowIfNull(toggleAll);
        ArgumentNullException.ThrowIfNull(commentSelectedAsync);
        ArgumentNullException.ThrowIfNull(exportToClipboardAsync);
        ArgumentNullException.ThrowIfNull(copyPreviewFileNameAsync);
        ArgumentNullException.ThrowIfNull(openSelectedAsync);
        ArgumentNullException.ThrowIfNull(tickSameType);
        ArgumentNullException.ThrowIfNull(untickSameType);

        m_fixFindingCommandImpl = new AsyncRelayCommand(
            parameter => fixFindingAsync(parameter as ReviewResultRow),
            parameter => parameter is ReviewResultRow row && row.CanFixActive);
        m_createCodexPromptCommandImpl = new AsyncRelayCommand(
            parameter => createCodexPromptAsync(parameter as ReviewResultRow),
            parameter => parameter is ReviewResultRow row && row.CanCodexPromptActive);
        m_commentFindingCommandImpl = new AsyncRelayCommand(
            parameter => commentFindingAsync(parameter as ReviewResultRow),
            parameter => parameter is ReviewResultRow row && row.CanCommentActive);
        m_toggleAllCommandImpl = new RelayCommand(
            _ => toggleAll(),
            _ => CanToggleAll);
        m_commentSelectedCommandImpl = new AsyncRelayCommand(
            _ => commentSelectedAsync(),
            _ => CanCommentSelected);
        m_exportToClipboardCommandImpl = new AsyncRelayCommand(
            _ => exportToClipboardAsync(),
            _ => CanExportToClipboard);
        m_copyPreviewFileNameCommandImpl = new AsyncRelayCommand(
            _ => copyPreviewFileNameAsync(),
            _ => CanCopyPreviewFileName);
        m_openSelectedCommandImpl = new AsyncRelayCommand(
            _ => openSelectedAsync(),
            _ => CanOpenSelected);
        m_tickSameTypeCommandImpl = new RelayCommand(
            parameter => tickSameType(parameter as ReviewResultRow),
            parameter => parameter is ReviewResultRow row && !string.IsNullOrWhiteSpace(row.RuleId));
        m_untickSameTypeCommandImpl = new RelayCommand(
            parameter => untickSameType(parameter as ReviewResultRow),
            parameter => parameter is ReviewResultRow row && !string.IsNullOrWhiteSpace(row.RuleId));

        FixFindingCommand = m_fixFindingCommandImpl;
        CreateCodexPromptCommand = m_createCodexPromptCommandImpl;
        CommentFindingCommand = m_commentFindingCommandImpl;
        ToggleAllCommand = m_toggleAllCommandImpl;
        CommentSelectedCommand = m_commentSelectedCommandImpl;
        ExportToClipboardCommand = m_exportToClipboardCommandImpl;
        CopyPreviewFileNameCommand = m_copyPreviewFileNameCommandImpl;
        OpenSelectedCommand = m_openSelectedCommandImpl;
        TickSameTypeCommand = m_tickSameTypeCommandImpl;
        UntickSameTypeCommand = m_untickSameTypeCommandImpl;

        OnPropertyChanged(nameof(FixFindingCommand));
        OnPropertyChanged(nameof(CreateCodexPromptCommand));
        OnPropertyChanged(nameof(CommentFindingCommand));
        OnPropertyChanged(nameof(ToggleAllCommand));
        OnPropertyChanged(nameof(CommentSelectedCommand));
        OnPropertyChanged(nameof(ExportToClipboardCommand));
        OnPropertyChanged(nameof(CopyPreviewFileNameCommand));
        OnPropertyChanged(nameof(OpenSelectedCommand));
        OnPropertyChanged(nameof(TickSameTypeCommand));
        OnPropertyChanged(nameof(UntickSameTypeCommand));
        RaiseCommandCanExecuteChanged();
    }

    public void UpdateOpenTargetState(string targetDisplayName, MaterialIconKind iconKind, bool isSelectedTargetAvailable)
    {
        var normalizedDisplayName = string.IsNullOrWhiteSpace(targetDisplayName)
            ? "Code"
            : targetDisplayName.Trim();
        OpenSelectedTargetText = $"Open ({normalizedDisplayName})";
        OpenSelectedTargetIconKind = iconKind;
        OpenSelectedToolTip = $"Open selected finding using {normalizedDisplayName}";

        if (!SetIfDifferent(ref m_isSelectedOpenTargetAvailable, isSelectedTargetAvailable))
            return;

        RefreshOpenSelectedState();
    }

    public void RefreshOpenSelectedState()
    {
        CanOpenSelected = SelectedRow?.CanOpenActive == true &&
                          m_isSelectedOpenTargetAvailable;
    }

    private void InitializeDisabledCommands()
    {
        m_fixFindingCommandImpl = new AsyncRelayCommand(_ => Task.CompletedTask, _ => false);
        m_createCodexPromptCommandImpl = new AsyncRelayCommand(_ => Task.CompletedTask, _ => false);
        m_commentFindingCommandImpl = new AsyncRelayCommand(_ => Task.CompletedTask, _ => false);
        m_toggleAllCommandImpl = new RelayCommand(_ => { }, _ => false);
        m_commentSelectedCommandImpl = new AsyncRelayCommand(_ => Task.CompletedTask, _ => false);
        m_exportToClipboardCommandImpl = new AsyncRelayCommand(_ => Task.CompletedTask, _ => false);
        m_copyPreviewFileNameCommandImpl = new AsyncRelayCommand(_ => Task.CompletedTask, _ => false);
        m_openSelectedCommandImpl = new AsyncRelayCommand(_ => Task.CompletedTask, _ => false);
        m_tickSameTypeCommandImpl = new RelayCommand(_ => { }, _ => false);
        m_untickSameTypeCommandImpl = new RelayCommand(_ => { }, _ => false);

        FixFindingCommand = m_fixFindingCommandImpl;
        CreateCodexPromptCommand = m_createCodexPromptCommandImpl;
        CommentFindingCommand = m_commentFindingCommandImpl;
        ToggleAllCommand = m_toggleAllCommandImpl;
        CommentSelectedCommand = m_commentSelectedCommandImpl;
        ExportToClipboardCommand = m_exportToClipboardCommandImpl;
        CopyPreviewFileNameCommand = m_copyPreviewFileNameCommandImpl;
        OpenSelectedCommand = m_openSelectedCommandImpl;
        TickSameTypeCommand = m_tickSameTypeCommandImpl;
        UntickSameTypeCommand = m_untickSameTypeCommandImpl;
    }

    private void RaiseCommandCanExecuteChanged()
    {
        m_fixFindingCommandImpl?.RaiseCanExecuteChanged();
        m_createCodexPromptCommandImpl?.RaiseCanExecuteChanged();
        m_commentFindingCommandImpl?.RaiseCanExecuteChanged();
        m_toggleAllCommandImpl?.RaiseCanExecuteChanged();
        m_commentSelectedCommandImpl?.RaiseCanExecuteChanged();
        m_exportToClipboardCommandImpl?.RaiseCanExecuteChanged();
        m_copyPreviewFileNameCommandImpl?.RaiseCanExecuteChanged();
        m_openSelectedCommandImpl?.RaiseCanExecuteChanged();
        m_tickSameTypeCommandImpl?.RaiseCanExecuteChanged();
        m_untickSameTypeCommandImpl?.RaiseCanExecuteChanged();
    }

    private static bool SetIfDifferent<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        return true;
    }
}
