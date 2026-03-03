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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Services.Checks;

/// <summary>
/// Finds and optionally fixes if/else pairs where braces are unnecessary for both branches.
/// </summary>
public sealed class IfElseUnnecessaryBracesCodeReviewCheck : CodeReviewCheckBase, IFixableCodeReviewCheck
{
    public override string RuleId => CodeReviewRuleIds.IfElseUnnecessaryBraces;

    public override string DisplayName => "If/else unnecessary braces";

    public bool CanFix(CodeSmellFinding finding) =>
        finding != null &&
        string.Equals(finding.RuleId, RuleId, StringComparison.OrdinalIgnoreCase) &&
        finding.LineNumber > 0;

    public bool TryFix(CodeSmellFinding finding, FileInfo resolvedFile, out string resultMessage)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (resolvedFile == null)
        {
            resultMessage = "A valid file path is required.";
            return false;
        }

        if (!this.TryPrepareFix(
                finding,
                resolvedFile,
                out var sourceText,
                out var lineIndex,
                out resultMessage))
        {
            return false;
        }

        var root = CSharpSyntaxTree.ParseText(sourceText).GetCompilationUnitRoot();
        var lineSpan = TextSpan.FromBounds(sourceText.Lines[lineIndex].Start, sourceText.Lines[lineIndex].End);
        var ifStatement = root
            .DescendantNodes()
            .OfType<IfStatementSyntax>()
            .FirstOrDefault(node => node.Span.IntersectsWith(lineSpan) && HasUnnecessaryBraces(node));
        if (ifStatement?.Statement is not BlockSyntax ifBlock || !CanUnwrapBlock(ifBlock))
        {
            resultMessage = "Target line does not contain an if/else with unnecessary braces.";
            return false;
        }

        var updatedIfStatement = ifStatement.WithStatement(UnwrapBlock(ifBlock));
        if (ifStatement.Else?.Statement is BlockSyntax elseBlock && CanUnwrapBlock(elseBlock))
            updatedIfStatement = updatedIfStatement.WithElse(ifStatement.Else.WithStatement(UnwrapBlock(elseBlock)));

        var updatedRoot = root.ReplaceNode(ifStatement, updatedIfStatement);
        var updatedText = updatedRoot.ToFullString();
        updatedText = CodeReviewFixTextUtilities.CollapseConsecutiveBlankLinesNearLine(updatedText, lineIndex);

        if (!this.TryWriteUpdatedText(resolvedFile, updatedText, out resultMessage))
            return false;

        resultMessage = "Removed unnecessary braces from if/else branches.";
        return true;
    }

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        foreach (var file in context.Files)
        {
            var root = RoslynCodeReviewCheckUtilities.ParseRoot(file);
            var ifStatements = root.DescendantNodes().OfType<IfStatementSyntax>();
            foreach (var ifStatement in ifStatements)
            {
                if (!HasUnnecessaryBraces(ifStatement))
                    continue;
                if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, ifStatement.Span))
                    continue;

                var lineNumber = ifStatement.IfKeyword.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                AddFinding(
                    report,
                    CodeReviewFindingSeverity.Hint,
                    file.Path,
                    lineNumber,
                    "If/else braces are unnecessary when each branch contains a single simple statement.");
            }
        }
    }

    private static bool HasUnnecessaryBraces(IfStatementSyntax ifStatement)
    {
        if (ifStatement == null)
            return false;

        if (ifStatement.Else == null)
            return false;
        if (ifStatement.Statement is not BlockSyntax ifBlock)
            return false;

        if (ifStatement.Else.Statement is BlockSyntax elseBlock)
            return CanUnwrapBlock(ifBlock) && CanUnwrapBlock(elseBlock);

        return false;
    }

    private static bool HasOnlyWhitespaceTrivia(SyntaxTriviaList trivia) =>
        trivia.All(item => item.IsKind(SyntaxKind.WhitespaceTrivia) || item.IsKind(SyntaxKind.EndOfLineTrivia));

    private static bool HasSafeBraceTrivia(BlockSyntax block)
    {
        if (!HasOnlyWhitespaceTrivia(block.OpenBraceToken.LeadingTrivia) ||
            !HasOnlyWhitespaceTrivia(block.OpenBraceToken.TrailingTrivia) ||
            !HasOnlyWhitespaceTrivia(block.CloseBraceToken.LeadingTrivia) ||
            !HasOnlyWhitespaceTrivia(block.CloseBraceToken.TrailingTrivia))
        {
            return false;
        }

        return true;
    }

    private static bool CanUseWithoutBraces(StatementSyntax statement) =>
        statement is not LocalDeclarationStatementSyntax and not IfStatementSyntax;

    private static bool CanUnwrapBlock(BlockSyntax block)
    {
        if (block == null || block.Statements.Count != 1)
            return false;
        if (!HasSafeBraceTrivia(block))
            return false;

        var statement = block.Statements[0];
        if (!CanUseWithoutBraces(statement))
            return false;
        if (!StatementSpansSingleLine(statement))
            return false;

        return true;
    }

    private static StatementSyntax UnwrapBlock(BlockSyntax block)
    {
        var statement = block.Statements.Single();
        return statement;
    }

    private static bool StatementSpansSingleLine(StatementSyntax statement)
    {
        if (statement == null)
            return false;

        var lineSpan = statement.GetLocation().GetLineSpan();
        return lineSpan.StartLinePosition.Line == lineSpan.EndLinePosition.Line;
    }
}
