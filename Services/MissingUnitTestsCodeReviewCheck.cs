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
using System.Linq;
using System.Text.RegularExpressions;

namespace ReviewG33k.Services;

public sealed class MissingUnitTestsCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly Regex UiUsingRegex = new(
        @"^\s*using\s+(?<ns>(Avalonia\.(Controls|VisualTree)|System\.Windows(?:\.(Controls|Data|Documents|Forms|Input|Interop|Markup|Media|Navigation|Shapes|Threading))?|Windows\.UI\.Xaml(?:\.(Controls|Data|Documents|Input|Interop|Markup|Media|Navigation|Shapes))?))\s*;",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public override string RuleId => CodeReviewRuleIds.MissingTests;

    public override string DisplayName => "new non-UI type has new test file";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files.Where(file => file.IsAdded))
        {
            if (file.Path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsLikelyInterfaceFilePath(file.Path))
                continue;
            if (IsLikelyUiCodeFile(file))
                continue;

            if (!CodeReviewCheckUtilities.TryGetPublicTypeDeclaration(file.Text, out var typeName, out var declarationLineNumber))
                continue;

            var expectedTestFileName = $"{typeName}Tests.cs";
            if (context.AddedTestFilesByName.Contains(expectedTestFileName))
                continue;

            if (!context.HasAnyAddedTestFiles)
            {
                AddFinding(report, CodeReviewFindingSeverity.Suggestion, file.Path, declarationLineNumber, $"No new unit test added for new type '{typeName}'. Expected '{expectedTestFileName}'.");
                continue;
            }

            AddFinding(report, CodeReviewFindingSeverity.Hint, file.Path, declarationLineNumber, $"No matching test file '{expectedTestFileName}' found for new type '{typeName}'.");
        }
    }

    private static bool IsLikelyInterfaceFilePath(string path)
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

    private static bool IsLikelyUiCodeFile(CodeReviewChangedFile file) =>
        file != null &&
        !string.IsNullOrWhiteSpace(file.Text) &&
        UiUsingRegex.IsMatch(file.Text);
}
