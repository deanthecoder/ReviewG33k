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
using System.IO;
using System.Linq;
using DTC.Core.Extensions;

namespace ReviewG33k.Services;

public static class RepositoryUtilities
{
    public static bool IsGitRepository(string directory) =>
        !string.IsNullOrWhiteSpace(directory) &&
        (directory.ToDir().GetDir(".git").Exists() || directory.ToDir().GetFile(".git").Exists());

    public static string FindTopLevelSolutionFile(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !rootFolder.ToDir().Exists())
            return null;

        return Directory
            .EnumerateFiles(rootFolder, "*.sln", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                Depth = GetDepth(Path.GetRelativePath(rootFolder, path))
            })
            .OrderBy(candidate => candidate.Depth)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    public static bool AreSameRepoPath(string leftPath, string rightPath)
    {
        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
            return false;

        return string.Equals(NormalizeRepoPath(leftPath), NormalizeRepoPath(rightPath), StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeRepoPath(string path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim();

    private static int GetDepth(string relativePath)
    {
        var depth = 0;
        foreach (var character in relativePath ?? string.Empty)
        {
            if (character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar)
                depth++;
        }

        return depth;
    }
}
