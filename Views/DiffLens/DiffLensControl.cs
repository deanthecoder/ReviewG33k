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
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using DiffPlex.DiffBuilder.Model;
using ReviewG33k.Services.DiffLens;
using ReviewG33k.Services;

namespace ReviewG33k.Views.DiffLens;

/// <summary>
/// Displays changed files in folder groups on a pointer-centered canvas with per-file diff presentation.
/// </summary>
/// <remarks>
/// Lightweight drawing keeps zoom independent of editor layout; rows retain source identities.
/// </remarks>
internal sealed class DiffLensControl : Control
{
    private const double CellWidth = 1040;
    private const double HeaderHeight = 68;
    private const double RowHeight = 22;
    private readonly Typeface m_font = new("Cascadia Mono,Consolas,monospace");
    private readonly IBrush m_background = Brush.Parse("#0C1219");
    private readonly IBrush m_card = Brush.Parse("#151E29");
    private readonly IBrush m_selectedCard = Brush.Parse("#29251B");
    private readonly IBrush m_findingMarker = Brush.Parse("#E9BC70");
    private readonly IBrush m_text = Brush.Parse("#D7E3EE");
    private readonly IBrush m_muted = Brush.Parse("#8194A8");
    private readonly IBrush m_added = Brush.Parse("#173C31");
    private readonly IBrush m_removed = Brush.Parse("#44282E");
    private IReadOnlyList<DiffLensCell> m_cells = [];
    private DiffLensFolder m_root;
    private int? m_previousDetail;
    private DiffLensCell m_fadingCell;
    public string SelectedPath { get; private set; }
    private Vector m_offset = new(60, 45);
    private Point? m_drag;
    private Point? m_middleClickOrigin;
    private double m_zoom = 0.6;
    private double m_horizontalScroll;
    private string m_hoverHint;
    private readonly DispatcherTimer m_hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Stopwatch m_fitClock = new();
    private readonly DispatcherTimer m_fitTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double m_fitStartZoom;
    private double m_fitEndZoom;
    private Vector m_fitStartOffset;
    private Vector m_fitEndOffset;
    private readonly Stopwatch m_fadeClock = new();
    private readonly DispatcherTimer m_fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public DiffLensControl()
    {
        ClipToBounds = true;
        Focusable = true;
        // The whole canvas is one control; manage hover as the pointer crosses drawn rows.
        ToolTip.SetServiceEnabled(this, false);
        m_hoverTimer.Tick += (_, _) =>
        {
            m_hoverTimer.Stop();
            if (m_hoverHint != null && m_drag == null && ContextMenu?.IsOpen != true && TopLevel.GetTopLevel(this) != null)
                ToolTip.SetIsOpen(this, true);
        };
        Gestures.AddPointerTouchPadGestureMagnifyHandler(this, (_, e) =>
        {
            // macOS supplies incremental magnification, centered on the pointer.
            HideHoverHint();
            SetZoom(m_zoom * (1 + e.Delta.Y), e.GetPosition(this));
            e.Handled = true;
        });
        m_fadeTimer.Tick += (_, _) =>
        {
            InvalidateVisual();
            if (m_fadeClock.ElapsedMilliseconds >= 180)
            {
                m_fadeTimer.Stop();
                m_previousDetail = null;
            }
        };
        m_fitTimer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(m_fitClock.Elapsed.TotalMilliseconds / 250, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            // Use the same easing and frame for position and scale, including the border.
            m_zoom = m_fitStartZoom + (m_fitEndZoom - m_fitStartZoom) * eased;
            m_offset = m_fitStartOffset + (m_fitEndOffset - m_fitStartOffset) * eased;
            if (progress >= 1)
            {
                m_zoom = m_fitEndZoom;
                m_offset = m_fitEndOffset;
                m_fitTimer.Stop();
            }
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        };
        DetachedFromVisualTree += (_, _) => StopAnimations();
    }

    public void StopAnimations()
    {
        HideHoverHint();
        m_fitTimer.Stop();
        m_fadeTimer.Stop();
    }

