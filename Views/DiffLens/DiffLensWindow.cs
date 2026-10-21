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
using AvaloniaEdit.Document;
using System.Linq;
using System.IO;
using System.Xml;
using System.Threading.Tasks;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Avalonia.Animation;
using Avalonia.Threading;
using ReviewG33k.Services.DiffLens;
using ReviewG33k.Services;
using ReviewG33k.ViewModels;

namespace ReviewG33k.Views.DiffLens;

/// <summary>
/// Hosts the DiffLens repository canvas and working-copy editor.
/// </summary>
/// <remarks>
/// The editor document stays alive while switching back to the zoomable diff.
/// </remarks>
internal sealed class DiffLensWindow : Window
{
    private readonly string m_repositoryPath;
    private readonly string m_targetBranch;
    private readonly string m_relativePath;
    private readonly TextBlock m_status = new() { Text = "No repository selected.", TextWrapping = TextWrapping.Wrap };
    private readonly DiffLensControl m_canvas = new();
    private readonly TextEditor m_editor = new()
    {
        ShowLineNumbers = true, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 16,
        Background = Brush.Parse("#151E29"), Foreground = Brush.Parse("#D7E3EE"), IsVisible = false
    };
    private readonly DiffLensReviewSnapshot m_review;
    private readonly CommandLineReviewMode m_reviewMode;
    private DiffLensFile m_file;
    private readonly Dictionary<string, DiffLensFile> m_files = new();
    private readonly Dictionary<string, TextDocument> m_documents = new();
    private readonly Dictionary<string, DiffLensFindingActions> m_findingActions = new();
    private readonly DispatcherTimer m_autoSave = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly SemaphoreSlim m_saveGate = new(1, 1);
    private bool m_closeReady;
    private bool m_isClosing;
    private bool m_isClosed;
    private bool m_loading;
    private bool m_settingText;
    private bool m_isEditing;
    private int m_transitionVersion;

    private static string NavigationHint => OperatingSystem.IsMacOS()
        ? "Two-finger scroll: pan · Pinch or ⌘+scroll: zoom · Shift+scroll: long lines"
        : "Scroll or drag: pan · Ctrl+wheel: zoom · Shift+wheel: long lines";

