// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

namespace ReviewG33k.Services;

public static class CodeReviewRuleIds
{
    public const string AsyncVoid = "async-void";
    public const string AsyncMethodNameSuffix = "async-method-name-suffix";
    public const string PublicMethodArgumentGuards = "public-method-argument-guards";
    public const string EmptyCatch = "empty-catch";
    public const string SwallowingCatch = "swallowing-catch";
    public const string LockThisOrPublic = "lock-this-or-public";
    public const string TaskRunAsync = "task-run-async";
    public const string MissingXmlDocs = "missing-xml-docs";
    public const string MissingTests = "missing-tests";
}
