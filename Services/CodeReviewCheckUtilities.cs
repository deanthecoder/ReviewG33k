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
using System.Text.RegularExpressions;

namespace ReviewG33k.Services;

internal static class CodeReviewCheckUtilities
{
    private static readonly Regex CatchOpenRegex = new(@"\bcatch\b\s*(?:\([^)]*\))?\s*\{", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PublicTypeDeclarationRegex = new(
        @"^\s*public\s+(?:sealed\s+|abstract\s+|partial\s+|static\s+)*\b(class|interface|record|struct)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static bool LooksLikeEventHandlerSignature(string line) =>
        line?.Contains("(object ", StringComparison.OrdinalIgnoreCase) == true &&
        line.Contains("EventArgs", StringComparison.OrdinalIgnoreCase);

    public static bool LooksLikePublicLockObject(IReadOnlyList<string> fileLines, string lockTarget)
    {
        var fieldPattern = $@"\bpublic\s+(?:static\s+)?(?:readonly\s+)?(?:object|System\.Object)\s+{Regex.Escape(lockTarget)}\b";
        var propertyPattern = $@"\bpublic\s+(?:object|System\.Object)\s+{Regex.Escape(lockTarget)}\s*\{{";

        return fileLines.Any(line => Regex.IsMatch((string)line, fieldPattern, RegexOptions.IgnoreCase)) ||
               fileLines.Any(line => Regex.IsMatch(line, propertyPattern, RegexOptions.IgnoreCase));
    }

    public static bool TryGetPublicTypeDeclaration(string fileText, out string typeName, out int lineNumber)
    {
        typeName = null;
        lineNumber = 0;

        var match = PublicTypeDeclarationRegex.Match(fileText ?? string.Empty);
        if (!match.Success)
            return false;

        typeName = match.Groups["name"].Value;
        lineNumber = (fileText ?? string.Empty)[..match.Index].Count(character => character == '\n') + 1;
        return !string.IsNullOrWhiteSpace(typeName);
    }

    public static bool HasXmlDocumentationAbove(IReadOnlyList<string> lines, int declarationLineNumber)
    {
        var index = declarationLineNumber - 2;
        while (index >= 0)
        {
            var trimmed = lines[index].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                index--;
                continue;
            }

            if (trimmed.StartsWith("///", StringComparison.Ordinal))
                return true;

            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                index--;
                continue;
            }

            return false;
        }

        return false;
    }

    public static IEnumerable<CatchBlockInfo> EnumerateAddedCatchBlocks(CodeReviewChangedFile file)
    {
        if (file.AddedLineNumbers.Count == 0 || string.IsNullOrWhiteSpace(file.Text))
            yield break;

        var lineStarts = BuildLineStartOffsets(file.Text);
        foreach (Match catchMatch in CatchOpenRegex.Matches(file.Text))
        {
            var openBraceIndex = file.Text.IndexOf('{', catchMatch.Index);
            if (openBraceIndex < 0)
                continue;

            var closeBraceIndex = FindMatchingBrace(file.Text, openBraceIndex);
            if (closeBraceIndex < 0)
                continue;

            var startLine = GetLineNumberFromOffset(lineStarts, catchMatch.Index);
            var endLine = GetLineNumberFromOffset(lineStarts, closeBraceIndex);
            if (!file.AddedLineNumbers.Any(line => line >= startLine && line <= endLine))
                continue;

            var body = file.Text.Substring(openBraceIndex + 1, closeBraceIndex - openBraceIndex - 1);
            var bodyWithoutComments = StripComments(body).Trim();
            yield return new CatchBlockInfo(startLine, bodyWithoutComments);
        }
    }

    public static bool ContainsRethrow(string catchBody) =>
        catchBody.Contains("throw;", StringComparison.Ordinal) ||
        Regex.IsMatch(catchBody, @"\bthrow\s+[A-Za-z_]", RegexOptions.Compiled);

    public static bool ContainsLoggingCall(string catchBody) =>
        Regex.IsMatch(catchBody, @"\b(Log|Logger|AppendLog|Trace|Debug|Warn|Error|Exception|Console\.Write)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static int[] BuildLineStartOffsets(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }

        return starts.ToArray();
    }

    private static int GetLineNumberFromOffset(IReadOnlyList<int> lineStarts, int offset)
    {
        var low = 0;
        var high = lineStarts.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (lineStarts[mid] <= offset)
                low = mid + 1;
            else
                high = mid - 1;
        }

        return Math.Max(1, high + 1);
    }

    private static int FindMatchingBrace(string text, int openBraceIndex)
    {
        var depth = 0;
        for (var i = openBraceIndex; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static string StripComments(string text)
    {
        var withoutBlockComments = Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var withoutLineComments = Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
        return withoutLineComments;
    }
}