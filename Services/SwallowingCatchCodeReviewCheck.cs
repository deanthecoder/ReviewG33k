// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

namespace ReviewG33k.Services;

public sealed class SwallowingCatchCodeReviewCheck : CodeReviewCheckBase
{
    public override string RuleId => CodeReviewRuleIds.SwallowingCatch;

    public override string DisplayName => "exception swallowing in catch";

    public override void Analyze(CodeReviewAnalysisContext context, CodeSmellReport report)
    {
        foreach (var file in context.Files)
        {
            foreach (var catchBlock in CodeReviewCheckUtilities.EnumerateAddedCatchBlocks(file))
            {
                if (string.IsNullOrWhiteSpace(catchBlock.BodyWithoutComments))
                    continue;

                if (!CodeReviewCheckUtilities.ContainsRethrow(catchBlock.BodyWithoutComments) &&
                    !CodeReviewCheckUtilities.ContainsLoggingCall(catchBlock.BodyWithoutComments))
                {
                    AddFinding(report, CodeReviewFindingSeverity.Important, file.Path, catchBlock.StartLine, "Catch block appears to swallow exceptions.");
                }
            }
        }
    }
}
