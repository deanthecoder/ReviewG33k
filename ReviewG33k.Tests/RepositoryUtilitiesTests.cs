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

public sealed class RepositoryUtilitiesTests
{
    [Test]
    public void FindTopLevelSolutionFileWhenRootContainsSolutionPrefersTopLevelFile()
    {
        using var tempRoot = new TempDirectory();
        tempRoot.GetFile("Root.sln").WriteAllText(string.Empty);
        tempRoot.GetDir("src/App").Create();
        tempRoot.GetDir("src/App").GetFile("Nested.sln").WriteAllText(string.Empty);

        var solutionPath = RepositoryUtilities.FindTopLevelSolutionFile(tempRoot.FullName);

        Assert.That(solutionPath, Is.EqualTo(tempRoot.GetFile("Root.sln").FullName));
    }

    [Test]
    public void FindTopLevelSolutionFileWhenOnlyBinContainsSolutionIgnoresGeneratedFolder()
    {
        using var tempRoot = new TempDirectory();
        tempRoot.GetDir("bin/Debug").Create();
        tempRoot.GetDir("bin/Debug").GetFile("Generated.sln").WriteAllText(string.Empty);
        tempRoot.GetDir("src/App").Create();
        tempRoot.GetDir("src/App").GetFile("Actual.sln").WriteAllText(string.Empty);

        var solutionPath = RepositoryUtilities.FindTopLevelSolutionFile(tempRoot.FullName);

        Assert.That(solutionPath, Is.EqualTo(tempRoot.GetDir("src/App").GetFile("Actual.sln").FullName));
    }
}
