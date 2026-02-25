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
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReviewG33k.Models;

namespace ReviewG33k.Services;

public sealed class BitbucketPullRequestMetadataClient : IDisposable
{
    private readonly HttpClient m_httpClient;
    private readonly ConcurrentDictionary<string, BitbucketPullRequestMetadata> m_metadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string[]> m_changedPathsCache = new(StringComparer.OrdinalIgnoreCase);

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

    public async Task<BitbucketPullRequestMetadata> TryGetMetadataAsync(BitbucketPullRequestReference pullRequest, CancellationToken cancellationToken = default)
    {
        if (pullRequest == null)
            return null;

        if (m_metadataCache.TryGetValue(pullRequest.SourceUrl, out var cachedInfo))
            return cachedInfo;

        var apiUrl = BuildMetadataApiUrl(pullRequest);
        var json = await TryGetResponseTextAsync(pullRequest, apiUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        if (!TryParseMetadata(json, out var metadata))
            return null;

        m_metadataCache[pullRequest.SourceUrl] = metadata;
        return metadata;
    }

    public async Task<IReadOnlyList<string>> TryGetChangedPathsAsync(BitbucketPullRequestReference pullRequest, CancellationToken cancellationToken = default)
    {
        if (pullRequest == null)
            return [];

        if (m_changedPathsCache.TryGetValue(pullRequest.SourceUrl, out var cachedPaths))
            return cachedPaths;

        var paths = new List<string>();
        var start = 0;

        while (true)
        {
            var apiUrl = BuildChangesApiUrl(pullRequest, start, 500);
            var json = await TryGetResponseTextAsync(pullRequest, apiUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                break;

            if (!TryParseChangesPage(json, paths, out var isLastPage, out var nextPageStart))
                break;

            if (isLastPage || !nextPageStart.HasValue || nextPageStart.Value <= start)
                break;

            start = nextPageStart.Value;
        }

        var uniquePaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        m_changedPathsCache[pullRequest.SourceUrl] = uniquePaths;
        return uniquePaths;
    }

    public async Task<(bool Success, string ErrorMessage)> TryAddInlineCommentAsync(
        BitbucketPullRequestReference pullRequest,
        string path,
        int line,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (pullRequest == null)
            return (false, "Pull request reference is not available.");
        if (line < 1)
            return (false, "Line number must be greater than zero.");
        if (string.IsNullOrWhiteSpace(path))
            return (false, "File path is required.");
        if (string.IsNullOrWhiteSpace(text))
            return (false, "Comment text is required.");

        var apiUrl = BuildCommentsApiUrl(pullRequest);
        var payloadJson = JsonSerializer.Serialize(
            new
            {
                text = text.Trim(),
                anchor = new
                {
                    line,
                    lineType = "ADDED",
                    fileType = "TO",
                    path = NormalizePath(path)
                }
            });

        HttpResponseMessage response;
        try
        {
            response = await SendCommentRequestAsync(apiUrl, payloadJson, cancellationToken);
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }

        if ((response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) &&
            await TryGetGitCredentialsAsync(pullRequest, cancellationToken) is { } gitCredentials)
        {
            response.Dispose();

            try
            {
                response = await SendCommentRequestAsync(
                    apiUrl,
                    payloadJson,
                    cancellationToken,
                    BuildBasicAuthHeader(gitCredentials.Username, gitCredentials.Password));
            }
            catch (Exception exception)
            {
                return (false, exception.Message);
            }
        }

        if (response.IsSuccessStatusCode)
        {
            response.Dispose();
            return (true, null);
        }

        var error = await TryReadErrorAsync(response, cancellationToken);
        response.Dispose();
        return (false, string.IsNullOrWhiteSpace(error) ? $"Bitbucket returned {(int)response.StatusCode}." : error);
    }

    public void Dispose() => m_httpClient.Dispose();

    private async Task<HttpResponseMessage> SendCommentRequestAsync(
        string apiUrl,
        string payloadJson,
        CancellationToken cancellationToken,
        AuthenticationHeaderValue authHeader = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };

        if (authHeader != null)
            request.Headers.Authorization = authHeader;

        return await m_httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<string> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("errors", out var errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array)
            {
                var messages = errorsElement
                    .EnumerateArray()
                    .Select(error => error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString()
                        : null)
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .ToArray();

                if (messages.Length > 0)
                    return string.Join(" ", messages);
            }

            return raw.Trim();
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> TryGetResponseTextAsync(BitbucketPullRequestReference pullRequest, string apiUrl, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await m_httpClient.GetAsync(apiUrl, cancellationToken);
        }
        catch
        {
            return null;
        }

        if ((response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) &&
            await TryGetGitCredentialsAsync(pullRequest, cancellationToken) is { } gitCredentials)
        {
            response.Dispose();

            try
            {
                using var authenticatedRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                authenticatedRequest.Headers.Authorization = BuildBasicAuthHeader(gitCredentials.Username, gitCredentials.Password);
                response = await m_httpClient.SendAsync(authenticatedRequest, cancellationToken);
            }
            catch
            {
                return null;
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            return null;
        }

        string json;
        try
        {
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
        finally
        {
            response.Dispose();
        }

        return json;
    }

    private static string BuildMetadataApiUrl(BitbucketPullRequestReference pullRequest) =>
        $"https://{pullRequest.Host}/rest/api/1.0/projects/{Uri.EscapeDataString(pullRequest.ProjectKey)}/repos/{Uri.EscapeDataString(pullRequest.RepoSlug)}/pull-requests/{pullRequest.PullRequestId}";

    private static string BuildChangesApiUrl(BitbucketPullRequestReference pullRequest, int start, int limit) =>
        $"https://{pullRequest.Host}/rest/api/1.0/projects/{Uri.EscapeDataString(pullRequest.ProjectKey)}/repos/{Uri.EscapeDataString(pullRequest.RepoSlug)}/pull-requests/{pullRequest.PullRequestId}/changes?start={start}&limit={limit}";

    private static string BuildCommentsApiUrl(BitbucketPullRequestReference pullRequest) =>
        $"https://{pullRequest.Host}/rest/api/1.0/projects/{Uri.EscapeDataString(pullRequest.ProjectKey)}/repos/{Uri.EscapeDataString(pullRequest.RepoSlug)}/pull-requests/{pullRequest.PullRequestId}/comments";

    private static string NormalizePath(string path) => (path ?? string.Empty).Replace('\\', '/');

    private static bool TryParseMetadata(string json, out BitbucketPullRequestMetadata metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var sourceBranch = GetBranchName(root, "fromRef");
            var targetBranch = GetBranchName(root, "toRef");
            var title = GetTitle(root);
            var author = GetAuthor(root);
            var updatedAt = GetUpdatedAt(root);
            var state = GetState(root);

            metadata = new BitbucketPullRequestMetadata(sourceBranch, targetBranch, title, author, updatedAt, state);
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

    private static string GetTitle(JsonElement root)
    {
        if (root.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String)
            return titleElement.GetString();

        return string.Empty;
    }

    private static string GetAuthor(JsonElement root)
    {
        if (!root.TryGetProperty("author", out var authorElement) || authorElement.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (!authorElement.TryGetProperty("user", out var userElement) || userElement.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (userElement.TryGetProperty("displayName", out var displayNameElement) && displayNameElement.ValueKind == JsonValueKind.String)
            return displayNameElement.GetString();

        if (userElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            return nameElement.GetString();

        return string.Empty;
    }

    private static DateTimeOffset? GetUpdatedAt(JsonElement root)
    {
        if (!root.TryGetProperty("updatedDate", out var updatedDateElement))
            return null;

        if (updatedDateElement.ValueKind == JsonValueKind.Number && updatedDateElement.TryGetInt64(out var updatedDateUnixMs))
            return DateTimeOffset.FromUnixTimeMilliseconds(updatedDateUnixMs);

        if (updatedDateElement.ValueKind == JsonValueKind.String &&
            long.TryParse(updatedDateElement.GetString(), out updatedDateUnixMs))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(updatedDateUnixMs);
        }

        return null;
    }

    private static string GetState(JsonElement root)
    {
        if (root.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.String)
            return stateElement.GetString();

        return string.Empty;
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

    private static bool TryParseChangesPage(string json, List<string> paths, out bool isLastPage, out int? nextPageStart)
    {
        isLastPage = true;
        nextPageStart = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("isLastPage", out var isLastPageElement) &&
                (isLastPageElement.ValueKind == JsonValueKind.True || isLastPageElement.ValueKind == JsonValueKind.False))
            {
                isLastPage = isLastPageElement.GetBoolean();
            }

            if (root.TryGetProperty("nextPageStart", out var nextPageStartElement) &&
                nextPageStartElement.ValueKind == JsonValueKind.Number &&
                nextPageStartElement.TryGetInt32(out var nextStart))
            {
                nextPageStart = nextStart;
            }

            if (!root.TryGetProperty("values", out var valuesElement) || valuesElement.ValueKind != JsonValueKind.Array)
                return true;

            foreach (var changeElement in valuesElement.EnumerateArray())
            {
                if (TryGetChangedPath(changeElement, out var path))
                    paths.Add(path);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetChangedPath(JsonElement changeElement, out string path)
    {
        path = null;

        if (!changeElement.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.Object)
            return false;

        if (pathElement.TryGetProperty("toString", out var toStringElement) && toStringElement.ValueKind == JsonValueKind.String)
        {
            path = toStringElement.GetString();
            return !string.IsNullOrWhiteSpace(path);
        }

        if (!pathElement.TryGetProperty("components", out var componentsElement) || componentsElement.ValueKind != JsonValueKind.Array)
            return false;

        var components = componentsElement
            .EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(c => c.GetString())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToArray();

        if (components.Length == 0)
            return false;

        path = string.Join("/", components);
        return true;
    }

    private static AuthenticationHeaderValue BuildBasicAuthHeader(string username, string password)
    {
        var combined = $"{username}:{password}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(combined));
        return new AuthenticationHeaderValue("Basic", encoded);
    }

    private static async Task<(string Username, string Password)?> TryGetGitCredentialsAsync(BitbucketPullRequestReference pullRequest, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("credential");
        startInfo.ArgumentList.Add("fill");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch
        {
            return null;
        }

        var input = new StringBuilder()
            .AppendLine("protocol=https")
            .AppendLine($"host={pullRequest.Host}")
            .AppendLine($"path=scm/{pullRequest.ProjectKey.ToLowerInvariant()}/{pullRequest.RepoSlug}.git")
            .AppendLine()
            .ToString();

        try
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }
        catch
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            return null;

        var output = await outputTask;
        _ = await errorTask;

        if (!TryParseCredentialOutput(output, out var username, out var password))
            return null;

        return (username, password);
    }

    private static bool TryParseCredentialOutput(string output, out string username, out string password)
    {
        username = null;
        password = null;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = rawLine.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            var key = rawLine[..separatorIndex].Trim();
            var value = rawLine[(separatorIndex + 1)..].Trim();

            if (key.Equals("username", StringComparison.OrdinalIgnoreCase))
                username = value;
            else if (key.Equals("password", StringComparison.OrdinalIgnoreCase))
                password = value;
        }

        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
    }
}
