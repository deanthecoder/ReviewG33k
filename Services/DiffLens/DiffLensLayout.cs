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
using System.Linq;
using Avalonia;

namespace ReviewG33k.Services.DiffLens;

/// <summary>
/// Caches a file's detail levels and current canvas footprint.
/// </summary>
/// <remarks>
/// The footprint matches the visible detail so compact views leave no reserved gaps.
/// </remarks>
internal sealed class DiffLensCell
{
    public const double Width = 1040;
    public const double HeaderHeight = 68;
    public const double RowHeight = 22;
    public DiffLensEntry Entry { get; }
    public Point Position { get; set; }
    public IReadOnlyList<DiffLensRow>[] Levels { get; private set; }
    public int Detail { get; set; }
    public IReadOnlyList<CodeSmellFinding> Findings { get; set; } = [];
    public double Height => DisplayHeight(Detail);
    public Rect Bounds => new(Position, new Size(Width, Height));
    public IReadOnlyList<DiffLensRow> Rows(int detail) => Levels[detail];
    public double DisplayHeight(int detail) => Math.Max(160, HeaderHeight + Rows(detail).Count * RowHeight + 24);

    public DiffLensCell(DiffLensEntry entry)
    {
        Entry = entry;
        Update(entry.File?.Baseline ?? string.Empty, entry.File?.WorkingText ?? string.Empty);
    }

    public int FindingRow(int line, int detail)
    {
        var rows = Rows(detail);
        var lastLine = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].NewLine is { } current)
            {
                if (current == line)
                    return i;
                lastLine = current;
            }
            else if (rows[i].HiddenLines > 0)
            {
                if (line > lastLine && line <= lastLine + rows[i].HiddenLines)
                    return i;
                lastLine += rows[i].HiddenLines;
            }
        }
        return -1;
    }

    public void Update(string baseline, string working)
    {
        var unified = DiffLensRows.Build(baseline, working, 4, false);
        Levels = [unified, unified, unified, DiffLensRows.Build(baseline, working, 4, true)];
    }
}

/// <summary>
/// Represents a folder frame and its contained changed files and folders.
/// </summary>
/// <remarks>
/// Folder bounds enclose the visible footprint of every descendant.
/// </remarks>
internal sealed class DiffLensFolder(string name)
{
    public string Name { get; set; } = name;
    public List<DiffLensFolder> Folders { get; } = [];
    public List<DiffLensCell> Files { get; } = [];
    public Rect Bounds { get; set; }
    public int FileCount => Files.Count + Folders.Sum(folder => folder.FileCount);
    public IEnumerable<DiffLensFolder> Descendants => new[] { this }.Concat(Folders.SelectMany(folder => folder.Descendants));
}

/// <summary>
/// Packs changed files into nested folder frames rooted at the repository.
/// </summary>
/// <remarks>
/// Empty ancestry is compacted into path labels; children retain nonoverlapping natural heights.
/// </remarks>
internal static class DiffLensLayout
{
    public const double Padding = 40;
    public const double FolderHeader = 80;

    public static DiffLensFolder Build(string rootName, IReadOnlyList<DiffLensCell> cells, int? detail = null)
    {
        var root = new DiffLensFolder(rootName);
        foreach (var cell in cells.OrderBy(cell => cell.Entry.Path, StringComparer.Ordinal))
        {
            if (detail.HasValue)
                cell.Detail = detail.Value;
            var folder = root;
            var parts = cell.Entry.Path.Replace('\\', '/').Split('/');
            foreach (var segment in parts.SkipLast(1))
            {
                var child = folder.Folders.FirstOrDefault(item => item.Name == segment);
                if (child == null)
                {
                    child = new DiffLensFolder(segment);
                    folder.Folders.Add(child);
                }
                folder = child;
            }
            folder.Files.Add(cell);
        }
        foreach (var child in root.Folders)
            Compact(child);
        Measure(root);
        Position(root, new Point(0, 0));
        return root;
    }

    private static void Compact(DiffLensFolder folder)
    {
        while (folder.Files.Count == 0 && folder.Folders.Count == 1)
        {
            var child = folder.Folders[0];
            folder.Name += " / " + child.Name;
            folder.Files.AddRange(child.Files);
            folder.Folders.Clear();
            folder.Folders.AddRange(child.Folders);
        }
        foreach (var child in folder.Folders)
            Compact(child);
    }

    private static void Measure(DiffLensFolder folder)
    {
        foreach (var child in folder.Folders)
            Measure(child);
        var sizes = folder.Files.Select(cell => new Size(DiffLensCell.Width, cell.Height))
            .Concat(folder.Folders.Select(child => child.Bounds.Size)).ToArray();
        var targetWidth = sizes.Length == 0 ? DiffLensCell.Width : Math.Max(sizes.Max(size => size.Width), Math.Sqrt(sizes.Sum(size => (size.Width + Padding) * (size.Height + Padding))) * 1.4);
        // Estimate the column count, then assign children before determining column widths.
        // Each column reserves only the width of its own widest child, including frames.
        var columnWidth = sizes.Length == 0 ? DiffLensCell.Width : sizes.Max(size => size.Width);
        var columnCount = Math.Clamp((int)Math.Round((targetWidth + Padding) / (columnWidth + Padding)), 1, Math.Max(1, sizes.Length));
        var bottoms = Enumerable.Repeat(FolderHeader, columnCount).ToArray();
        var widths = new double[columnCount];
        var columns = new int[sizes.Length];
        var tops = new double[sizes.Length];
        foreach (var (size, index) in sizes.Select((size, index) => (size, index)))
        {
            var column = Array.IndexOf(bottoms, bottoms.Min());
            columns[index] = column;
            tops[index] = bottoms[column];
            widths[column] = Math.Max(widths[column], size.Width);
            bottoms[column] += size.Height + Padding;
        }
        var lefts = new double[columnCount];
        var nextLeft = Padding;
        for (var column = 0; column < columnCount; column++)
        {
            lefts[column] = nextLeft;
            nextLeft += widths[column] + Padding;
        }
        var placed = new List<Rect>();
        foreach (var (size, index) in sizes.Select((size, index) => (size, index)))
        {
            var best = new Point(lefts[columns[index]], tops[index]);
            var bounds = new Rect(best, size);
            placed.Add(bounds);
            if (index < folder.Files.Count)
                folder.Files[index].Position = best;
            else
                folder.Folders[index - folder.Files.Count].Bounds = bounds;
        }
        folder.Bounds = new Rect(0, 0, placed.Count == 0 ? DiffLensCell.Width + Padding * 2 : placed.Max(rect => rect.Right) + Padding,
            placed.Count == 0 ? FolderHeader + Padding : placed.Max(rect => rect.Bottom) + Padding);
    }

    private static void Position(DiffLensFolder folder, Point origin)
    {
        folder.Bounds = new Rect(origin, folder.Bounds.Size);
        foreach (var file in folder.Files)
            file.Position += (Vector)origin;
        foreach (var child in folder.Folders)
            Position(child, child.Bounds.Position + (Vector)origin);
    }
}
