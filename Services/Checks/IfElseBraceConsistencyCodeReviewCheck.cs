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

using ReviewG33k.Services;

namespace ReviewG33k.Services.Checks;

public sealed class IfElseBraceConsistencyCodeReviewCheck : CodeReviewCheckBase
{
    public override string RuleId => CodeReviewRuleIds.IfElseBraceConsistency;

    public override string DisplayName => "If/else brace consistency";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            var root = RoslynCodeReviewCheckUtilities.ParseRoot(file);
            var ifStatements = root.DescendantNodes().OfType<IfStatementSyntax>();
            foreach (var ifStatement in ifStatements)
            {
                if (ifStatement.Else == null || RoslynCodeReviewCheckUtilities.IsElseIf(ifStatement))
                    continue;
                if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, ifStatement.Span))
                    continue;

                var hasIfBraces = ifStatement.Statement is BlockSyntax;
                var hasElseBraces = ifStatement.Else.Statement is BlockSyntax;
                if (hasIfBraces == hasElseBraces)
                    continue;

                var lineNumber = ifStatement.IfKeyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                AddFinding(
                    report,
                    CodeReviewFindingSeverity.Suggestion,
                    file.Path,
                    lineNumber,
                    "If/else branches should both use braces when either branch uses braces.");
            }
        }
    }
}
