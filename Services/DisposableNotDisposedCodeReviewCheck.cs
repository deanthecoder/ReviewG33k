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

public sealed class DisposableNotDisposedCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
    private static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(CreateMetadataReferences);

    public override string RuleId => CodeReviewRuleIds.DisposableNotDisposed;

    public override string DisplayName => "Disposable created without disposal";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Text))
                continue;

            var syntaxTree = CSharpSyntaxTree.ParseText(file.Text, ParseOptions, file.Path);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ReviewG33k.DisposableNotDisposed",
                syntaxTrees: [syntaxTree],
                references: MetadataReferences.Value,
                options: CompilationOptions);
            var diagnostics = compilation.GetDiagnostics();
            if (HasSourceErrorsForTree(diagnostics, syntaxTree))
                continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetCompilationUnitRoot();
            var localDeclarations = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>();
            foreach (var localDeclaration in localDeclarations)
            {
                if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, localDeclaration.Span))
                    continue;
                if (localDeclaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                    continue;

                var declarationBlock = localDeclaration.Parent as BlockSyntax;
                foreach (var variable in localDeclaration.Declaration?.Variables ?? [])
                {
                    if (variable.Initializer?.Value is not ObjectCreationExpressionSyntax &&
                        variable.Initializer?.Value is not ImplicitObjectCreationExpressionSyntax)
                    {
                        continue;
                    }

                    var localSymbol = semanticModel.GetDeclaredSymbol(variable) as ILocalSymbol;
                    var typeSymbol = localSymbol?.Type ?? semanticModel.GetTypeInfo(variable.Initializer.Value).Type;
                    if (!ImplementsDisposable(typeSymbol))
                        continue;

                    if (localSymbol != null &&
                        declarationBlock != null &&
                        IsDisposedLater(semanticModel, declarationBlock, localDeclaration.SpanStart, localSymbol))
                    {
                        continue;
                    }

                    var lineNumber = variable.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var typeName = typeSymbol?.Name ?? "disposable";
                    AddFinding(
                        report,
                        CodeReviewFindingSeverity.Suggestion,
                        file.Path,
                        lineNumber,
                        $"Disposable `{typeName}` is created without `using`/`await using` or explicit dispose call.");
                }
            }
        }
    }

    private static bool IsDisposedLater(
        SemanticModel semanticModel,
        BlockSyntax declarationBlock,
        int declarationSpanStart,
        ILocalSymbol localSymbol)
    {
        var invocations = declarationBlock.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            if (invocation.SpanStart <= declarationSpanStart)
                continue;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var invokedName = memberAccess.Name.Identifier.ValueText;
            if (!string.Equals(invokedName, "Dispose", StringComparison.Ordinal) &&
                !string.Equals(invokedName, "DisposeAsync", StringComparison.Ordinal))
            {
                continue;
            }

            var targetSymbol = semanticModel.GetSymbolInfo(memberAccess.Expression).Symbol;
            if (targetSymbol != null && SymbolEqualityComparer.Default.Equals(targetSymbol, localSymbol))
                return true;
        }

        return false;
    }

    private static bool ImplementsDisposable(ITypeSymbol typeSymbol)
    {
        if (typeSymbol == null)
            return false;

        var interfaces = typeSymbol.AllInterfaces;
        return interfaces.Any(@interface =>
            string.Equals(@interface.ToDisplayString(), "System.IDisposable", StringComparison.Ordinal) ||
            string.Equals(@interface.ToDisplayString(), "System.IAsyncDisposable", StringComparison.Ordinal));
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