    public DiffLensWindow(string[] args = null, DiffLensReviewSnapshot review = null, CommandLineReviewMode? reviewMode = null)
    {
        m_review = review;
        m_reviewMode = reviewMode ?? review?.Mode ??
            (Argument(args, "--base") is { } branch && branch != "HEAD" ? CommandLineReviewMode.Committed : CommandLineReviewMode.Uncommitted);
        Title = "ReviewG33k DiffLens";
        Width = 1420;
        Height = 920;
        MinWidth = 950;
        MinHeight = 600;
        Background = Brush.Parse("#0C1219");
        m_repositoryPath = Argument(args, "--repo") ?? Settings.Instance.LocalReviewRepositoryPath;
        m_targetBranch = Argument(args, "--base") ?? "HEAD";
        m_relativePath = Argument(args, "--file");
        Title = $"ReviewG33k DiffLens — {m_repositoryPath} ({m_targetBranch})";
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var surface = new Grid();
        surface.Children.Add(m_canvas);
        surface.Children.Add(m_editor);
        if (m_review != null)
        {
            surface.ColumnDefinitions = new ColumnDefinitions("*,340");
            var findingsPanel = CreateFindingsPanel();
            Grid.SetColumn(findingsPanel, 1);
            surface.Children.Add(findingsPanel);
        }
        m_canvas.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) } };
        m_editor.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) } };
        m_editor.Opacity = 0;
        Grid.SetRow(surface, 0);
        root.Children.Add(surface);
        var footer = new DockPanel { Margin = new Thickness(12, 8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var fit = new Button { Content = "Fit all" };
        fit.Click += (_, _) =>
        {
            if (m_isEditing)
                ToggleEditor();
            m_canvas.Fit();
        };
        ToolTip.SetTip(fit, "Fit all changed files (middle-click).");
        actions.Children.Add(fit);
        DockPanel.SetDock(actions, Dock.Left);
        actions.Margin = new Thickness(0, 0, 12, 0);
        m_status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(actions);
        ToolTip.SetTip(footer, NavigationHint);
        footer.Children.Add(m_status);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        Content = root;
        m_canvas.FindingLineResolver = finding => m_findingActions.TryGetValue(finding.FilePath, out var actions)
            ? actions.GetCurrentLine(finding) : finding.LineNumber;
        m_canvas.ViewChanged += (_, _) => UpdateView();
        m_canvas.EditRequested += (_, request) => { SelectFile(request.Path); ToggleEditor(request.Line); };
        m_canvas.ContextRequested += (_, e) =>
        {
            var findings = e.TryGetPosition(m_canvas, out var point) ? m_canvas.FindingsAt(point) : m_canvas.SelectedFindings;
            if (findings.Count == 0)
                return;
            OpenFindingMenu(m_canvas, findings);
            e.Handled = true;
        };
        m_autoSave.Tick += async (_, _) =>
        {
            m_autoSave.Stop();
            await SaveAsync();
        };
        m_editor.TextChanged += (_, _) =>
        {
            if (m_settingText || m_file == null)
                return;
            m_autoSave.Stop();
            m_autoSave.Start();
            m_status.Text = "Saving...";
        };
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                await SaveAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && m_isEditing)
            {
                ToggleEditor();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        Closing += async (_, e) =>
        {
            if (m_closeReady)
                return;
            e.Cancel = true;
            if (m_loading || m_isClosing)
                return;
            m_isClosing = true;
            SetBusy(true);
            m_autoSave.Stop();
            if (await SaveAsync())
            {
                // A dispatcher post can run inside Avalonia's native input callback on macOS.
                // Wait for a timer tick before disposing the window so that callback can return.
                DispatcherTimer.RunOnce(() =>
                {
                    if (m_isClosed)
                        return;
                    m_closeReady = true;
                    Close();
                }, TimeSpan.FromMilliseconds(1), DispatcherPriority.Background);
            }
            else
            {
                m_isClosing = false;
                SetBusy(false);
            }
        };
        Closed += (_, _) =>
        {
            m_isClosed = true;
            ++m_transitionVersion;
            m_autoSave.Stop();
            m_canvas.StopAnimations();
        };
        Opened += async (_, _) =>
        {
            UpdateView();
            if (!string.IsNullOrWhiteSpace(m_repositoryPath))
                await LoadSnapshotAsync();
        };
    }

    private Control CreateFindingsPanel()
    {
        var panel = new DockPanel { Margin = new Thickness(12) };
        var heading = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = $"Review findings ({m_review.Findings.Count})", FontSize = 18 });
        heading.Children.Add(new TextBlock
        {
            Text = "Last scan snapshot. Edits may move lines or resolve issues. Rerun the review to update findings.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#B8A77B"), FontSize = 12
        });
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var items = new StackPanel { Spacing = 8 };
        foreach (var finding in m_review.Findings.OrderBy(finding => finding.FilePath).ThenBy(finding => finding.LineNumber))
        {
            var label = new StackPanel { Spacing = 4 };
            label.Children.Add(new TextBlock { Text = $"{finding.Severity} · {finding.RuleId}", Foreground = Brush.Parse("#E9BC70"), TextWrapping = TextWrapping.Wrap });
            label.Children.Add(new TextBlock { Text = $"{finding.FilePath}:{finding.LineNumber}", FontSize = 12, TextWrapping = TextWrapping.Wrap });
            label.Children.Add(new TextBlock { Text = finding.Message, TextWrapping = TextWrapping.Wrap });
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10) };
            button.Click += (_, _) =>
            {
                if (m_isEditing)
                    ToggleEditor();
                if (m_canvas.FocusFinding(finding.FilePath, m_findingActions.TryGetValue(finding.FilePath, out var actions) ? actions.GetCurrentLine(finding) : finding.LineNumber))
                    m_status.Text = string.Empty;
                else
                    m_status.Text = "Finding is outside this view.";
            };
            button.ContextRequested += (_, e) =>
            {
                OpenFindingMenu(button, [finding]);
                e.Handled = true;
            };
            items.Children.Add(button);
        }
        panel.Children.Add(new ScrollViewer { Content = items, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return panel;
    }

    private TextDocument GetDocument(DiffLensFile file)
    {
        if (!m_documents.TryGetValue(file.RelativePath, out var document))
        {
            m_documents[file.RelativePath] = document = new TextDocument(file.WorkingText);
            var findings = m_review?.FindingsFor(m_repositoryPath, m_targetBranch, m_reviewMode, file.RelativePath) ?? [];
            m_findingActions[file.RelativePath] = new DiffLensFindingActions(file.RelativePath, document, findings);
        }
        return document;
    }

    private void OpenFindingMenu(Control target, IReadOnlyList<CodeSmellFinding> findings)
    {
        if (m_loading || m_isClosing || m_isClosed)
            return;
        ToolTip.SetIsOpen(m_canvas, false);
        var menu = new ContextMenu();
        foreach (var finding in findings)
        {
            var items = CreateFindingMenuItems(finding);
            if (findings.Count == 1)
                menu.ItemsSource = items;
            else
                menu.Items.Add(new MenuItem
                {
                    Header = new TextBlock { Text = finding.Message, MaxWidth = 340, TextWrapping = TextWrapping.Wrap },
                    ItemsSource = items
                });
        }
        target.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (target.ContextMenu == menu)
                target.ContextMenu = null;
        };
        menu.Open(target);
    }

    private MenuItem[] CreateFindingMenuItems(CodeSmellFinding finding)
    {
        DiffLensFindingActions actions = null;
        var reason = "The issue's file is unavailable for editing.";
        var canInsert = false;
        if (m_files.TryGetValue(finding.FilePath, out var file) && file.CanEdit)
        {
            GetDocument(file);
            actions = m_findingActions[file.RelativePath];
            canInsert = actions.CanInsertTodo(finding, out reason);
        }
        var add = new MenuItem { Header = "Add TODO above this line", IsEnabled = canInsert };
        ToolTip.SetTip(add, canInsert ? "Insert an undoable TODO using this issue's instructions" : reason);
        add.Click += async (_, _) => await AddFindingTodoAsync(finding);
        var copy = new MenuItem { Header = "Copy fix instructions", IsEnabled = Clipboard != null };
        copy.Click += async (_, _) =>
        {
            try
            {
                var instructions = actions?.GetFixInstructions(finding) ??
                    $"File: {finding.FilePath}:{finding.LineNumber}\nRule: {finding.RuleId}\nIssue: {finding.Message}";
                if (Clipboard != null)
                {
                    await Clipboard.SetTextAsync(instructions);
                    m_status.Text = "Fix instructions copied.";
                }
            }
            catch (Exception ex)
            {
                m_status.Text = $"Could not copy instructions: {ex.Message}";
            }
        };
        return [add, copy];
    }

    private async Task AddFindingTodoAsync(CodeSmellFinding finding)
    {
        if (m_loading || m_isClosing || m_isClosed || !m_files.TryGetValue(finding.FilePath, out var file) || !file.CanEdit)
            return;
        GetDocument(file);
        if (!m_findingActions[file.RelativePath].TryInsertTodo(finding, out var line, out var reason))
        {
            m_status.Text = reason;
            return;
        }
        SelectFile(file.RelativePath);
        if (!m_isEditing)
            ToggleEditor(line);
        else
        {
            m_editor.TextArea.Caret.Line = line;
            m_editor.ScrollToLine(line);
        }
        await SaveAsync();
    }

    private void SelectFile(string path)
    {
        if (path == null || !m_files.TryGetValue(path, out var file))
        {
            m_file = null;
            return;
        }
        if (m_file == file)
            return;
        m_file = file;
        var document = GetDocument(file);
        m_settingText = true;
        m_editor.Document = document;
        m_settingText = false;
        m_editor.SyntaxHighlighting = Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? CreateHighlighting() : null;
    }

    private void UpdateView()
    {
        if (!m_isEditing)
            SelectFile(m_canvas.SelectedPath);
    }

    private async Task LoadSnapshotAsync()
    {
        if (m_loading || m_isClosing || m_isClosed)
            return;
        SetBusy(true);
        m_autoSave.Stop();
        if (!await SaveAsync())
        {
            SetBusy(false);
            return;
        }
        m_status.Text = "Loading...";
        try
        {
            var changeSet = await new DiffLensFileLoader().LoadReviewSetAsync(m_repositoryPath, m_targetBranch, m_reviewMode, m_relativePath);
            var cells = await Task.Run(() => changeSet.Entries.Select(entry => new DiffLensCell(entry)).ToArray());
            m_files.Clear();
            m_documents.Clear();
            m_findingActions.Clear();
            m_file = null;
            foreach (var entry in changeSet.Entries.Where(entry => entry.File != null))
                m_files[entry.Path] = entry.File;
            m_editor.IsVisible = false;
            m_transitionVersion++;
            m_isEditing = false;
            m_editor.Opacity = 0;
            m_canvas.IsVisible = true;
            m_canvas.Opacity = 1;
            m_canvas.IsHitTestVisible = true;
            foreach (var cell in cells)
                cell.Findings = m_review?.FindingsFor(changeSet.Root, m_targetBranch, m_reviewMode, cell.Entry.Path) ?? [];
            m_canvas.SetFiles(Path.GetFileName(changeSet.Root), cells);
            m_status.Text = cells.Length == 0 ? "No files to review." : "Double-click to edit · Esc to return";
        }
        catch (Exception ex)
        {
            m_status.Text = $"Unable to load: {ex.Message}";
        }
        finally { SetBusy(false); }
    }

    private void ToggleEditor(int? line = null)
    {
        if (m_file?.CanEdit != true || m_loading || m_isClosing || m_isClosed)
            return;
        m_isEditing = !m_isEditing;
        var version = ++m_transitionVersion;
        m_editor.IsVisible = true;
        m_canvas.IsVisible = true;
        m_editor.IsHitTestVisible = m_isEditing;
        m_canvas.IsHitTestVisible = !m_isEditing;
        m_editor.Opacity = m_isEditing ? 1 : 0;
        // The canvas stays opaque underneath the editor throughout the crossfade.
        m_canvas.Opacity = 1;
        _ = FinishTransitionAsync(version);
        if (m_isEditing)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (m_isClosing || m_isClosed || version != m_transitionVersion || !m_isEditing)
                    return;
                m_editor.Focus();
                if (line is { } requestedLine)
                {
                    var targetLine = Math.Clamp(requestedLine, 1, m_editor.Document.LineCount);
                    m_editor.TextArea.Caret.Line = targetLine;
                    m_editor.TextArea.Caret.Column = 1;
                    m_editor.ScrollToLine(targetLine);
                }
            }, DispatcherPriority.Loaded);
            m_status.Text = "Edits save automatically · Esc to return";
        }
        else
        {
            m_canvas.SetFile(m_file.RelativePath, m_file.Baseline, m_editor.Text, false);
            m_canvas.Focus();
            m_autoSave.Stop();
            _ = SaveAsync();
        }
        UpdateView();
    }

    private async Task FinishTransitionAsync(int version)
    {
        await Task.Delay(180);
        if (m_isClosing || m_isClosed || version != m_transitionVersion)
            return;
        m_editor.IsVisible = m_isEditing;
        m_canvas.IsVisible = !m_isEditing;
    }

    private async Task<bool> SaveAsync()
    {
        // Serialize writes without disabling typing. Always read the captured document,
        // since the user may select another file while a write is in progress.
        await m_saveGate.WaitAsync();
        try
        {
            foreach (var (path, document) in m_documents.ToArray())
            {
                var file = m_files[path];
                while (document.Text != file.WorkingText)
                {
                    await file.SaveAsync(document.Text);
                    m_canvas.SetFile(path, file.Baseline, document.Text, false);
                    m_status.Text = "Saved.";
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            m_status.Text = $"Could not save automatically: {ex.Message} Your edits are retained; Ctrl+S retries.";
            return false;
        }
        finally { m_saveGate.Release(); }
    }

    private void SetBusy(bool busy)
    {
        m_loading = busy;
        m_canvas.IsHitTestVisible = !busy && !m_isEditing;
        m_editor.IsReadOnly = busy;
        UpdateView();
    }

    private static IHighlightingDefinition CreateHighlighting()
    {
        const string definition = """
            <SyntaxDefinition name="DiffLens CSharp" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="#7DAA84" />
              <Color name="String" foreground="#DEB887" />
              <Color name="Keyword" foreground="#82B9F0" />
              <RuleSet>
                <Span color="Comment" begin="//" />
                <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                <Span color="String" begin="&quot;" end="&quot;" />
                <Keywords color="Keyword">
                  <Word>using</Word><Word>namespace</Word><Word>public</Word><Word>private</Word>
                  <Word>internal</Word><Word>protected</Word><Word>sealed</Word><Word>class</Word>
                  <Word>readonly</Word><Word>static</Word><Word>async</Word><Word>await</Word>
                  <Word>return</Word><Word>throw</Word><Word>new</Word><Word>var</Word><Word>const</Word>
                  <Word>int</Word><Word>string</Word><Word>bool</Word><Word>void</Word><Word>null</Word>
                  <Word>if</Word><Word>else</Word><Word>for</Word><Word>while</Word><Word>try</Word><Word>catch</Word><Word>when</Word>
                </Keywords>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(definition));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static string Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args ?? [], name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
