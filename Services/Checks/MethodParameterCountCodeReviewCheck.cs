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
using Microsoft.CodeAnalysis.CSharp.Syntax;

using ReviewG33k.Services;

namespace ReviewG33k.Services.Checks;

public sealed class MethodParameterCountCodeReviewCheck : CodeReviewCheckBase
{
    private const int ParameterThreshold = 6;

    public override string RuleId => CodeReviewRuleIds.MethodParameterCount;

    public override string DisplayName => "Methods/constructors with high parameter count";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            var root = RoslynCodeReviewCheckUtilities.ParseRoot(file);
            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                AnalyzeCallable(
                    file,
                    report,
                    method,
                    method.ParameterList,
                    "Method",
                    method.Identifier.ValueText);
            }

            var constructors = root.DescendantNodes().OfType<ConstructorDeclarationSyntax>();
            foreach (var constructor in constructors)
            {
                AnalyzeCallable(
                    file,
                    report,
                    constructor,
                    constructor.ParameterList,
                    "Constructor",
                    constructor.Identifier.ValueText);
            }
        }
    }

    private void AnalyzeCallable(
        CodeReviewChangedFile file,
        CodeSmellReport report,
        SyntaxNode node,
        ParameterListSyntax parameterList,
        string callableKind,
        string callableName)
    {
        if (!RoslynCodeReviewCheckUtilities.IsNodeNew(file, node))
            return;

        var parameterCount = parameterList?.Parameters.Count ?? 0;
        if (parameterCount < ParameterThreshold)
            return;

        var lineNumber = RoslynCodeReviewCheckUtilities.GetStartLine(node);
        AddFinding(
            report,
            CodeReviewFindingSeverity.Hint,
            file.Path,
            lineNumber,
            $"{callableKind} '{callableName}' has {parameterCount} parameters. Consider reducing parameter count.");
    }
}
