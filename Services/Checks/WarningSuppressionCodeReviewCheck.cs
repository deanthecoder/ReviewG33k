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

using ReviewG33k.Services;

namespace ReviewG33k.Services.Checks;

public sealed class WarningSuppressionCodeReviewCheck : CodeReviewCheckBase
{
    public override string RuleId => CodeReviewRuleIds.WarningSuppression;

    public override string DisplayName => "Warning suppressions (`#pragma` / `[SuppressMessage]`)";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            var root = RoslynCodeReviewCheckUtilities.ParseRoot(file);

            var pragmaDirectives = root
                .DescendantTrivia(descendIntoTrivia: true)
                .Select(trivia => trivia.GetStructure())
                .OfType<PragmaWarningDirectiveTriviaSyntax>();
            foreach (var directive in pragmaDirectives)
            {
                if (!IsDisablePragma(directive))
                    continue;

                var lineNumber = directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (!file.IsAdded && !file.AddedLineNumbers.Contains(lineNumber))
                    continue;

                AddFinding(
                    report,
                    CodeReviewFindingSeverity.Important,
                    file.Path,
                    lineNumber,
                    "Warning suppression via `#pragma warning disable` added. Prefer fixing the warning or documenting why suppression is required.");
            }

            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
            {
                if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, attribute.Span))
                    continue;
                if (!IsSuppressMessageAttribute(attribute))
                    continue;

                var lineNumber = RoslynCodeReviewCheckUtilities.GetStartLine(attribute);
                AddFinding(
                    report,
                    CodeReviewFindingSeverity.Important,
                    file.Path,
                    lineNumber,
                    "Warning suppression via `[SuppressMessage]` added. Prefer fixing the warning or documenting why suppression is required.");
            }
        }
    }

    private static bool IsDisablePragma(PragmaWarningDirectiveTriviaSyntax directive) =>
        directive != null &&
        string.Equals(directive.DisableOrRestoreKeyword.ValueText, "disable", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuppressMessageAttribute(AttributeSyntax attribute)
    {
        var name = GetAttributeName(attribute?.Name);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return string.Equals(name, "SuppressMessage", StringComparison.Ordinal) ||
               string.Equals(name, "SuppressMessageAttribute", StringComparison.Ordinal);
    }

    private static string GetAttributeName(NameSyntax nameSyntax)
    {
        return nameSyntax switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => GetAttributeName(qualified.Right),
            AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => nameSyntax?.ToString()
        };
    }
}
