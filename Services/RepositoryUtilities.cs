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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DTC.Core.Extensions;

namespace ReviewG33k.Services;

public static class RepositoryUtilities
{
    private static readonly HashSet<string> SkippedSolutionSearchDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        "bin",
        "obj",
        "dist",
        "node_modules"
    };
    private static readonly ConcurrentDictionary<string, string> TopLevelSolutionFileCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsGitRepository(string directory) =>
        !string.IsNullOrWhiteSpace(directory) &&
        (directory.ToDir().GetDir(".git").Exists() || directory.ToDir().GetFile(".git").Exists());

    public static string FindTopLevelSolutionFile(string rootFolder)
    {
        var normalizedRootFolder = NormalizeRepoPath(rootFolder);
        if (string.IsNullOrWhiteSpace(rootFolder) || !rootFolder.ToDir().Exists())
            return null;

        return TopLevelSolutionFileCache.GetOrAdd(normalizedRootFolder, _ => FindTopLevelSolutionFileCore(rootFolder));
    }

    private static string FindTopLevelSolutionFileCore(string rootFolder)
    {
        var rootDirectory = rootFolder.ToDir();
        var topLevelCandidates = rootDirectory
            .TryGetFiles("*.sln")
            .Select(file => file.FullName)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (topLevelCandidates.Length > 0)
            return topLevelCandidates[0];

        return rootDirectory
            .TryGetFiles("*.sln", SearchOption.AllDirectories)
            .Where(file => !IsInsideSkippedSolutionSearchDirectory(rootDirectory.FullName, file.DirectoryName))
            .Select(file => file.FullName)
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

    private static bool IsInsideSkippedSolutionSearchDirectory(string rootFolder, string candidateDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidateDirectory))
            return false;

        var relativeDirectory = Path.GetRelativePath(rootFolder, candidateDirectory);
        if (string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == ".")
            return false;

        var segments = relativeDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (SkippedSolutionSearchDirectories.Contains(segment))
                return true;
        }

        return false;
    }
}
