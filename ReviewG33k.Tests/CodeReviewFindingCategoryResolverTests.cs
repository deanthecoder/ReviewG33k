// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using ReviewG33k.Services.Checks;
using ReviewG33k.Services.Checks.Support;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class CodeReviewFindingCategoryResolverTests
{
    [Test]
    public void ResolveCategoryWhenRuleIsKnownReturnsExpectedCategory()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                CodeReviewFindingCategoryResolver.ResolveCategory(CodeReviewRuleIds.TaskRunAsync),
                Is.EqualTo(CodeReviewFindingCategoryResolver.Threading));
            Assert.That(
                CodeReviewFindingCategoryResolver.ResolveCategory(CodeReviewRuleIds.ResxMissingLocaleKeys),
                Is.EqualTo(CodeReviewFindingCategoryResolver.Resources));
            Assert.That(
                CodeReviewFindingCategoryResolver.ResolveCategory(CodeReviewRuleIds.MissingTypedBindingContext),
                Is.EqualTo(CodeReviewFindingCategoryResolver.Ui));
            Assert.That(
                CodeReviewFindingCategoryResolver.ResolveCategory(CodeReviewRuleIds.MissingTests),
                Is.EqualTo(CodeReviewFindingCategoryResolver.Testing));
            Assert.That(
                CodeReviewFindingCategoryResolver.ResolveCategory(CodeReviewRuleIds.PrivateFieldUsedInSingleMethod),
                Is.EqualTo(CodeReviewFindingCategoryResolver.Maintainability));
        });
    }

    [Test]
    public void ResolveCategoryWhenRuleIsUnknownReturnsMaintainability()
    {
        var category = CodeReviewFindingCategoryResolver.ResolveCategory("unknown-check-id");
        Assert.That(category, Is.EqualTo(CodeReviewFindingCategoryResolver.Maintainability));
    }
}
