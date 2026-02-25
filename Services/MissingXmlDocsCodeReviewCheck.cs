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
using System.Linq;

namespace ReviewG33k.Services;

public sealed class MissingXmlDocsCodeReviewCheck : CodeReviewCheckBase
{
    public override string RuleId => CodeReviewRuleIds.MissingXmlDocs;

    public override string DisplayName => "XML docs on new public types";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files.Where(file => file.IsAdded))
        {
            if (CodeReviewFileClassification.IsTestFilePath(file.Path))
                continue;

            if (!CodeReviewCheckUtilities.TryGetPublicTypeDeclaration(file.Text, out _, out var declarationLineNumber))
                continue;

            if (!CodeReviewCheckUtilities.HasXmlDocumentationAbove(file.Lines, declarationLineNumber))
                AddFinding(report, CodeReviewFindingSeverity.Hint, file.Path, declarationLineNumber, "Missing XML docs on new public type.");
        }
    }
}
