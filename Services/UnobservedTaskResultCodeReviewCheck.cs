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

public sealed class UnobservedTaskResultCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
    private static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(CreateMetadataReferences);

    public override string RuleId => CodeReviewRuleIds.UnobservedTaskResult;

    public override string DisplayName => "Unobserved Task/ValueTask results";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Text))
                continue;

            var syntaxTree = CSharpSyntaxTree.ParseText(file.Text, ParseOptions, file.Path);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ReviewG33k.UnobservedTaskResult",
                syntaxTrees: [syntaxTree],
                references: MetadataReferences.Value,
                options: CompilationOptions);
            var diagnostics = compilation.GetDiagnostics();
            if (HasSourceErrorsForTree(diagnostics, syntaxTree))
                continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetCompilationUnitRoot();
            var statements = root.DescendantNodes().OfType<ExpressionStatementSyntax>();
            foreach (var statement in statements)
            {
                if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, statement.Span))
                    continue;
                if (statement.Expression is AwaitExpressionSyntax)
                    continue;
                if (statement.Expression is not InvocationExpressionSyntax invocation)
                    continue;
                if (!ReturnsTaskLike(semanticModel, invocation))
                    continue;

                var lineNumber = RoslynCodeReviewCheckUtilities.GetStartLine(statement);
                var invokedMethodName = (semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol)?.Name;
                var methodHint = string.IsNullOrWhiteSpace(invokedMethodName)
                    ? "Result of async call is ignored."
                    : $"Result of async call `{invokedMethodName}` is ignored.";
                AddFinding(
                    report,
                    CodeReviewFindingSeverity.Suggestion,
                    file.Path,
                    lineNumber,
                    $"{methodHint} Consider awaiting or explicitly handling the returned `Task`/`ValueTask`.");
            }
        }
    }

    private static bool ReturnsTaskLike(SemanticModel semanticModel, InvocationExpressionSyntax invocation)
    {
        var returnType = semanticModel.GetTypeInfo(invocation).Type as INamedTypeSymbol;
        if (returnType == null)
            return false;

        var typeName = returnType.Name;
        var namespaceName = returnType.ContainingNamespace?.ToDisplayString();
        return string.Equals(namespaceName, "System.Threading.Tasks", StringComparison.Ordinal) &&
               (string.Equals(typeName, "Task", StringComparison.Ordinal) ||
                string.Equals(typeName, "ValueTask", StringComparison.Ordinal));
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
