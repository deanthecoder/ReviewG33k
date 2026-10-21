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
    private readonly TextBox m_repository = new() { Watermark = "Repository folder", MinWidth = 270 };
    private readonly TextBox m_target = new() { Text = "main", Width = 120, Watermark = "Target branch" };
    private readonly TextBox m_path = new() { Watermark = "Relative file path (blank = all changed files)", MinWidth = 270 };
    private readonly TextBlock m_status = new() { Text = "Load a file to begin.", TextWrapping = TextWrapping.Wrap };
    private readonly DiffLensControl m_canvas = new();
    private readonly TextEditor m_editor = new()
    {
        ShowLineNumbers = true, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 16,
        Background = Brush.Parse("#151E29"), Foreground = Brush.Parse("#D7E3EE"), IsVisible = false
    };
    private readonly Button m_load = new() { Content = "Load / Refresh" };
    private readonly DiffLensReviewSnapshot m_review;
    private DiffLensFile m_file;
    private readonly Dictionary<string, DiffLensFile> m_files = new();
    private readonly Dictionary<string, TextDocument> m_documents = new();
    private readonly DispatcherTimer m_autoSave = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly SemaphoreSlim m_saveGate = new(1, 1);
    private bool m_closeReady;
    private bool m_loading;
    private bool m_settingText;
    private bool m_isEditing;
    private int m_transitionVersion;

    public DiffLensWindow(string[] args = null, DiffLensReviewSnapshot review = null)
    {
        m_review = review;
        Title = "ReviewG33k DiffLens";
        Width = 1420;
        Height = 920;
        MinWidth = 950;
        MinHeight = 600;
        Background = Brush.Parse("#0C1219");
        m_repository.Text = Argument(args, "--repo") ?? Settings.Instance.LocalReviewRepositoryPath;
        m_target.Text = Argument(args, "--base") ?? Settings.Instance.LocalReviewBaseBranch ?? "main";
        m_path.Text = Argument(args, "--file");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        var heading = new StackPanel { Margin = new Thickness(20, 15), Spacing = 5 };
        heading.Children.Add(new TextBlock { Text = "ReviewG33k  /  DiffLens", FontSize = 24, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Branch base → current files    ·    Ctrl+wheel: zoom    ·    Drag: pan    ·    Shift+wheel: scroll long lines", Foreground = Brush.Parse("#91A7B9") });
        var configured = !string.IsNullOrWhiteSpace(Argument(args, "--repo"));
        if (!configured)
            root.Children.Add(heading);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 0, 20, 12) };
        toolbar.Children.Add(m_repository);
        toolbar.Children.Add(m_target);
        toolbar.Children.Add(m_path);
        toolbar.Children.Add(m_load);
        Grid.SetRow(toolbar, 1);
        if (!configured)
            root.Children.Add(toolbar);
        else
            Title = $"ReviewG33k DiffLens — {m_repository.Text} ({m_target.Text})";
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
        Grid.SetRow(surface, 2);
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
        ToolTip.SetTip(fit, "Fit all changed files (middle-click). F5 refreshes from disk.");
        actions.Children.Add(fit);
        DockPanel.SetDock(actions, Dock.Left);
        actions.Margin = new Thickness(0, 0, 12, 0);
        m_status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(actions);
        footer.Children.Add(m_status);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        Content = root;
        m_canvas.ViewChanged += (_, _) => UpdateView();
        m_canvas.EditRequested += (_, request) => { SelectFile(request.Path); ToggleEditor(request.Line); };
        m_load.Click += async (_, _) => await LoadAsync();
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
            m_status.Text = $"{m_file.RelativePath} · Saving automatically...";
        };
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                await SaveAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                await LoadAsync();
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
            if (m_loading)
                return;
            SetBusy(true);
            m_autoSave.Stop();
            if (await SaveAsync())
            {
                m_closeReady = true;
                Dispatcher.UIThread.Post(Close);
            }
            else
                SetBusy(false);
        };
        Closed += (_, _) => m_autoSave.Stop();
        Opened += async (_, _) =>
        {
            UpdateView();
            if (!string.IsNullOrWhiteSpace(m_repository.Text))
                await LoadAsync();
        };
    }

    private Control CreateFindingsPanel()
    {
        var panel = new DockPanel { Margin = new Thickness(12) };
        var heading = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock { Text = $"Review findings ({m_review.Findings.Count})", FontSize = 18 });
        heading.Children.Add(new TextBlock
        {
            Text = "Last scan snapshot. Edits may move lines or resolve issues. Rerun the review to update findings; F5 refreshes code only.",
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
                if (m_canvas.FocusFinding(finding.FilePath, finding.LineNumber))
                    m_status.Text = $"Last scan · {finding.FilePath}:{finding.LineNumber} · {finding.Message}";
                else
                    m_status.Text = $"{finding.FilePath}: this finding is outside the current changed-file canvas. Use Review Results to open its source.";
            };
            items.Children.Add(button);
        }
        panel.Children.Add(new ScrollViewer { Content = items, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return panel;
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
        if (!m_documents.TryGetValue(path, out var document))
            m_documents[path] = document = new TextDocument(file.WorkingText);
        m_settingText = true;
        m_editor.Document = document;
        m_settingText = false;
        m_editor.SyntaxHighlighting = Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) ? CreateHighlighting() : null;
        m_status.Text = path + " · Edits save automatically";
    }

    private void UpdateView()
    {
        if (!m_isEditing)
            SelectFile(m_canvas.SelectedPath);
    }

    private async Task LoadAsync()
    {
        if (m_loading)
            return;
        SetBusy(true);
        m_autoSave.Stop();
        if (!await SaveAsync())
        {
            SetBusy(false);
            return;
        }
        m_status.Text = "Resolving branch base and loading working files...";
        try
        {
            var changeSet = await new DiffLensFileLoader().LoadSetAsync(m_repository.Text, m_target.Text, m_path.Text);
            var cells = await Task.Run(() => changeSet.Entries.Select(entry => new DiffLensCell(entry)).ToArray());
            m_files.Clear();
            m_documents.Clear();
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
            if (m_review?.Matches(changeSet.Root, m_target.Text) == true)
                foreach (var cell in cells)
                    cell.Findings = m_review.Findings.Where(finding => string.Equals(finding.FilePath, cell.Entry.Path, StringComparison.OrdinalIgnoreCase)).ToArray();
            m_canvas.SetFiles(Path.GetFileName(changeSet.Root), cells);
            m_status.Text = $"{cells.Length} changed files · Base {changeSet.BaseRevision[..12]} → working file  ·  Ctrl+wheel zooms · Double-click edits · Esc returns · F5 refreshes.";
        }
        catch (Exception ex)
        {
            m_status.Text = $"Unable to load: {ex.Message}";
        }
        finally { SetBusy(false); }
    }

    private void ToggleEditor(int? line = null)
    {
        if (m_file?.CanEdit != true || m_loading)
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
                if (version != m_transitionVersion || !m_isEditing)
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
            m_status.Text = $"{m_file.RelativePath} · Edits save automatically; Esc returns to the diff.";
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
        if (version != m_transitionVersion)
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
                    m_status.Text = $"{path} · Saved automatically";
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
        m_load.IsEnabled = !busy;
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
