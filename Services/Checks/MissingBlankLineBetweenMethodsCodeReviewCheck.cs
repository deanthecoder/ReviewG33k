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
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReviewG33k.Services.Checks;

public sealed class MissingBlankLineBetweenMethodsCodeReviewCheck : CodeReviewCheckBase
{
    public override string RuleId => CodeReviewRuleIds.MissingBlankLineBetweenMethods;

    public override string DisplayName => "Blank line between methods";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            var root = RoslynCodeReviewCheckUtilities.ParseRoot(file);
            var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

            foreach (var type in types)
            {
                var methods = type.Members
                    .OfType<MethodDeclarationSyntax>()
                    .OrderBy(method => method.SpanStart)
                    .ToArray();

                for (var index = 0; index < methods.Length - 1; index++)
                {
                    var currentMethod = methods[index];
                    var nextMethod = methods[index + 1];

                    if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, currentMethod.Span) &&
                        !RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, nextMethod.Span))
                    {
                        continue;
                    }

                    var currentEndLine = currentMethod.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
                    var nextStartLine = RoslynCodeReviewCheckUtilities.GetStartLine(nextMethod);
                    if (nextStartLine > currentEndLine + 1)
                        continue;

                    AddFinding(
                        report,
                        CodeReviewFindingSeverity.Hint,
                        file.Path,
                        nextStartLine,
                        $"Add a blank line between methods `{currentMethod.Identifier.ValueText}` and `{nextMethod.Identifier.ValueText}`.");
                }
            }
        }
    }
}
