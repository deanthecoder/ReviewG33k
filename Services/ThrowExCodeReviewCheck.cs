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
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReviewG33k.Services;

public sealed class ThrowExCodeReviewCheck : CodeReviewCheckBase
{
    public override string RuleId => CodeReviewRuleIds.ThrowExInCatch;

    public override string DisplayName => "`throw ex;` in catch blocks";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            var root = RoslynCodeReviewCheckUtilities.ParseRoot(file);
            var throwStatements = root.DescendantNodes().OfType<ThrowStatementSyntax>();
            foreach (var throwStatement in throwStatements)
            {
                if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, throwStatement.Span))
                    continue;
                if (throwStatement.Expression is not IdentifierNameSyntax identifier)
                    continue;

                var catchClause = throwStatement.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
                var caughtExceptionName = catchClause?.Declaration?.Identifier.ValueText;
                if (string.IsNullOrWhiteSpace(caughtExceptionName))
                    continue;
                if (!string.Equals(identifier.Identifier.ValueText, caughtExceptionName, StringComparison.Ordinal))
                    continue;

                var lineNumber = RoslynCodeReviewCheckUtilities.GetStartLine(throwStatement);
                AddFinding(
                    report,
                    CodeReviewFindingSeverity.Important,
                    file.Path,
                    lineNumber,
                    "Use `throw;` instead of `throw ex;` to preserve the original stack trace.");
            }
        }
    }
}
