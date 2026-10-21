using ReviewG33k.Services.DiffLens;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class DiffLensNavigationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ClickingAddedLineTargetsItsWorkingLine(bool split)
    {
        var rows = DiffLensRows.Build("before\nafter", "before\ninserted\nafter", 0, split);
        var index = rows.ToList().FindIndex(row => row.Right?.Text == "inserted");
        Assert.That(DiffLensRows.FindWorkingLine(rows, index), Is.EqualTo(2));
    }

    [Test]
    public void ClickingDeletedLineTargetsNextSurvivingWorkingLine()
    {
        var rows = DiffLensRows.Build("before\ndeleted\nafter", "before\nafter", 4, false);
        var index = rows.ToList().FindIndex(row => row.Left?.Text == "deleted");
        Assert.That(DiffLensRows.FindWorkingLine(rows, index), Is.EqualTo(2));
    }

    [Test]
    public void ClickingDeletedEndTargetsPreviousSurvivingWorkingLine()
    {
        var rows = DiffLensRows.Build("before\ndeleted", "before", 4, false);
        var index = rows.ToList().FindIndex(row => row.Left?.Text == "deleted");
        Assert.That(DiffLensRows.FindWorkingLine(rows, index), Is.EqualTo(1));
    }
}
