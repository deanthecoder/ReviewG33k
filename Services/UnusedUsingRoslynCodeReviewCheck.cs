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

public sealed class UnusedUsingRoslynCodeReviewCheck : CodeReviewCheckBase
{
    private const string UnusedUsingDiagnosticId = "CS8019";
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
    private static readonly CSharpCompilationOptions CompilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { { UnusedUsingDiagnosticId, ReportDiagnostic.Warn } });
    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(CreateMetadataReferences);

    public override string RuleId => CodeReviewRuleIds.UnusedUsingsRoslyn;

    public override string DisplayName => "Unused using directives (Roslyn)";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Text))
                continue;

            var syntaxTree = CSharpSyntaxTree.ParseText(file.Text, ParseOptions, file.Path);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ReviewG33k.UnusedUsings",
                syntaxTrees: [syntaxTree],
                references: MetadataReferences.Value,
                options: CompilationOptions);
            var diagnostics = compilation.GetDiagnostics();
            if (diagnostics.Length == 0)
                continue;

            if (HasSourceErrorsForTree(diagnostics, syntaxTree))
                continue;

            var root = syntaxTree.GetRoot();
            foreach (var diagnostic in diagnostics.Where(IsUnusedUsingDiagnostic))
            {
                var lineNumber = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
                if (!file.IsAdded && !file.AddedLineNumbers.Contains(lineNumber))
                    continue;

                var usingDirective = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<UsingDirectiveSyntax>();
                var usingText = BuildUsingText(usingDirective);
                var message = string.IsNullOrWhiteSpace(usingText)
                    ? "Unused using directive detected."
                    : $"Unused using directive `{usingText}`.";
                AddFinding(report, CodeReviewFindingSeverity.Hint, file.Path, lineNumber, message);
            }
        }
    }

    private static bool IsUnusedUsingDiagnostic(Diagnostic diagnostic) =>
        diagnostic is { Id: UnusedUsingDiagnosticId } &&
        diagnostic.Location != Location.None &&
        diagnostic.Location.IsInSource;

    private static bool HasSourceErrorsForTree(IEnumerable<Diagnostic> diagnostics, SyntaxTree syntaxTree) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error &&
            diagnostic.Location != Location.None &&
            diagnostic.Location.IsInSource &&
            diagnostic.Location.SourceTree == syntaxTree);

    private static string BuildUsingText(UsingDirectiveSyntax usingDirective)
    {
        var text = usingDirective?.ToString().Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return text.EndsWith(';') ? text : $"{text};";
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
}
