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
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReviewG33k.Models;

namespace ReviewG33k.Services;

public sealed class BitbucketPullRequestMetadataClient : IDisposable
{
    private readonly HttpClient m_httpClient;
    private readonly ConcurrentDictionary<string, BitbucketPullRequestBranchInfo> m_branchCache = new(StringComparer.OrdinalIgnoreCase);

    public BitbucketPullRequestMetadataClient()
    {
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true
        };

        m_httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    public async Task<BitbucketPullRequestBranchInfo> TryGetBranchInfoAsync(BitbucketPullRequestReference pullRequest, CancellationToken cancellationToken = default)
    {
        if (pullRequest == null)
            return null;

        if (m_branchCache.TryGetValue(pullRequest.SourceUrl, out var cachedInfo))
            return cachedInfo;

        var apiUrl = BuildApiUrl(pullRequest);

        HttpResponseMessage response;
        try
        {
            response = await m_httpClient.GetAsync(apiUrl, cancellationToken);
        }
        catch
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
            return null;

        string json;
        try
        {
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return null;
        }

        if (!TryParseBranchInfo(json, out var branchInfo))
            return null;

        m_branchCache[pullRequest.SourceUrl] = branchInfo;
        return branchInfo;
    }

    public void Dispose() => m_httpClient.Dispose();

    private static string BuildApiUrl(BitbucketPullRequestReference pullRequest) =>
        $"https://{pullRequest.Host}/rest/api/1.0/projects/{Uri.EscapeDataString(pullRequest.ProjectKey)}/repos/{Uri.EscapeDataString(pullRequest.RepoSlug)}/pull-requests/{pullRequest.PullRequestId}";

    private static bool TryParseBranchInfo(string json, out BitbucketPullRequestBranchInfo branchInfo)
    {
        branchInfo = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var sourceBranch = GetBranchName(root, "fromRef");
            var targetBranch = GetBranchName(root, "toRef");

            if (string.IsNullOrWhiteSpace(sourceBranch) || string.IsNullOrWhiteSpace(targetBranch))
                return false;

            branchInfo = new BitbucketPullRequestBranchInfo(sourceBranch, targetBranch);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetBranchName(JsonElement root, string refPropertyName)
    {
        if (!root.TryGetProperty(refPropertyName, out var refElement) || refElement.ValueKind != JsonValueKind.Object)
            return null;

        if (refElement.TryGetProperty("displayId", out var displayIdElement) && displayIdElement.ValueKind == JsonValueKind.String)
            return displayIdElement.GetString();

        if (refElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            return TrimGitRefPrefix(idElement.GetString());

        return null;
    }

    private static string TrimGitRefPrefix(string refName)
    {
        if (string.IsNullOrWhiteSpace(refName))
            return refName;

        const string headsPrefix = "refs/heads/";
        if (refName.StartsWith(headsPrefix, StringComparison.OrdinalIgnoreCase))
            return refName[headsPrefix.Length..];

        return refName;
    }
}
