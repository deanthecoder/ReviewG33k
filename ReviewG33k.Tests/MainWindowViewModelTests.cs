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

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class MainWindowViewModelTests
{
    [Test]
    public void ConstructorWhenSettingsIsNullThrows()
    {
        Assert.That(() => new MainWindowViewModel(null), Throws.ArgumentNullException);
    }

    [Test]
    public void ConstructorInitializesStateFromSettings()
    {
        var settings = new Settings
        {
            RepositoryRootPath = "  C:\\RepoRoot  ",
            LocalReviewRepositoryPath = "  C:\\LocalRepo  ",
            LocalReviewBaseBranch = "  ",
            ReviewModeIndex = 2,
            IncludeFullModifiedFiles = true
        };

        var viewModel = new MainWindowViewModel(settings);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.RepositoryRootPath, Is.EqualTo("C:\\RepoRoot"));
            Assert.That(viewModel.LocalRepositoryPath, Is.EqualTo("C:\\LocalRepo"));
            Assert.That(viewModel.LocalBaseBranch, Is.EqualTo("main"));
            Assert.That(viewModel.ReviewModeIndex, Is.EqualTo(2));
            Assert.That(viewModel.ScanScopeIndex, Is.EqualTo(1));
        });
    }

    [Test]
    public void ConstructorWhenReviewModeIndexIsOutOfRangeFallsBackToLegacyFlag()
    {
        var settings = new Settings
        {
            ReviewModeIndex = -1,
            UseLocalCommittedReview = true
        };

        var viewModel = new MainWindowViewModel(settings);

        Assert.That(viewModel.ReviewModeIndex, Is.EqualTo(1));
    }

    [Test]
    public void PullRequestUrlWhenSetToNullNormalizesToEmptyString()
    {
        var viewModel = new MainWindowViewModel(new Settings());
        viewModel.PullRequestUrl = null;
        Assert.That(viewModel.PullRequestUrl, Is.EqualTo(string.Empty));
    }

    [Test]
    public void AddLocalBaseBranchOptionWhenDuplicateIgnoringCaseDoesNotAddAgain()
    {
        var viewModel = new MainWindowViewModel(new Settings());

        viewModel.SetLocalBaseBranchOptions(["main"], "main");
        viewModel.AddLocalBaseBranchOption("MAIN");
        viewModel.AddLocalBaseBranchOption("develop");

        Assert.That(viewModel.LocalBaseBranchOptions, Is.EqualTo(new[] { "main", "develop" }));
    }

    [Test]
    public void SetLocalBaseBranchOptionsWhenSelectionMissingAddsAndSelectsIt()
    {
        var viewModel = new MainWindowViewModel(new Settings());

        viewModel.SetLocalBaseBranchOptions(["main", "develop"], "release/1.0");

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.LocalBaseBranchOptions, Does.Contain("main"));
            Assert.That(viewModel.LocalBaseBranchOptions, Does.Contain("develop"));
            Assert.That(viewModel.LocalBaseBranchOptions, Does.Contain("release/1.0"));
            Assert.That(viewModel.LocalBaseBranch, Is.EqualTo("release/1.0"));
        });
    }

    [Test]
    public void ReviewModeUiPropertiesWhenPullRequestModeIsSelectedShowPullRequestInputs()
    {
        var viewModel = new MainWindowViewModel(new Settings())
        {
            ReviewModeIndex = 0
        };

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.ShowPullRequestInputs, Is.True);
            Assert.That(viewModel.ShowLocalBaseBranch, Is.False);
            Assert.That(viewModel.PrepareReviewButtonText, Is.EqualTo("Review PR"));
        });
    }

    [Test]
    public void ReviewModeUiPropertiesWhenLocalCommittedModeIsSelectedShowBaseBranch()
    {
        var viewModel = new MainWindowViewModel(new Settings())
        {
            ReviewModeIndex = 1
        };

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.ShowPullRequestInputs, Is.False);
            Assert.That(viewModel.ShowLocalBaseBranch, Is.True);
            Assert.That(viewModel.PrepareReviewButtonText, Is.EqualTo("Review Local"));
        });
    }

    [Test]
    public void ShowPullRequestMetadataDependsOnModeAndMetadataText()
    {
        var viewModel = new MainWindowViewModel(new Settings())
        {
            ReviewModeIndex = 0
        };

        viewModel.PullRequestMetadataText = "Metadata";
        Assert.That(viewModel.ShowPullRequestMetadata, Is.True);

        viewModel.ReviewModeIndex = 1;
        Assert.That(viewModel.ShowPullRequestMetadata, Is.False);
    }

    [Test]
    public void UpdateActionStateInputsWhenPullRequestIsClosedDisablesPrepare()
    {
        var viewModel = new MainWindowViewModel(new Settings())
        {
            ReviewModeIndex = 0
        };

        viewModel.UpdateActionStateInputs(
            isGitAvailable: true,
            canReviewCurrentPullRequest: false,
            hasValidPullRequestInput: true,
            hasValidPullRequestPrepareInputs: true,
            hasValidLocalPrepareInputs: false,
            hasAvailableSolution: true,
            canCancelCurrentOperation: true,
            isCancellationRequested: false);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.CanPrepareReview, Is.False);
            Assert.That(viewModel.CanOpenPullRequest, Is.True);
            Assert.That(viewModel.CanOpenSolution, Is.True);
        });
    }

    [Test]
    public void UpdateActionStateInputsWhenLocalModeAndIdleEnablesPrepare()
    {
        var viewModel = new MainWindowViewModel(new Settings())
        {
            ReviewModeIndex = 1
        };

        viewModel.UpdateActionStateInputs(
            isGitAvailable: true,
            canReviewCurrentPullRequest: true,
            hasValidPullRequestInput: true,
            hasValidPullRequestPrepareInputs: true,
            hasValidLocalPrepareInputs: true,
            hasAvailableSolution: false,
            canCancelCurrentOperation: true,
            isCancellationRequested: false);

        Assert.That(viewModel.CanPrepareReview, Is.True);

        viewModel.IsBusy = true;
        Assert.Multiple(() =>
        {
            Assert.That(viewModel.CanPrepareReview, Is.False);
            Assert.That(viewModel.CanCancelProcessing, Is.True);
        });
    }

    [Test]
    public void UpdateActionStateInputsWhenCancellationRequestedShowsStoppingText()
    {
        var viewModel = new MainWindowViewModel(new Settings())
        {
            ReviewModeIndex = 0,
            IsBusy = true
        };

        viewModel.UpdateActionStateInputs(
            isGitAvailable: true,
            canReviewCurrentPullRequest: true,
            hasValidPullRequestInput: true,
            hasValidPullRequestPrepareInputs: true,
            hasValidLocalPrepareInputs: false,
            hasAvailableSolution: false,
            canCancelCurrentOperation: true,
            isCancellationRequested: true);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.ShowCancelStoppingText, Is.True);
            Assert.That(viewModel.CanCancelProcessing, Is.False);
        });
    }

    [Test]
    public void UpdateBusyProgressWhenTotalPositiveSetsBoundedValues()
    {
        var viewModel = new MainWindowViewModel(new Settings());

        viewModel.UpdateBusyProgress(completed: 12, total: 10);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.BusyProgressIsIndeterminate, Is.False);
            Assert.That(viewModel.BusyProgressMaximum, Is.EqualTo(10));
            Assert.That(viewModel.BusyProgressValue, Is.EqualTo(10));
        });
    }

    [Test]
    public void SetBusyProgressIndeterminateResetsProgressFields()
    {
        var viewModel = new MainWindowViewModel(new Settings());
        viewModel.UpdateBusyProgress(completed: 3, total: 10);

        viewModel.SetBusyProgressIndeterminate();

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.BusyProgressIsIndeterminate, Is.True);
            Assert.That(viewModel.BusyProgressMaximum, Is.EqualTo(1));
            Assert.That(viewModel.BusyProgressValue, Is.EqualTo(0));
        });
    }

    [Test]
    public void ReviewModeInfoTooltipReflectsSelectedMode()
    {
        var viewModel = new MainWindowViewModel(new Settings())
        {
            ReviewModeIndex = 0
        };
        Assert.That(viewModel.ReviewModeInfoTooltip, Does.Contain("Bitbucket pull request"));

        viewModel.ReviewModeIndex = 1;
        Assert.That(viewModel.ReviewModeInfoTooltip, Does.Contain("committed changes"));

        viewModel.ReviewModeIndex = 2;
        Assert.That(viewModel.ReviewModeInfoTooltip, Does.Contain("uncommitted"));
    }

    [Test]
    public void ScanScopeInfoTooltipReflectsSelectedScope()
    {
        var viewModel = new MainWindowViewModel(new Settings())
        {
            ScanScopeIndex = 0
        };
        Assert.That(viewModel.ScanScopeInfoTooltip, Does.Contain("newly added lines"));

        viewModel.ScanScopeIndex = 1;
        Assert.That(viewModel.ScanScopeInfoTooltip, Does.Contain("entire modified files"));
    }

    [Test]
    public void StatusTextDefaultsToReady()
    {
        var viewModel = new MainWindowViewModel(new Settings());
        Assert.That(viewModel.StatusText, Is.EqualTo("Ready."));
    }

    [Test]
    public void StatusTextWhenSetToNullNormalizesToEmptyString()
    {
        var viewModel = new MainWindowViewModel(new Settings());
        viewModel.StatusText = null;
        Assert.That(viewModel.StatusText, Is.EqualTo(string.Empty));
    }

    [Test]
    public void UpdatePullRequestReviewStateSetsPreviewFieldsAndOpenFlag()
    {
        var viewModel = new MainWindowViewModel(new Settings());
        viewModel.UpdatePullRequestReviewState("  PR title  ", " open ");

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.PreviewPullRequestTitle, Is.EqualTo("PR title"));
            Assert.That(viewModel.PreviewPullRequestState, Is.EqualTo("open"));
            Assert.That(viewModel.PreviewPullRequestIsOpen, Is.True);
        });
    }

    [Test]
    public void UpdatePullRequestReviewStateWhenStateIsMissingSetsOpenFlagToNull()
    {
        var viewModel = new MainWindowViewModel(new Settings());
        viewModel.UpdatePullRequestReviewState("Title", null);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.PreviewPullRequestTitle, Is.EqualTo("Title"));
            Assert.That(viewModel.PreviewPullRequestState, Is.Null);
            Assert.That(viewModel.PreviewPullRequestIsOpen, Is.Null);
        });
    }

    [Test]
    public void PreviewPullRequestStateDisplayWhenStateMissingReturnsNa()
    {
        var viewModel = new MainWindowViewModel(new Settings());
        viewModel.UpdatePullRequestReviewState("Title", null);
        Assert.That(viewModel.PreviewPullRequestStateDisplay, Is.EqualTo("N/A"));
    }

    [Test]
    public void UpdatePullRequestMetadataPreviewFormatsSummaryText()
    {
        var viewModel = new MainWindowViewModel(new Settings());
        viewModel.UpdatePullRequestMetadataPreview("  Title ", "  Dean ", " open ");

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.PreviewPullRequestTitle, Is.EqualTo("Title"));
            Assert.That(viewModel.PreviewPullRequestStateDisplay, Is.EqualTo("OPEN"));
            Assert.That(viewModel.PullRequestMetadataText, Is.EqualTo("Title: Title | Author: Dean | State: OPEN"));
        });
    }

    [Test]
    public void UpdatePullRequestMetadataPreviewWhenAllValuesMissingClearsSummaryText()
    {
        var viewModel = new MainWindowViewModel(new Settings());
        viewModel.UpdatePullRequestMetadataPreview("Title", "Dean", "OPEN");

        viewModel.UpdatePullRequestMetadataPreview(null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(viewModel.PullRequestMetadataText, Is.EqualTo(string.Empty));
            Assert.That(viewModel.PreviewPullRequestTitle, Is.Null);
            Assert.That(viewModel.PreviewPullRequestStateDisplay, Is.EqualTo("N/A"));
            Assert.That(viewModel.PreviewPullRequestIsOpen, Is.Null);
        });
    }
}
