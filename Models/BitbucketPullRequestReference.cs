namespace ReviewG33k.Models;

public sealed class BitbucketPullRequestReference
{
    public BitbucketPullRequestReference(string host, string projectKey, string repoSlug, int pullRequestId, string sourceUrl)
    {
        Host = host;
        ProjectKey = projectKey;
        RepoSlug = repoSlug;
        PullRequestId = pullRequestId;
        SourceUrl = sourceUrl;
    }

    public string Host { get; }

    public string ProjectKey { get; }

    public string RepoSlug { get; }

    public int PullRequestId { get; }

    public string SourceUrl { get; }

    public string CloneUrl => $"https://{Host}/scm/{ProjectKey.ToLowerInvariant()}/{RepoSlug}.git";
}
