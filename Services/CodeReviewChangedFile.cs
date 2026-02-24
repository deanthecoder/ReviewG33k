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

namespace ReviewG33k.Services;

public sealed class CodeReviewChangedFile
{
    public CodeReviewChangedFile(string status, string path, string fullPath, string text, IReadOnlyList<string> lines, IReadOnlySet<int> addedLineNumbers)
    {
        Status = status ?? string.Empty;
        Path = path ?? string.Empty;
        FullPath = fullPath ?? string.Empty;
        Text = text ?? string.Empty;
        Lines = lines ?? [];
        AddedLineNumbers = addedLineNumbers ?? new HashSet<int>();
    }

    public string Status { get; }

    public string Path { get; }

    public string FullPath { get; }

    public string Text { get; }

    public IReadOnlyList<string> Lines { get; }

    public IReadOnlySet<int> AddedLineNumbers { get; }

    public bool IsAdded => Status.Equals("A", StringComparison.OrdinalIgnoreCase);
}