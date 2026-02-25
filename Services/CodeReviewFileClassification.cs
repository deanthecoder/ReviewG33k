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
using System.IO;
using System.Text.RegularExpressions;

namespace ReviewG33k.Services;

internal static class CodeReviewFileClassification
{
    private static readonly Regex UiUsingRegex = new(
        @"^\s*using\s+(?<ns>(Avalonia\.(Controls|VisualTree)|System\.Windows(?:\.(Controls|Data|Documents|Forms|Input|Interop|Markup|Media|Navigation|Shapes|Threading))?|Windows\.UI\.Xaml(?:\.(Controls|Data|Documents|Input|Interop|Markup|Media|Navigation|Shapes))?))\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static bool IsAnalyzableChangedCSharpPath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".axaml.cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase);

    public static bool IsTestFilePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("/Tests/", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("\\Tests\\", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("/UnitTests/", StringComparison.OrdinalIgnoreCase) ||
         path.Contains("\\UnitTests\\", StringComparison.OrdinalIgnoreCase));

    public static bool IsGeneratedFilePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase));

    public static bool IsLikelyInterfaceFilePath(string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.Length < 4 ||
            fileName[0] != 'I')
        {
            return false;
        }

        return char.IsUpper(fileName[1]);
    }

    public static bool IsLikelyUiCodeFile(CodeReviewChangedFile file) =>
        file != null &&
        !string.IsNullOrWhiteSpace(file.Text) &&
        UiUsingRegex.IsMatch(file.Text);
}
