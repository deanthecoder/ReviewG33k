// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using System.Collections.Generic;

namespace ReviewG33k.Services;

public sealed class CodeSmellReport
{
    private readonly List<string> m_info = [];
    private readonly List<CodeSmellFinding> m_findings = [];

    public IReadOnlyList<string> Info => m_info;

    public IReadOnlyList<CodeSmellFinding> Findings => m_findings;

    public void AddInfo(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            m_info.Add(message.Trim());
    }

    public void AddFinding(CodeReviewFindingSeverity severity, string ruleId, string filePath, int lineNumber, string message)
    {
        m_findings.Add(new CodeSmellFinding(severity, ruleId, filePath, lineNumber, message));
    }
}