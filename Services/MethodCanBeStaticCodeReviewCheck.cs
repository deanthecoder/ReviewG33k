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

public sealed class MethodCanBeStaticCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
    private static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(CreateMetadataReferences);

    public override string RuleId => CodeReviewRuleIds.MethodCanBeStatic;

    public override string DisplayName => "Methods that can be static";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Text))
                continue;

            var syntaxTree = CSharpSyntaxTree.ParseText(file.Text, ParseOptions, file.Path);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ReviewG33k.MethodCanBeStatic",
                syntaxTrees: [syntaxTree],
                references: MetadataReferences.Value,
                options: CompilationOptions);
            var diagnostics = compilation.GetDiagnostics();
            if (HasSourceErrorsForTree(diagnostics, syntaxTree))
                continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetCompilationUnitRoot();
            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var method in methods)
            {
                if (!RoslynCodeReviewCheckUtilities.IsNodeNew(file, method))
                    continue;
                if (!CanMethodBeStatic(semanticModel, method, out var methodSymbol))
                    continue;
                if (UsesInstanceState(semanticModel, method, methodSymbol.ContainingType))
                    continue;

                var lineNumber = RoslynCodeReviewCheckUtilities.GetStartLine(method);
                AddFinding(
                    report,
                    CodeReviewFindingSeverity.Hint,
                    file.Path,
                    lineNumber,
                    $"Method `{method.Identifier.ValueText}` can likely be made static.");
            }
        }
    }

    private static bool CanMethodBeStatic(SemanticModel semanticModel, MethodDeclarationSyntax method, out IMethodSymbol methodSymbol)
    {
        methodSymbol = semanticModel.GetDeclaredSymbol(method);
        if (methodSymbol == null)
            return false;

        if (methodSymbol.IsStatic ||
            methodSymbol.IsAbstract ||
            methodSymbol.IsOverride ||
            methodSymbol.IsVirtual ||
            methodSymbol.IsExtern ||
            methodSymbol.IsImplicitlyDeclared)
        {
            return false;
        }

        if (methodSymbol.ContainingType == null || methodSymbol.ContainingType.TypeKind == TypeKind.Interface)
            return false;
        if (methodSymbol.ExplicitInterfaceImplementations.Length > 0)
            return false;
        if (method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
            return false;

        return true;
    }

    private static bool UsesInstanceState(SemanticModel semanticModel, MethodDeclarationSyntax method, INamedTypeSymbol containingType)
    {
        if (containingType == null)
            return true;

        if (method.DescendantNodes().OfType<ThisExpressionSyntax>().Any() ||
            method.DescendantNodes().OfType<BaseExpressionSyntax>().Any())
        {
            return true;
        }

        foreach (var node in method.DescendantNodes())
        {
            if (node is not IdentifierNameSyntax && node is not MemberAccessExpressionSyntax)
                continue;

            var symbol = semanticModel.GetSymbolInfo(node).Symbol;
            if (!RequiresInstance(symbol, containingType))
                continue;

            return true;
        }

        return false;
    }

    private static bool RequiresInstance(ISymbol symbol, INamedTypeSymbol containingType)
    {
        if (symbol == null)
            return false;

        if (symbol is IFieldSymbol fieldSymbol)
            return !fieldSymbol.IsStatic && IsInTypeHierarchy(fieldSymbol.ContainingType, containingType);
        if (symbol is IPropertySymbol propertySymbol)
            return !propertySymbol.IsStatic && IsInTypeHierarchy(propertySymbol.ContainingType, containingType);
        if (symbol is IEventSymbol eventSymbol)
            return !eventSymbol.IsStatic && IsInTypeHierarchy(eventSymbol.ContainingType, containingType);
        if (symbol is IMethodSymbol methodSymbol)
            return !methodSymbol.IsStatic && IsInTypeHierarchy(methodSymbol.ContainingType, containingType);

        return false;
    }

    private static bool IsInTypeHierarchy(INamedTypeSymbol candidateType, INamedTypeSymbol containingType)
    {
        if (candidateType == null || containingType == null)
            return false;

        for (var current = containingType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(candidateType, current))
                return true;
        }

        return false;
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