    public event EventHandler ViewChanged;
    public event EventHandler<(string Path, int Line)> EditRequested;
    public double Zoom => m_zoom;
    public Func<CodeSmellFinding, int> FindingLineResolver { get; set; }

    private int FindingLine(CodeSmellFinding finding) => FindingLineResolver?.Invoke(finding) ?? finding.LineNumber;

    public void SetFiles(string rootName, IReadOnlyList<DiffLensCell> cells)
    {
        var modes = m_cells.ToDictionary(cell => cell.Entry.Path, cell => cell.Detail);
        foreach (var cell in cells)
            if (modes.TryGetValue(cell.Entry.Path, out var detail))
                cell.Detail = detail;
        m_previousDetail = null;
        m_cells = cells;
        m_root = DiffLensLayout.Build(rootName, cells);
        SelectedPath = cells.FirstOrDefault(cell => cell.Entry.File?.CanEdit == true)?.Entry.Path;
        Fit(false);
    }

    public void SetFile(string path, string baseline, string working, bool resetCamera = true)
    {
        var cell = m_cells.FirstOrDefault(item => item.Entry.Path == path);
        if (cell == null)
            return;
        m_fitTimer.Stop();
        var position = cell.Position;
        cell.Update(baseline, working);
        m_root = DiffLensLayout.Build(m_root.Name, m_cells);
        m_offset += (position - cell.Position) * m_zoom;
        m_previousDetail = null;
        if (resetCamera)
            Fit();
        InvalidateVisual();
    }

    public void Fit(bool animate = true)
    {
        if (m_root != null)
            FitBounds(m_root, animate);
    }

