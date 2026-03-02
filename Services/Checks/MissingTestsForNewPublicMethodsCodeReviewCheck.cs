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
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Services.Checks;

public sealed class MissingTestsForNewPublicMethodsCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly Regex TestFixtureAttributeNameRegex = new(
        @"(?:^|\.)(?:TestFixture|TextFixture)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public override string RuleId => "missing-tests-public-methods";

    public override string DisplayName => "new public methods have test changes";

    public override CodeReviewCheckScope Scope => CodeReviewCheckScope.AddedLinesOnly;

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        var changedTestFiles = context.Files
            .Where(CodeReviewFileClassification.IsLikelyTestCodeFile)
            .ToArray();
        var changedTestFileNames = new HashSet<string>(
            changedTestFiles.Select(file => Path.GetFileName(file.Path)),
            StringComparer.OrdinalIgnoreCase);
        var hasAnyChangedTests = changedTestFiles.Length > 0;

        foreach (var file in context.Files)
        {
            if (CodeReviewFileClassification.IsTestFilePath(file.Path) ||
                CodeReviewFileClassification.IsGeneratedFilePath(file.Path) ||
                CodeReviewFileClassification.IsCodeBehindFilePath(file.Path) ||
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
                if (IsLikelyTestFixtureType(containingType))
                    continue;

                var typeName = containingType.Identifier.ValueText;
                if (string.IsNullOrWhiteSpace(typeName))
                    continue;

                if (HasLikelyMatchingTestFile(changedTestFileNames, typeName))
                    continue;

                var lineNumber = RoslynCodeReviewCheckUtilities.GetStartLine(method);
                var methodName = method.Identifier.ValueText;
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
                        $"New public method '{methodName}' has no likely matching unit test(s).");
                }
                else
                {
                    AddFinding(
                        report,
                        severity,
                        file.Path,
                        lineNumber,
                        $"New public method '{methodName}' has no unit test changes in this PR.");
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

    private static bool IsLikelyTestFixtureType(TypeDeclarationSyntax typeDeclaration)
    {
        if (typeDeclaration == null)
            return false;

        foreach (var attributeList in typeDeclaration.AttributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var attributeName = attribute?.Name?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(attributeName))
                    continue;

                if (TestFixtureAttributeNameRegex.IsMatch(attributeName))
                    return true;
            }
        }

        return false;
    }

}
