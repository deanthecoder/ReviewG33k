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
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using ReviewG33k.Services;

namespace ReviewG33k.Services.Checks;

public sealed class MissingTestsForNewPublicMethodsCodeReviewCheck : CodeReviewCheckBase
{
    public override string RuleId => CodeReviewRuleIds.MissingTestsForPublicMethods;

    public override string DisplayName => "new public methods have test changes";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        var changedTestFiles = context.Files
            .Where(file => CodeReviewFileClassification.IsTestFilePath(file.Path))
            .ToArray();
        var changedTestFileNames = new HashSet<string>(
            changedTestFiles.Select(file => Path.GetFileName(file.Path)),
            StringComparer.OrdinalIgnoreCase);
        var hasAnyChangedTests = changedTestFiles.Length > 0;

        foreach (var file in context.Files)
        {
            if (CodeReviewFileClassification.IsTestFilePath(file.Path) ||
                CodeReviewFileClassification.IsGeneratedFilePath(file.Path) ||
                CodeReviewFileClassification.IsLikelyUiCodeFile(file))
                continue;

            var root = RoslynCodeReviewCheckUtilities.ParseRoot(file);
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!RoslynCodeReviewCheckUtilities.IsNodeNew(file, method))
                    continue;
                if (!IsEligiblePublicMethod(method))
                    continue;

                var containingType = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (containingType == null || containingType is InterfaceDeclarationSyntax)
                    continue;

                var typeName = containingType.Identifier.ValueText;
                if (string.IsNullOrWhiteSpace(typeName))
                    continue;

                if (HasLikelyMatchingTestFile(changedTestFileNames, typeName))
                    continue;

                var lineNumber = RoslynCodeReviewCheckUtilities.GetStartLine(method);
                var methodName = method.Identifier.ValueText;
                var expectedTestFileName = $"{typeName}Tests.cs";
                var severity = hasAnyChangedTests
                    ? CodeReviewFindingSeverity.Hint
                    : CodeReviewFindingSeverity.Suggestion;

                if (hasAnyChangedTests)
                {
                    AddFinding(
                        report,
                        severity,
                        file.Path,
                        lineNumber,
                        $"New public method '{methodName}' on '{typeName}' has no likely matching changed test file (expected '{expectedTestFileName}').");
                }
                else
                {
                    AddFinding(
                        report,
                        severity,
                        file.Path,
                        lineNumber,
                        $"New public method '{methodName}' on '{typeName}' has no test changes in this PR (expected '{expectedTestFileName}' or similar).");
                }
            }
        }
    }

    private static bool IsEligiblePublicMethod(MethodDeclarationSyntax method)
    {
        if (method == null)
            return false;
        if (!method.Modifiers.Any(modifier => modifier.RawKind == (int)SyntaxKind.PublicKeyword))
            return false;
        if (method.Modifiers.Any(modifier =>
            modifier.RawKind == (int)SyntaxKind.AbstractKeyword ||
            modifier.RawKind == (int)SyntaxKind.OverrideKeyword ||
            modifier.RawKind == (int)SyntaxKind.ExternKeyword))
        {
            return false;
        }

        return method.Body != null || method.ExpressionBody != null;
    }

    private static bool HasLikelyMatchingTestFile(IReadOnlySet<string> changedTestFileNames, string typeName)
    {
        if (changedTestFileNames == null || changedTestFileNames.Count == 0 || string.IsNullOrWhiteSpace(typeName))
            return false;

        var exactName = $"{typeName}Tests.cs";
        if (changedTestFileNames.Contains(exactName))
            return true;

        return changedTestFileNames.Any(fileName =>
            !string.IsNullOrWhiteSpace(fileName) &&
            fileName.Contains(typeName, StringComparison.OrdinalIgnoreCase) &&
            fileName.Contains("Test", StringComparison.OrdinalIgnoreCase));
    }

}
