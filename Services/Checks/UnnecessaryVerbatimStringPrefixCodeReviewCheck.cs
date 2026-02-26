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
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReviewG33k.Services.Checks;

public sealed class UnnecessaryVerbatimStringPrefixCodeReviewCheck : RoslynSemanticCodeReviewCheckBase
{
    public override string RuleId => CodeReviewRuleIds.UnnecessaryVerbatimStringPrefix;

    public override string DisplayName => "Unnecessary verbatim string prefix";

    protected override void AnalyzeFile(
        CodeReviewAnalysisContext context,
        CodeReviewChangedFile file,
        CompilationUnitSyntax root,
        SemanticModel semanticModel,
        CodeSmellReport report)
    {
        AnalyzeVerbatimStringLiterals(file, root, report);
        AnalyzeVerbatimInterpolatedStrings(file, root, report);
    }

    private void AnalyzeVerbatimStringLiterals(CodeReviewChangedFile file, CompilationUnitSyntax root, CodeSmellReport report)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, literal.Span))
                continue;

            var token = literal.Token;
            if (!token.Text.StartsWith("@\"", StringComparison.Ordinal))
                continue;
            if (!CanBeRegularString(token.ValueText))
                continue;

            AddFinding(
                report,
                CodeReviewFindingSeverity.Hint,
                file.Path,
                RoslynCodeReviewCheckUtilities.GetStartLine(literal),
                "Unnecessary verbatim string prefix `@`.");
        }
    }

    private void AnalyzeVerbatimInterpolatedStrings(CodeReviewChangedFile file, CompilationUnitSyntax root, CodeSmellReport report)
    {
        foreach (var interpolated in root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
        {
            if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, interpolated.Span))
                continue;
            if (!interpolated.StringStartToken.Text.Contains('@', StringComparison.Ordinal))
                continue;
            if (!interpolated.Contents.OfType<InterpolatedStringTextSyntax>().All(text => CanBeRegularString(text.TextToken.ValueText)))
                continue;

            AddFinding(
                report,
                CodeReviewFindingSeverity.Hint,
                file.Path,
                RoslynCodeReviewCheckUtilities.GetStartLine(interpolated),
                "Unnecessary verbatim string prefix `@`.");
        }
    }

    private static bool CanBeRegularString(string valueText) =>
        valueText != null &&
        valueText.IndexOf('\\') < 0 &&
        valueText.IndexOf('"') < 0 &&
        valueText.IndexOf('\r') < 0 &&
        valueText.IndexOf('\n') < 0;
}
