// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using System.Text;
using ReviewG33k.Services;
using ReviewG33k.Services.DiffLens;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class DiffLensTests
{
    private string m_root;
    private readonly GitCommandRunner m_git = new();

    [SetUp]
    public async Task SetUp()
    {
        m_root = Path.Combine(Path.GetTempPath(), "ReviewG33k-DiffLens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(m_root);
        await Git("init", "-b", "main");
        await Git("config", "user.name", "DiffLens Tests");
        await Git("config", "user.email", "difflens@example.invalid");
        await Git("config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(m_root, "sample.cs"), "class Sample\n{\n    int Value = 1;\n}\n");
        await Git("add", ".");
        await Git("commit", "-m", "Baseline");
        await Git("switch", "-c", "feature/test");
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var file in Directory.EnumerateFiles(m_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(m_root, true);
    }

    [Test]
    public async Task LoadSetIncludesDeletedUntrackedAndUnsupportedFilesAgainstOneBase()
    {
        File.Delete(Path.Combine(m_root, "sample.cs"));
        Directory.CreateDirectory(Path.Combine(m_root, "src"));
        await File.WriteAllTextAsync(Path.Combine(m_root, "src", "added.cs"), "new file\n");
        await File.WriteAllBytesAsync(Path.Combine(m_root, "binary.dat"), new byte[] { 0, 1, 2, 0 });
        var set = await new DiffLensFileLoader().LoadSetAsync(m_root, "main");
        Assert.That(set.Entries.Select(entry => entry.Path), Is.EquivalentTo(new[] { "sample.cs", "src/added.cs", "binary.dat" }));
        Assert.That(set.Entries.Single(entry => entry.Path == "binary.dat").Error, Is.Not.Null);
        Assert.That(set.Entries.Single(entry => entry.Path == "sample.cs").File.CanEdit, Is.False);
        Assert.That(set.Entries.Where(entry => entry.File != null).All(entry => entry.File.BaseRevision == set.BaseRevision), Is.True);
    }

    [Test]
    public async Task GivenBranchAndLocalEditsUsesMergeBaseAndCurrentDisk()
    {
        await File.WriteAllTextAsync(Path.Combine(m_root, "sample.cs"), "committed feature\n");
        await Git("commit", "-am", "Feature");
        await Git("switch", "main");
        await File.WriteAllTextAsync(Path.Combine(m_root, "sample.cs"), "later main\n");
        await Git("commit", "-am", "Advance main");
        await Git("switch", "feature/test");
        await File.WriteAllTextAsync(Path.Combine(m_root, "sample.cs"), "staged\n");
        await Git("add", ".");
        await File.WriteAllTextAsync(Path.Combine(m_root, "sample.cs"), "current disk\n");
        var file = await new DiffLensFileLoader().LoadAsync(m_root, "main", "sample.cs");
        Assert.Multiple(() =>
        {
            Assert.That(file.Baseline, Does.Contain("Value = 1"));
            Assert.That(file.WorkingText, Is.EqualTo("current disk\n"));
        });
    }

    [Test]
    public async Task GivenRenameLoadsOriginalBaselinePath()
    {
        await Git("mv", "sample.cs", "renamed file.cs");
        var file = await new DiffLensFileLoader().LoadAsync(m_root, "main", "renamed file.cs");
        Assert.That(file.Baseline, Is.EqualTo(file.WorkingText));
    }

    [Test]
    public async Task GivenDeletedFileRetainsBaselineAndDisablesEditing()
    {
        File.Delete(Path.Combine(m_root, "sample.cs"));
        var file = await new DiffLensFileLoader().LoadAsync(m_root, "main", "sample.cs");
        Assert.Multiple(() =>
        {
            Assert.That(file.Baseline, Does.Contain("Value = 1"));
            Assert.That(file.WorkingText, Is.Empty);
            Assert.That(file.CanEdit, Is.False);
        });
    }

    [Test]
    public async Task GivenUntrackedFileUsesEmptyBaseline()
    {
        await File.WriteAllTextAsync(Path.Combine(m_root, "new.cs"), "new file\n");
        var file = await new DiffLensFileLoader().LoadAsync(m_root, "main", "new.cs");
        Assert.That(file.Baseline, Is.Empty);
        Assert.That(file.WorkingText, Is.EqualTo("new file\n"));
    }

    [Test]
    public void GivenOutsidePathRefusesToOpenIt() =>
        Assert.ThrowsAsync<InvalidOperationException>(async () => await new DiffLensFileLoader().LoadAsync(m_root, "main", "../outside.cs"));

    [Test]
    public async Task GivenExternalEditSaveRefusesAndRetainsExternalContent()
    {
        var file = await new DiffLensFileLoader().LoadAsync(m_root, "main", "sample.cs");
        await File.WriteAllTextAsync(file.FullPath, "agent edit");
        Assert.ThrowsAsync<IOException>(async () => await file.SaveAsync("user edit"));
        Assert.That(await File.ReadAllTextAsync(file.FullPath), Is.EqualTo("agent edit"));
    }

    [Test]
    public async Task GivenUtf16FileSavePreservesEncodingAndCrLfWithoutStaging()
    {
        var encoding = new UnicodeEncoding(false, true, true);
        var path = Path.Combine(m_root, "new.cs");
        await File.WriteAllTextAsync(path, "old\r\n", encoding);
        var file = await new DiffLensFileLoader().LoadAsync(m_root, "main", "new.cs");
        await file.SaveAsync("// TODO: test\r\nold\r\n");
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(encoding.GetPreamble().Concat(encoding.GetBytes("// TODO: test\r\nold\r\n")).ToArray()));
        var staged = await m_git.RunAsync(m_root, "diff", "--cached", "--name-only");
        Assert.That(staged.StandardOutput, Is.Empty);
    }

    [Test]
    public void GivenWhitespaceChangeItIsNotHidden()
    {
        var rows = DiffLensRows.Build("value = 1;", "value  = 1;", 0, true);
        Assert.That(rows.Any(row => row.Changed), Is.True);
    }

    [Test]
    public void GivenContextExpansionSourceLineIdentityIsPreserved()
    {
        var oldText = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}"));
        var newText = oldText.Replace("line 15", "changed line");
        var compact = DiffLensRows.Build(oldText, newText, 0, false);
        var expanded = DiffLensRows.Build(oldText, newText, 4, true);
        Assert.Multiple(() =>
        {
            Assert.That(compact.Any(row => row.NewLine == 15 && row.Right.Text == "changed line"), Is.True);
            Assert.That(expanded.Any(row => row.NewLine == 15 && row.OldLine == 15), Is.True);
            Assert.That(expanded.Count, Is.GreaterThan(compact.Count));
        });
    }

    private async Task Git(params string[] args)
    {
        var result = await m_git.RunAsync(m_root, args);
        Assert.That(result.IsSuccess, Is.True, result.StandardError);
    }
}
