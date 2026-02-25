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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReviewG33k.Services;

public sealed class TaskRunAsyncCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
    private static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(CreateMetadataReferences);

    public override string RuleId => CodeReviewRuleIds.TaskRunAsync;

    public override string DisplayName => "Task.Run(async ...)";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Text))
                continue;

            var syntaxTree = CSharpSyntaxTree.ParseText(file.Text, ParseOptions, file.Path);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ReviewG33k.TaskRunAsync",
                syntaxTrees: [syntaxTree],
                references: MetadataReferences.Value,
                options: CompilationOptions);
            var diagnostics = compilation.GetDiagnostics();
            if (HasSourceErrorsForTree(diagnostics, syntaxTree))
                continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetCompilationUnitRoot();
            var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var invocation in invocations)
            {
                if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, invocation.Span))
                    continue;
                var invokedMethod = GetInvokedMethodSymbol(semanticModel, invocation);
                if (!IsTaskRunMethod(invokedMethod))
                    continue;
                if (!FirstDelegateParameterReturnsTaskLike(invokedMethod))
                    continue;

                var lineNumber = RoslynCodeReviewCheckUtilities.GetStartLine(invocation);
                AddFinding(report, CodeReviewFindingSeverity.Suggestion, file.Path, lineNumber, "Task.Run wrapping async code detected (possible fake async).");
            }
        }
    }

    private static IMethodSymbol GetInvokedMethodSymbol(SemanticModel semanticModel, InvocationExpressionSyntax invocation)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        return symbolInfo.Symbol as IMethodSymbol ??
               symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    private static bool IsTaskRunMethod(IMethodSymbol methodSymbol)
    {
        if (methodSymbol == null || !string.Equals(methodSymbol.Name, "Run", StringComparison.Ordinal))
            return false;

        var containingType = methodSymbol.ContainingType;
        if (containingType == null || !string.Equals(containingType.Name, "Task", StringComparison.Ordinal))
            return false;

        return string.Equals(containingType.ContainingNamespace?.ToDisplayString(), "System.Threading.Tasks", StringComparison.Ordinal);
    }

    private static bool FirstDelegateParameterReturnsTaskLike(IMethodSymbol methodSymbol)
    {
        if (methodSymbol?.Parameters.Length < 1)
            return false;

        if (methodSymbol.Parameters[0].Type is not INamedTypeSymbol delegateType || delegateType.TypeKind != TypeKind.Delegate)
            return false;

        var returnType = delegateType.DelegateInvokeMethod?.ReturnType;
        return IsTaskLike(returnType);
    }

    private static bool IsTaskLike(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol namedType)
            return false;

        var namespaceName = namedType.ContainingNamespace?.ToDisplayString();
        if (!string.Equals(namespaceName, "System.Threading.Tasks", StringComparison.Ordinal))
            return false;

        return string.Equals(namedType.Name, "Task", StringComparison.Ordinal) ||
               string.Equals(namedType.Name, "ValueTask", StringComparison.Ordinal);
    }

    private static bool HasSourceErrorsForTree(IEnumerable<Diagnostic> diagnostics, SyntaxTree syntaxTree) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error &&
            diagnostic.Location != Location.None &&
            diagnostic.Location.IsInSource &&
            diagnostic.Location.SourceTree == syntaxTree);

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences()
    {
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedAssemblies))
            return [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

        return trustedAssemblies
            .Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .ToArray();
    }
}