    private void FitBounds(DiffLensFolder folder, bool animate = true)
    {
        HideHoverHint();
        var bounds = folder.Bounds;
        var zoom = Math.Clamp(Math.Min((Bounds.Width - 60) / bounds.Width, (Bounds.Height - 60) / bounds.Height), 0.001, 0.72);
        var offset = new Vector((Bounds.Width - bounds.Width * zoom) / 2 - bounds.X * zoom,
            (Bounds.Height - bounds.Height * zoom) / 2 - bounds.Y * zoom);
        m_fitTimer.Stop();
        m_previousDetail = null;
        m_horizontalScroll = 0;
        if (animate)
        {
            m_fitStartZoom = m_zoom;
            m_fitEndZoom = zoom;
            m_fitStartOffset = m_offset;
            m_fitEndOffset = offset;
            m_fitClock.Restart();
            m_fitTimer.Start();
        }
        else
        {
            m_zoom = zoom;
            m_offset = offset;
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        InvalidateVisual();
    }

    public void ZoomBy(double factor) => SetZoom(m_zoom * factor, new Point(Bounds.Width / 2, Bounds.Height / 2));

    private DiffLensCell HitCell(Point world) => m_cells.FirstOrDefault(cell =>
        new Rect(cell.Position, new Size(CellWidth, cell.Height)).Contains(world));

    private void SetZoom(double zoom, Point pointer)
    {
        HideHoverHint();
        m_fitTimer.Stop();
        zoom = Math.Clamp(zoom, 0.001, 2.5);
        var local = (pointer - m_offset) / m_zoom;
        m_zoom = zoom;
        m_offset = pointer - local * zoom;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetMode(DiffLensCell cell, int detail)
    {
        HideHoverHint();
        m_fitTimer.Stop();
        if (cell.Detail == detail)
            return;
        var position = cell.Position;
        m_fadingCell = cell;
        m_previousDetail = cell.Detail;
        cell.Detail = detail;
        m_root = DiffLensLayout.Build(m_root.Name, m_cells);
        // Keep the clicked file header anchored while its contents and neighbors resize.
        m_offset += (position - cell.Position) * m_zoom;
        m_fadeClock.Restart();
        m_fadeTimer.Start();
        InvalidateVisual();
    }

    private Rect ModeButton(DiffLensCell cell, bool split)
    {
        var size = Math.Min(26, 32 * m_zoom);
        var gap = 6 * m_zoom;
        var right = m_offset.X + cell.Bounds.Right * m_zoom - gap;
        return new Rect(right - size - (split ? 0 : size + gap),
            m_offset.Y + cell.Position.Y * m_zoom + gap, size, size);
    }

    public bool FocusFinding(string path, int line)
    {
        HideHoverHint();
        var cell = m_cells.FirstOrDefault(item => string.Equals(item.Entry.Path, path, StringComparison.OrdinalIgnoreCase));
        if (cell == null)
            return false;
        m_fitTimer.Stop();
        SelectedPath = cell.Entry.Path;
        var row = Math.Max(0, cell.FindingRow(line, cell.Detail));
        m_zoom = Math.Clamp((Bounds.Width - 60) / CellWidth, 0.45, 1.2);
        m_offset = new Vector((Bounds.Width - CellWidth * m_zoom) / 2 - cell.Position.X * m_zoom,
            Bounds.Height / 2 - (cell.Position.Y + HeaderHeight + row * RowHeight) * m_zoom);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public IReadOnlyList<CodeSmellFinding> SelectedFindings =>
        m_cells.FirstOrDefault(cell => cell.Entry.Path == SelectedPath)?.Findings ?? [];

    public IReadOnlyList<CodeSmellFinding> FindingsAt(Point point)
    {
        foreach (var cell in m_cells.Reverse())
        {
            var left = m_offset.X + cell.Position.X * m_zoom;
            var top = m_offset.Y + (cell.Position.Y + HeaderHeight) * m_zoom;
            var margin = point.X >= left - 8 && point.X <= left + Math.Max(18, 8 * m_zoom);
            if (!margin && !cell.Bounds.Contains((point - m_offset) / m_zoom))
                continue;
            if (margin)
            {
                var closest = cell.Findings.Select(finding => (Finding: finding, Row: cell.FindingRow(FindingLine(finding), cell.Detail)))
                    .Where(item => item.Row >= 0)
                    .Select(item => (item.Finding, item.Row, Distance: Math.Abs(point.Y - (top + (item.Row + 0.5) * RowHeight * m_zoom))))
                    .Where(item => item.Distance <= Math.Max(7, RowHeight * m_zoom / 2))
                    .OrderBy(item => item.Distance).ToArray();
                if (closest.Length > 0)
                    return closest.Where(item => item.Row == closest[0].Row).Select(item => item.Finding).ToArray();
            }
            else
            {
                var row = (int)Math.Floor((point.Y - top) / (RowHeight * m_zoom));
                var findings = cell.Findings.Where(finding => cell.FindingRow(FindingLine(finding), cell.Detail) == row && row >= 0).ToArray();
                if (findings.Length > 0)
                    return findings;
            }
        }
        return [];
    }

    private void DrawFindings(DrawingContext context, DiffLensCell cell, Vector offset)
    {
        if (cell.Findings.Count == 0)
            return;
        var marker = m_findingMarker;
        using (context.PushTransform(Matrix.CreateScale(m_zoom, m_zoom) * Matrix.CreateTranslation(offset)))
        {
            DrawText(context, $"! {cell.Findings.Count}", 12, 39, marker, 15);
            foreach (var row in cell.Findings.Select(finding => cell.FindingRow(FindingLine(finding), cell.Detail)).Where(row => row >= 0).Distinct())
                context.FillRectangle(marker, new Rect(1, HeaderHeight + row * RowHeight, 5, RowHeight));
        }
    }

    private void DrawModeButtons(DrawingContext context, DiffLensCell cell)
    {
        foreach (var split in new[] { false, true })
        {
            var bounds = ModeButton(cell, split);
            var selected = (cell.Detail >= 3) == split;
            context.FillRectangle(Brush.Parse(selected ? "#28544C" : "#202E3D"), bounds, 2);
            var icon = bounds.Deflate(bounds.Width * 0.24);
            var pen = new Pen(selected ? Brush.Parse("#66DCC1") : m_muted, Math.Min(1.3, m_zoom * 2));
            context.DrawRectangle(pen, icon);
            if (split)
                context.DrawLine(pen, new Point(icon.Center.X, icon.Top), new Point(icon.Center.X, icon.Bottom));
            else
            {
                context.DrawLine(pen, new Point(icon.Left, icon.Top + icon.Height / 3), new Point(icon.Right, icon.Top + icon.Height / 3));
                context.DrawLine(pen, new Point(icon.Left, icon.Top + icon.Height * 2 / 3), new Point(icon.Right, icon.Top + icon.Height * 2 / 3));
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(m_background, new Rect(Bounds.Size));
        for (double x = m_offset.X % 32; x < Bounds.Width; x += 32)
            for (double y = m_offset.Y % 32; y < Bounds.Height; y += 32)
                context.FillRectangle(Brush.Parse("#24303E"), new Rect(x, y, 1, 1));
        // Retain an opaque outgoing layer beneath the incoming layer. Fading both
        // independently would dim their combined alpha and expose the background.
        if (m_root == null)
            return;
        foreach (var folder in m_root.Descendants)
        {
            var rect = new Rect(folder.Bounds.Position * m_zoom + m_offset, folder.Bounds.Size * m_zoom);
            context.DrawRectangle(new Pen(Brush.Parse("#456076"), 1), rect, 8);
            var label = FolderLabel(folder);
            using (context.PushClip(label))
                DrawText(context, $"{folder.Name}  ·  {folder.FileCount} files", label.X + 6, label.Y + 3, m_muted, Math.Min(14, 38 * m_zoom));
        }
        var progress = m_previousDetail == null ? 1 : Math.Clamp(m_fadeClock.Elapsed.TotalMilliseconds / 180, 0, 1);
        foreach (var cell in m_cells)
        {
            var offset = m_offset + (Vector)cell.Position * m_zoom;
            if (!new Rect(cell.Position * m_zoom + m_offset, cell.Bounds.Size * m_zoom).Intersects(new Rect(Bounds.Size)))
                continue;
            // One opaque card and one geometry for content, border and hit testing.
            // Only the row content fades; outgoing content cannot leave a ghost card.
            using (context.PushTransform(Matrix.CreateScale(m_zoom, m_zoom) * Matrix.CreateTranslation(offset)))
                context.FillRectangle(cell.Entry.Path == SelectedPath ? m_selectedCard : m_card, new Rect(0, 0, CellWidth, cell.Height), 8);
            using (context.PushClip(new Rect(cell.Position * m_zoom + m_offset,
                       new Size(CellWidth * m_zoom, cell.Height * m_zoom))))
            {
                if (cell == m_fadingCell && m_previousDetail is { } previous && progress < 1)
                    DrawLayer(context, cell.Rows(previous), previous, offset, 1);
                DrawLayer(context, cell.Rows(cell.Detail), cell.Detail, offset, cell == m_fadingCell ? progress : 1);
            }
            context.DrawRectangle(null, new Pen(Brush.Parse(cell.Entry.Path == SelectedPath ? "#E9BC70" : "#36546A"),
                cell.Entry.Path == SelectedPath ? 1.5 : 1),
                new Rect(cell.Position * m_zoom + m_offset, new Size(CellWidth * m_zoom, cell.Height * m_zoom)), 8 * m_zoom, 8 * m_zoom);
            DrawTitle(context, cell, offset);
            DrawModeButtons(context, cell);
            DrawFindings(context, cell, offset);
            if (cell.Entry.Error != null)
            {
                using (context.PushClip(new Rect(cell.Position * m_zoom + m_offset, cell.Bounds.Size * m_zoom)))
                    DrawText(context, "Preview unavailable: " + cell.Entry.Error, offset.X + 12 * m_zoom, offset.Y + HeaderHeight * m_zoom, m_muted, 14 * m_zoom);
            }

        }
    }

    private Rect FolderLabel(DiffLensFolder folder) => new(folder.Bounds.Position * m_zoom + m_offset,
        new Size(folder.Bounds.Width * m_zoom, DiffLensLayout.FolderHeader * m_zoom));

    private void DrawLayer(DrawingContext context, IReadOnlyList<DiffLensRow> rows, int detail, Vector offset, double opacity)
    {
        using (context.PushOpacity(opacity))
        using (context.PushTransform(Matrix.CreateScale(m_zoom, m_zoom) * Matrix.CreateTranslation(offset)))
        {
            var height = Math.Max(160, HeaderHeight + rows.Count * RowHeight + 24);
            context.FillRectangle(m_card, new Rect(0, HeaderHeight, CellWidth, height - HeaderHeight));
            using (context.PushClip(new Rect(0, HeaderHeight, CellWidth, height - HeaderHeight)))
            {
                var first = Math.Max(0, (int)((-offset.Y / m_zoom - HeaderHeight) / RowHeight));
                var last = Math.Min(rows.Count, (int)((Bounds.Height - offset.Y) / m_zoom / RowHeight) + 2);
                for (var i = first; i < last; i++)
                {
                    var row = rows[i];
                    var y = HeaderHeight + i * RowHeight;
                    if (row.HiddenLines > 0)
                    {
                        DrawText(context, $"⋯  {row.HiddenLines} unchanged lines", 18, y + 2, m_muted);
                        continue;
                    }
                    if (detail >= 3)
                    {
                        DrawPiece(context, row.Left, 0, y, CellWidth / 2, true, row.Changed);
                        DrawPiece(context, row.Right, CellWidth / 2, y, CellWidth / 2, false, row.Changed);
                    }
                    else
                        DrawPiece(context, row.Right ?? row.Left, 0, y, CellWidth, row.Right == null, row.Changed);
                }
            }
            if (detail >= 3)
                context.DrawLine(new Pen(Brush.Parse("#36546A")), new Point(CellWidth / 2, 38), new Point(CellWidth / 2, height));
        }
    }

    private void DrawTitle(DrawingContext context, DiffLensCell cell, Vector offset)
    {
        // Keep the title inside the projected card, including at overview zoom.
        var titleSize = Math.Min(14, 36 * m_zoom);
        var titleBounds = new Rect(offset.X + 2, offset.Y + 1, Math.Max(1, ModeButton(cell, false).Left - offset.X - 6), Math.Max(1, HeaderHeight * m_zoom - 2));
        using (context.PushClip(titleBounds))
        {
            var title = new FormattedText(System.IO.Path.GetFileName(cell.Entry.Path), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, m_font, titleSize, m_text)
            {
                MaxTextWidth = Math.Max(1, titleBounds.Width - 12),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            context.DrawText(title, new Point(titleBounds.X + 6, titleBounds.Y + Math.Min(6, m_zoom * 6)));
        }
        if (cell.Detail >= 3 && m_zoom >= 0.45)
        {
            DrawText(context, "BRANCH BASE", offset.X + (cell.Findings.Count > 0 ? 90 : 12) * m_zoom, offset.Y + 40 * m_zoom, m_muted, 11);
            DrawText(context, "WORKING FILE", offset.X + CellWidth * m_zoom / 2 + 12, offset.Y + 40 * m_zoom, Brush.Parse("#66DCC1"), 11);
        }
    }

    private void DrawPiece(DrawingContext context, DiffPiece piece, double x, double y, double width, bool oldSide, bool changed)
    {
        if (piece == null || piece.Type == ChangeType.Imaginary)
            return;
        using (context.PushClip(new Rect(x, y, width, RowHeight)))
        {
            if (changed)
                context.FillRectangle(oldSide ? m_removed : m_added, new Rect(x, y, width, RowHeight));
            var marker = changed ? oldSide ? "−" : "+" : " ";
            DrawText(context, $"{piece.Position,4} {marker}", x + 8, y + 2, m_muted, 12);
            using (context.PushClip(new Rect(x + 68, y, width - 68, RowHeight)))
                DrawText(context, piece.Text.Replace("\t", "    "), x + 70 - m_horizontalScroll, y + 2, m_text);
        }
    }

    private void DrawText(DrawingContext context, string text, double x, double y, IBrush brush, double size = 14) =>
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, m_font, size, brush), new Point(x, y));

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        HideHoverHint();
        m_fitTimer.Stop();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            (OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            SetZoom(m_zoom * Math.Pow(1.15, e.Delta.Y), e.GetPosition(this));
        else
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                m_horizontalScroll = Math.Max(0, m_horizontalScroll - (e.Delta.X != 0 ? e.Delta.X : e.Delta.Y) * 45);
            else
                m_offset += new Vector(e.Delta.X * 45, e.Delta.Y * 45);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        HideHoverHint();
        base.OnPointerPressed(e);
        m_fitTimer.Stop();
        Focus();
        var local = (e.GetPosition(this) - m_offset) / m_zoom;
        var cell = HitCell(local);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (cell != null)
            {
                SelectedPath = cell.Entry.Path;
                ViewChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
                var point = e.GetPosition(this);
                if (ModeButton(cell, false).Contains(point) || ModeButton(cell, true).Contains(point))
                {
                    m_drag = null;
                    e.Pointer.Capture(null);
                    SetMode(cell, ModeButton(cell, true).Contains(point) ? 3 : 0);
                    e.Handled = true;
                    return;
                }
                if (e.ClickCount == 2)
                {
                    m_drag = null;
                    e.Pointer.Capture(null);
                    var rowIndex = (int)Math.Floor((local.Y - cell.Position.Y - HeaderHeight) / RowHeight);
                    EditRequested?.Invoke(this, (cell.Entry.Path, DiffLensRows.FindWorkingLine(cell.Rows(cell.Detail), rowIndex)));
                    e.Handled = true;
                    return;
                }
            }
            else
            {
                var folder = m_root?.Descendants.Reverse().FirstOrDefault(item => FolderLabel(item).Contains(e.GetPosition(this)));
                if (folder != null)
                {
                    FitBounds(folder);
                    e.Handled = true;
                    return;
                }
            }
        }
        if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            m_drag = e.GetPosition(this);
            m_middleClickOrigin = e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed ? m_drag : null;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (m_drag is not { } start)
        {
            var hoverPoint = e.GetPosition(this);
            var cell = HitCell((hoverPoint - m_offset) / m_zoom);
            var hint = cell == null ? null : ModeButton(cell, false).Contains(hoverPoint) ? "Unified diff (U)" :
                ModeButton(cell, true).Contains(hoverPoint) ? "Side-by-side diff (S)" : null;
            if (hint == null)
            {
                var findings = FindingsAt(hoverPoint);
                if (findings.Count > 0)
                    hint = string.Join("\n", findings.Select(finding => $"{finding.Severity} · {finding.RuleId}: {finding.Message}")) +
                           "\nRight-click for issue actions";
            }
            // Keep an unchanged tip alive while the pointer moves inside a marker's hit area.
            if (hint != m_hoverHint)
            {
                HideHoverHint();
                m_hoverHint = hint;
                ToolTip.SetTip(this, hint);
                if (hint != null)
                    m_hoverTimer.Start();
            }
            return;
        }
        var point = e.GetPosition(this);
        if (m_middleClickOrigin is { } origin)
        {
            // Ignore small hand movements when clicking the wheel.
            if (((Vector)(point - origin)).Length <= 4)
                return;
            m_middleClickOrigin = null;
        }
        m_offset += point - start;
        m_drag = point;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.U or Key.S))
            return;
        var cell = m_cells.FirstOrDefault(item => item.Entry.Path == SelectedPath);
        if (cell == null)
            return;
        SetMode(cell, e.Key == Key.S ? 3 : 0);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var fit = e.InitialPressMouseButton == MouseButton.Middle &&
                  m_middleClickOrigin is { } origin && ((Vector)(e.GetPosition(this) - origin)).Length <= 4;
        m_drag = null;
        m_middleClickOrigin = null;
        e.Pointer.Capture(null);
        if (fit)
        {
            Fit();
            e.Handled = true;
        }
        // Control raises ContextRequested here for a right-button release.
        base.OnPointerReleased(e);
    }

    private void HideHoverHint()
    {
        m_hoverTimer.Stop();
        m_hoverHint = null;
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        HideHoverHint();
        base.OnPointerExited(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        m_drag = null;
        m_middleClickOrigin = null;
    }
}
