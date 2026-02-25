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
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ReviewG33k.Services;

internal static class RoslynCodeReviewCheckUtilities
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
    private static readonly CSharpCompilationOptions SemanticCompilationOptions = new(OutputKind.DynamicallyLinkedLibrary);
    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(CreateMetadataReferences);
    private static readonly ConditionalWeakTable<CodeReviewChangedFile, RoslynFileCache> FileCaches = new();

    public static CompilationUnitSyntax ParseRoot(CodeReviewChangedFile file)
    {
        if (file == null)
            return CSharpSyntaxTree.ParseText(string.Empty, ParseOptions).GetCompilationUnitRoot();

        return GetOrCreateFileCache(file).Root.Value;
    }

    public static SyntaxTree ParseTree(CodeReviewChangedFile file)
    {
        if (file == null)
            return CSharpSyntaxTree.ParseText(string.Empty, ParseOptions);

        return GetOrCreateFileCache(file).SyntaxTree.Value;
    }

    public static int GetStartLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static bool IsNodeNew(CodeReviewChangedFile file, SyntaxNode node)
    {
        if (file.IsAdded)
            return true;

        return file.AddedLineNumbers.Contains(GetStartLine(node));
    }

    public static bool SpanContainsAddedLine(CodeReviewChangedFile file, TextSpan span)
    {
        if (file.IsAdded)
            return true;

        var lineSpan = ParseTree(file).GetLineSpan(span);
        var startLine = lineSpan.StartLinePosition.Line + 1;
        var endLine = lineSpan.EndLinePosition.Line + 1;
        return file.AddedLineNumbers.Any(lineNumber => lineNumber >= startLine && lineNumber <= endLine);
    }

    public static bool TryGetSemanticAnalysis(
        CodeReviewChangedFile file,
        out CompilationUnitSyntax root,
        out SemanticModel semanticModel,
        out SyntaxTree syntaxTree,
        out IReadOnlyList<Diagnostic> diagnostics)
    {
        root = null;
        semanticModel = null;
        syntaxTree = null;
        diagnostics = null;

        if (file == null || string.IsNullOrWhiteSpace(file.Text))
            return false;

        var fileCache = GetOrCreateFileCache(file);
        var semanticCache = fileCache.SemanticAnalysis.Value;

        root = fileCache.Root.Value;
        semanticModel = semanticCache.SemanticModel;
        syntaxTree = semanticCache.SyntaxTree;
        diagnostics = semanticCache.Diagnostics;
        return semanticModel != null && syntaxTree != null;
    }

    public static bool HasSourceErrorsForTree(IEnumerable<Diagnostic> diagnostics, SyntaxTree syntaxTree)
    {
        // These checks often run against a single changed file rather than the full project compilation.
        // A green CI build can still produce local Roslyn errors here (missing project-local symbols/types).
        // Treat only parse/syntax errors as blocking so we don't skip useful checks on otherwise-valid PRs.
        _ = diagnostics; // kept for call-site compatibility and future diagnostics-based filtering.

        if (syntaxTree == null)
            return true;

        return syntaxTree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    public static bool IsPrivateProperty(PropertyDeclarationSyntax property) =>
        property.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PrivateKeyword));

    public static bool IsSimpleAutoProperty(PropertyDeclarationSyntax property)
    {
        if (property.AccessorList == null || property.ExpressionBody != null)
            return false;

        var hasGet = false;
        var hasSet = false;
        foreach (var accessor in property.AccessorList.Accessors)
        {
            if (accessor.Body != null || accessor.ExpressionBody != null)
                return false;

            if (accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                hasGet = true;
            else if (accessor.IsKind(SyntaxKind.SetAccessorDeclaration))
                hasSet = true;
        }

        return hasGet && hasSet;
    }

    public static bool TryGetSimplePropertyBackingField(PropertyDeclarationSyntax property, out string fieldName)
    {
        fieldName = null;
        if (property.AccessorList == null || property.ExpressionBody != null)
            return false;

        var getAccessor = property.AccessorList.Accessors.FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration));
        var setAccessor = property.AccessorList.Accessors.FirstOrDefault(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration));
        if (getAccessor == null || setAccessor == null)
            return false;

        if (!TryGetGetterFieldName(getAccessor, out var getterFieldName))
            return false;
        if (!TryGetSetterFieldName(setAccessor, out var setterFieldName))
            return false;
        if (!string.Equals(getterFieldName, setterFieldName, StringComparison.Ordinal))
            return false;

        fieldName = getterFieldName;
        return !string.IsNullOrWhiteSpace(fieldName);
    }

    public static bool IsElseIf(IfStatementSyntax ifStatement) =>
        ifStatement?.Else?.Statement is IfStatementSyntax;

    public static int CountConstructorCodeLines(CodeReviewChangedFile file, ConstructorDeclarationSyntax constructor)
    {
        if (constructor?.Body == null || file?.Lines == null || file.Lines.Count == 0)
            return 0;

        var startLine = constructor.Body.OpenBraceToken.GetLocation().GetLineSpan().StartLinePosition.Line + 2;
        var endLine = constructor.Body.CloseBraceToken.GetLocation().GetLineSpan().StartLinePosition.Line;
        if (endLine < startLine)
            return 0;

        var count = 0;
        for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
        {
            if (lineNumber < 1 || lineNumber > file.Lines.Count)
                continue;

            var line = file.Lines[lineNumber - 1]?.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            count++;
        }

        return count;
    }

    private static bool TryGetGetterFieldName(AccessorDeclarationSyntax getAccessor, out string fieldName)
    {
        fieldName = null;

        if (getAccessor.ExpressionBody != null)
            return TryExtractFieldName(getAccessor.ExpressionBody.Expression, out fieldName);

        if (getAccessor.Body?.Statements.Count != 1 || getAccessor.Body.Statements[0] is not ReturnStatementSyntax returnStatement)
            return false;

        return TryExtractFieldName(returnStatement.Expression, out fieldName);
    }

    private static bool TryGetSetterFieldName(AccessorDeclarationSyntax setAccessor, out string fieldName)
    {
        fieldName = null;
        AssignmentExpressionSyntax assignmentExpression;

        if (setAccessor.ExpressionBody != null)
        {
            assignmentExpression = setAccessor.ExpressionBody.Expression as AssignmentExpressionSyntax;
            if (assignmentExpression == null)
                return false;
        }
        else
        {
            if (setAccessor.Body?.Statements.Count != 1 || setAccessor.Body.Statements[0] is not ExpressionStatementSyntax expressionStatement)
                return false;

            assignmentExpression = expressionStatement.Expression as AssignmentExpressionSyntax;
            if (assignmentExpression == null)
                return false;
        }

        if (!assignmentExpression.IsKind(SyntaxKind.SimpleAssignmentExpression))
            return false;

        if (assignmentExpression.Right is not IdentifierNameSyntax rightIdentifier || !string.Equals(rightIdentifier.Identifier.ValueText, "value", StringComparison.Ordinal))
            return false;

        return TryExtractFieldName(assignmentExpression.Left, out fieldName);
    }

    private static bool TryExtractFieldName(ExpressionSyntax expression, out string fieldName)
    {
        fieldName = null;
        if (expression == null)
            return false;

        if (expression is IdentifierNameSyntax identifierName)
        {
            fieldName = identifierName.Identifier.ValueText;
            return !string.IsNullOrWhiteSpace(fieldName);
        }

        if (expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Expression is ThisExpressionSyntax &&
            memberAccess.Name is IdentifierNameSyntax memberIdentifier)
        {
            fieldName = memberIdentifier.Identifier.ValueText;
            return !string.IsNullOrWhiteSpace(fieldName);
        }

        return false;
    }

    private static RoslynFileCache GetOrCreateFileCache(CodeReviewChangedFile file) =>
        FileCaches.GetValue(file, static changedFile => new RoslynFileCache(changedFile));

    private static SemanticAnalysisCache CreateSemanticAnalysis(SyntaxTree syntaxTree)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "ReviewG33k.SharedSemanticAnalysis",
            syntaxTrees: [syntaxTree],
            references: MetadataReferences.Value,
            options: SemanticCompilationOptions);

        var diagnostics = compilation.GetDiagnostics();
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        return new SemanticAnalysisCache(syntaxTree, semanticModel, diagnostics);
    }

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

    private sealed class RoslynFileCache
    {
        public RoslynFileCache(CodeReviewChangedFile file)
        {
            SyntaxTree = new Lazy<SyntaxTree>(
                () => CSharpSyntaxTree.ParseText(file?.Text ?? string.Empty, ParseOptions, file?.Path),
                isThreadSafe: true);
            Root = new Lazy<CompilationUnitSyntax>(
                () => ((CSharpSyntaxTree)SyntaxTree.Value).GetCompilationUnitRoot(),
                isThreadSafe: true);
            SemanticAnalysis = new Lazy<SemanticAnalysisCache>(
                () => CreateSemanticAnalysis(SyntaxTree.Value),
                isThreadSafe: true);
        }

        public Lazy<SyntaxTree> SyntaxTree { get; }

        public Lazy<CompilationUnitSyntax> Root { get; }

        public Lazy<SemanticAnalysisCache> SemanticAnalysis { get; }
    }

    private sealed class SemanticAnalysisCache
    {
        public SemanticAnalysisCache(SyntaxTree syntaxTree, SemanticModel semanticModel, IReadOnlyList<Diagnostic> diagnostics)
        {
            SyntaxTree = syntaxTree;
            SemanticModel = semanticModel;
            Diagnostics = diagnostics ?? [];
        }

        public SyntaxTree SyntaxTree { get; }

        public SemanticModel SemanticModel { get; }

        public IReadOnlyList<Diagnostic> Diagnostics { get; }
    }
}
