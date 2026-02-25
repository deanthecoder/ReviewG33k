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

public sealed class UnusedPrivateMemberCodeReviewCheck : CodeReviewCheckBase
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
    private static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(CreateMetadataReferences);

    public override string RuleId => CodeReviewRuleIds.UnusedPrivateMember;

    public override string DisplayName => "Unused private members";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Text))
                continue;

            var syntaxTree = CSharpSyntaxTree.ParseText(file.Text, ParseOptions, file.Path);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ReviewG33k.UnusedPrivateMember",
                syntaxTrees: [syntaxTree],
                references: MetadataReferences.Value,
                options: CompilationOptions);
            var diagnostics = compilation.GetDiagnostics();
            if (HasSourceErrorsForTree(diagnostics, syntaxTree))
                continue;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetCompilationUnitRoot();

            AnalyzeFields(file, report, semanticModel, root);
            AnalyzeMethods(file, report, semanticModel, root);
            AnalyzeProperties(file, report, semanticModel, root);
        }
    }

    private void AnalyzeFields(CodeReviewChangedFile file, CodeSmellReport report, SemanticModel semanticModel, CompilationUnitSyntax root)
    {
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PrivateKeyword)))
                continue;
            if (field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ConstKeyword)))
                continue;
            if (IsInPartialType(field))
                continue;

            foreach (var variable in field.Declaration?.Variables ?? [])
            {
                if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, variable.Span))
                    continue;

                var symbol = semanticModel.GetDeclaredSymbol(variable) as IFieldSymbol;
                if (symbol == null || symbol.IsImplicitlyDeclared)
                    continue;
                if (HasAnyReference(root, semanticModel, symbol))
                    continue;

                AddFinding(
                    report,
                    CodeReviewFindingSeverity.Hint,
                    file.Path,
                    variable.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    $"Private field `{symbol.Name}` appears to be unused.");
            }
        }
    }

    private void AnalyzeMethods(CodeReviewChangedFile file, CodeSmellReport report, SemanticModel semanticModel, CompilationUnitSyntax root)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, method.Span))
                continue;
            if (IsInPartialType(method))
                continue;

            var symbol = semanticModel.GetDeclaredSymbol(method) as IMethodSymbol;
            if (symbol == null || symbol.IsImplicitlyDeclared)
                continue;
            if (symbol.DeclaredAccessibility != Accessibility.Private)
                continue;
            if (symbol.IsOverride || symbol.IsAbstract)
                continue;
            if (HasAnyReference(root, semanticModel, symbol))
                continue;

            AddFinding(
                report,
                CodeReviewFindingSeverity.Hint,
                file.Path,
                RoslynCodeReviewCheckUtilities.GetStartLine(method),
                $"Private method `{symbol.Name}` appears to be unused.");
        }
    }

    private void AnalyzeProperties(CodeReviewChangedFile file, CodeSmellReport report, SemanticModel semanticModel, CompilationUnitSyntax root)
    {
        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (!RoslynCodeReviewCheckUtilities.SpanContainsAddedLine(file, property.Span))
                continue;
            if (IsInPartialType(property))
                continue;

            var symbol = semanticModel.GetDeclaredSymbol(property) as IPropertySymbol;
            if (symbol == null || symbol.IsImplicitlyDeclared)
                continue;
            if (symbol.DeclaredAccessibility != Accessibility.Private)
                continue;
            if (HasAnyReference(root, semanticModel, symbol))
                continue;

            AddFinding(
                report,
                CodeReviewFindingSeverity.Hint,
                file.Path,
                RoslynCodeReviewCheckUtilities.GetStartLine(property),
                $"Private property `{symbol.Name}` appears to be unused.");
        }
    }

    private static bool HasAnyReference(CompilationUnitSyntax root, SemanticModel semanticModel, ISymbol symbol)
    {
        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(name);
            if (SymbolMatches(symbolInfo, symbol))
                return true;
        }

        return false;
    }

    private static bool SymbolMatches(SymbolInfo symbolInfo, ISymbol symbol)
    {
        if (SymbolEqualityComparer.Default.Equals(symbolInfo.Symbol, symbol))
            return true;

        return symbolInfo.CandidateSymbols.Any(candidateSymbol => SymbolEqualityComparer.Default.Equals(candidateSymbol, symbol));
    }

    private static bool IsInPartialType(SyntaxNode node)
    {
        var typeDeclaration = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return typeDeclaration?.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)) == true;
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
