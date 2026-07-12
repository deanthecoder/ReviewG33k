---
name: reviewg33k
description: Review code quality with the ReviewG33k command-line scanner. Use when the user asks to review, check, or scan uncommitted changes, completed feature-branch changes, or a repository using ReviewG33k, including phrases such as "Review my changes using ReviewG33k", "Check my work with ReviewG33k", and "Review this branch before a PR".
---

# Review with ReviewG33k

Run ReviewG33k from the repository root and interpret its machine-readable findings.

## Select the scope

- Use `uncommitted` for work that is not committed yet or for a check during development.
- Use `committed` for a completed feature branch, an end-of-cycle review, or a pre-PR check. Use the user-specified base branch; otherwise discover the repository's default remote branch and fall back to `main`.
- Use `tree` only when the user explicitly requests a whole-repository scan.

## Run the review

Locate the installed `ReviewG33k` executable. Check the command path first, then normal application install locations for the current operating system. If it cannot be found, tell the user to install ReviewG33k and stop.

Run one of:

```text
ReviewG33k --cli --repo <repository-root> --mode uncommitted --json
ReviewG33k --cli --repo <repository-root> --mode committed --base <base-branch> --json
ReviewG33k --cli --repo <repository-root> --mode tree --json
```

Exit code `0` means no findings. Exit code `1` means findings were produced and is a successful scan. Exit code `2` means the scan failed; parse the JSON error from standard error and report it without treating it as review feedback.

## Handle findings

Parse JSON only from standard output. The response has `schemaVersion`, review context, `summary`, and `findings`. Each finding contains `ruleId`, `severity`, `category`, `file`, `line`, and `message`.

Open the reported file and inspect enough surrounding code to confirm the finding is relevant. Present concise, actionable findings ordered by severity. Include clickable file locations when available. Do not reproduce the raw JSON unless the user requests it.

If there are no findings, say that ReviewG33k completed successfully and found none. Do not imply that this proves the code is defect-free.

When the user asks for fixes, implement the selected fixes, run appropriate tests, and rerun the same ReviewG33k scope to confirm the result.
