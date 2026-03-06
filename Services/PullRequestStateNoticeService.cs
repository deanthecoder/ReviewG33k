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
using ReviewG33k.Models;

namespace ReviewG33k.Services;

internal sealed class PullRequestStateNoticeService
{
    private string m_lastNonOpenPullRequestNoticeKey;

    public bool TryCreateNonOpenPullRequestNotice(
        BitbucketPullRequestReference pullRequest,
        bool? isPullRequestOpen,
        string pullRequestStateDisplay,
        out string message)
    {
        message = null;

        if (pullRequest == null || isPullRequestOpen != false)
            return false;

        var normalizedState = string.IsNullOrWhiteSpace(pullRequestStateDisplay)
            ? "N/A"
            : pullRequestStateDisplay.Trim();
        var noticeKey = $"{pullRequest.SourceUrl}|{normalizedState}";
        if (string.Equals(noticeKey, m_lastNonOpenPullRequestNoticeKey, StringComparison.Ordinal))
            return false;

        m_lastNonOpenPullRequestNoticeKey = noticeKey;
        message = normalizedState.Equals("MERGED", StringComparison.OrdinalIgnoreCase)
            ? $"PR #{pullRequest.PullRequestId} is MERGED. ReviewG33k will attempt merge-commit fallback."
            : $"PR #{pullRequest.PullRequestId} is {normalizedState}. Review checkout requires an OPEN pull request.";
        return true;
    }
}
