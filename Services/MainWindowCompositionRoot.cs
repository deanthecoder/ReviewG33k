// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using ReviewG33k.ViewModels;

namespace ReviewG33k.Services;

internal static class MainWindowCompositionRoot
{
    public static MainWindowDependencies CreateDependencies()
    {
        var settings = Settings.Instance;
        var gitCommandRunner = new GitCommandRunner();
        var inputValidationService = new MainWindowInputValidationService();
        var gitAvailabilityService = new GitAvailabilityService(
            (workingDirectory, arguments) => gitCommandRunner.RunAsync(workingDirectory, arguments));
        var codeLocationOpener = new CodeLocationOpener();
        var logNavigationService = new LogNavigationService();
        var logFeedService = new MainWindowLogFeedService();
        var pullRequestMetadataClient = new BitbucketPullRequestMetadataClient();
        var reviewFindingInteractionService = new ReviewFindingInteractionService(
            codeLocationOpener,
            logNavigationService,
            pullRequestMetadataClient);
        var pullRequestUrlExtractionService = new PullRequestUrlExtractionService();
        var actionStateService = new MainWindowActionStateService(inputValidationService);
        var localBaseBranchService = new LocalBaseBranchService(gitCommandRunner);
        var orchestrator = new CodeReviewOrchestrator(gitCommandRunner);
        var codeSmellReportAnalyzer = new CodeSmellReportAnalyzer(gitCommandRunner);
        var localFindingResampleService = new LocalFindingResampleService(gitCommandRunner, codeSmellReportAnalyzer);
        var pullRequestPreviewService = new PullRequestPreviewService(pullRequestMetadataClient);
        var pullRequestStateNoticeService = new PullRequestStateNoticeService();
        var startupService = new MainWindowStartupService(gitAvailabilityService, orchestrator);
        var codeSmellReportLogService = new CodeSmellReportLogService();
        var reviewExecutionService = new ReviewExecutionService(
            gitCommandRunner,
            orchestrator,
            codeSmellReportAnalyzer,
            codeSmellReportLogService,
            pullRequestMetadataClient);
        var reviewPreparationService = new MainWindowReviewPreparationService(
            inputValidationService,
            localBaseBranchService,
            reviewExecutionService);
        var reviewWorkflowService = new MainWindowReviewWorkflowService(reviewPreparationService);

        return new MainWindowDependencies(
            settings,
            codeLocationOpener,
            logNavigationService,
            logFeedService,
            reviewFindingInteractionService,
            pullRequestUrlExtractionService,
            actionStateService,
            localBaseBranchService,
            localFindingResampleService,
            pullRequestPreviewService,
            pullRequestStateNoticeService,
            codeSmellReportAnalyzer,
            pullRequestMetadataClient,
            startupService,
            reviewWorkflowService);
    }
}
