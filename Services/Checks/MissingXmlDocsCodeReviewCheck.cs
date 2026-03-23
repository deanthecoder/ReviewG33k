// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Services.Checks;

public sealed class MissingXmlDocsCodeReviewCheck : CodeReviewCheckBase
{
    public override string RuleId => "missing-xml-docs";

    public override string DisplayName => "XML docs on new public/internal types";

    public override CodeReviewCheckScope Scope => CodeReviewCheckScope.AddedLinesOnly;

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files.Where(file => file.IsAdded))
        {
            if (CodeReviewFileClassification.IsLikelyTestCodeFile(file))
                continue;

            var root = RoslynCodeReviewCheckUtilities.ParseRoot(file);
            var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>();
            foreach (var type in types)
            {
                if (!IsDocumentableType(type))
                    continue;

                var declarationLineNumber = RoslynCodeReviewCheckUtilities.GetStartLine(type);
                if (HasXmlDocumentation(type))
                    continue;

                AddFinding(report, CodeReviewFindingSeverity.Hint, file.Path, declarationLineNumber, "Missing XML docs on new public/internal type.");
            }
        }
    }

    private static bool IsDocumentableType(TypeDeclarationSyntax type)
    {
        if (type == null)
            return false;

        return type.Modifiers.Any(modifier =>
            modifier.IsKind(SyntaxKind.PublicKeyword) ||
            modifier.IsKind(SyntaxKind.InternalKeyword));
    }

    private static bool HasXmlDocumentation(TypeDeclarationSyntax type) =>
        type != null &&
        type.GetLeadingTrivia().Any(trivia =>
            trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
}
