// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using DTC.Core;
using DTC.Core.Extensions;
using ReviewG33k.Services;

namespace ReviewG33k.Tests;

public sealed class LocalRepositoryChangedFileSourceTests
{
    [Test]
    public async Task LoadAsyncWhenIgnoredDirectoriesContainSourceFilesSkipsThem()
    {
        using var tempRoot = new TempDirectory();
        tempRoot.GetDir("src/App").Create();
        tempRoot.GetDir("bin/Debug").Create();
        tempRoot.GetDir("dist/output").Create();

        tempRoot.GetDir("src/App").GetFile("Worker.cs").WriteAllText("public sealed class Worker { }");
        tempRoot.GetDir("bin/Debug").GetFile("Generated.cs").WriteAllText("public sealed class Generated { }");
        tempRoot.GetDir("dist/output").GetFile("Built.cs").WriteAllText("public sealed class Built { }");

        var source = new LocalRepositoryChangedFileSource(tempRoot.FullName);

        var result = await source.LoadAsync();

        Assert.That(result.Files.Select(file => file.Path).ToArray(), Is.EqualTo(new[] { "src/App/Worker.cs" }));
    }

    [Test]
    public async Task LoadAsyncWhenGitRepositoryUsesGitEnumerationAndStillSkipsIgnoredDirectories()
    {
        using var tempRoot = new TempDirectory();
        tempRoot.GetDir("src/App").Create();
        tempRoot.GetDir("bin/Debug").Create();

        tempRoot.GetDir("src/App").GetFile("Tracked.cs").WriteAllText("public sealed class Tracked { }");
        tempRoot.GetDir("src/App").GetFile("Untracked.cs").WriteAllText("public sealed class Untracked { }");
        tempRoot.GetDir("bin/Debug").GetFile("Generated.cs").WriteAllText("public sealed class Generated { }");

        var gitRunner = new GitCommandRunner();
        await gitRunner.RunAsync(tempRoot.FullName, "init");
        await gitRunner.RunAsync(tempRoot.FullName, "add", "src/App/Tracked.cs");

        var source = new LocalRepositoryChangedFileSource(tempRoot.FullName, gitRunner);

        var result = await source.LoadAsync();

        Assert.That(
            result.Files.Select(file => file.Path).OrderBy(path => path).ToArray(),
            Is.EqualTo(new[] { "src/App/Tracked.cs", "src/App/Untracked.cs" }));
    }
}
