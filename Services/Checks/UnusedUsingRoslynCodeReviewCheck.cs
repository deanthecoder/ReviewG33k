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
using Microsoft.CodeAnalysis.Text;
using ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Services.Checks;

public sealed class UnusedUsingRoslynCodeReviewCheck : CodeReviewCheckBase, IFixableCodeReviewCheck
{
    private const string UnusedUsingDiagnosticId = "CS8019";
    private static readonly CSharpCompilationOptions CompilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { { UnusedUsingDiagnosticId, ReportDiagnostic.Warn } });
    private static readonly Lazy<IReadOnlyList<MetadataReference>> MetadataReferences = new(CreateMetadataReferences);

    public override string RuleId => CodeReviewRuleIds.UnusedUsingsRoslyn;

    public override string DisplayName => "Unused using directives (Roslyn)";

    public bool CanFix(CodeSmellFinding finding) =>
        finding != null &&
        string.Equals(finding.RuleId, RuleId, StringComparison.OrdinalIgnoreCase) &&
        finding.LineNumber > 0;

    public bool TryFix(CodeSmellFinding finding, string resolvedFilePath, out string resultMessage)
    {
        resultMessage = null;

        if (!CanFix(finding))
        {
            resultMessage = "Finding is not fixable.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(resolvedFilePath) || !File.Exists(resolvedFilePath))
        {
            resultMessage = "File path could not be resolved.";
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(resolvedFilePath);
        }
        catch (Exception exception)
        {
            resultMessage = $"Could not read file: {exception.Message}";
            return false;
        }

        var sourceText = SourceText.From(text);
        var lineIndex = finding.LineNumber - 1;
        if (lineIndex < 0 || lineIndex >= sourceText.Lines.Count)
        {
            resultMessage = "Finding line number is out of range for this file.";
            return false;
        }

        var line = sourceText.Lines[lineIndex];
        var lineText = line.ToString();
        var trimmed = lineText.TrimStart();
        if (!trimmed.StartsWith("using ", StringComparison.Ordinal) &&
            !trimmed.StartsWith("using\t", StringComparison.Ordinal) &&
            !trimmed.StartsWith("global using ", StringComparison.Ordinal) &&
            !trimmed.StartsWith("global using\t", StringComparison.Ordinal))
        {
            resultMessage = "Target line is not a using directive.";
            return false;
        }

        var spanToRemove = TextSpan.FromBounds(line.Start, line.EndIncludingLineBreak);
        var updatedText = sourceText.WithChanges(new TextChange(spanToRemove, string.Empty)).ToString();
        updatedText = CodeReviewFixTextUtilities.CollapseConsecutiveBlankLinesNearLine(updatedText, lineIndex);

        try
        {
            File.WriteAllText(resolvedFilePath, updatedText);
        }
        catch (Exception exception)
        {
            resultMessage = $"Could not write file: {exception.Message}";
            return false;
        }

        resultMessage = "Removed unused using directive.";
        return true;
    }

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Text))
                continue;

            var syntaxTree = RoslynCodeReviewCheckUtilities.ParseTree(file);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ReviewG33k.UnusedUsings",
                syntaxTrees: [syntaxTree],
                references: MetadataReferences.Value,
                options: CompilationOptions);
            var diagnostics = compilation.GetDiagnostics();
            if (diagnostics.Length == 0)
                continue;

            if (RoslynCodeReviewCheckUtilities.HasSourceErrorsForTree(diagnostics, syntaxTree))
                continue;

            var root = RoslynCodeReviewCheckUtilities.ParseRoot(file);
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
