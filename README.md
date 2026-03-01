[![Twitter URL](https://img.shields.io/twitter/url/https/twitter.com/deanthecoder.svg?style=social&label=Follow%20%40deanthecoder)](https://twitter.com/deanthecoder)

<p align="center">
  <img src="img/logo.png" alt="ReviewG33k Logo">
</p>

# ReviewG33k
ReviewG33k is a lightweight Avalonia desktop app for fast, local code reviews. It can prepare isolated Bitbucket pull-request checkouts, run automated checks against the changed code, and jump straight to findings in VS Code.

![Screenshot](img/ReviewG33k.png)

## Two review modes
- **Bitbucket PR reviews**: Paste/drop a Bitbucket PR URL. ReviewG33k prepares an isolated `git worktree` under a `CodeReview` folder, opens the solution, and can post inline comments back to the PR.
- **Local committed-change reviews**: Point ReviewG33k at a local repo folder and base branch (default `main`) to review `origin/<base>...HEAD` before you raise a PR. This mode does not require Bitbucket access and does not post PR comments.

## Highlights
- **Paste or drop PR URL**: Supports Bitbucket PR links directly from clipboard or drag/drop.
- **PR branch preview**: Uses Bitbucket REST API to show `source -> target` branch names in the preview line.
- **Canonical PR URLs**: Normalizes links by stripping `/overview` and query-string noise.
- **Smart local repo matching**: Reuses your existing local repository when available.
- **Clone on demand**: Clones missing repos automatically into your chosen repo root.
- **Isolated PR worktrees**: Uses `git worktree` (`CodeReview/<repo>/PR-<id>`) so your main working tree stays untouched.
- **Fast handoff to IDE**: Finds a `.sln` and opens it with your default solution app.
- **Open findings in VS Code**: One click to jump to `file:line` (requires the `code` CLI).
- **Local quick-fixes**: Some findings can be auto-fixed in-place when reviewing a local repo (for example, removing unused `using` directives).
- **Codex prompt copy button**: For local findings that do not have an auto-fix, copy a ready-to-paste Codex prompt for that exact issue.
- **Export to clipboard**: Copy findings as text to paste into Codex (or similar) to help automate fixes.
- **Opinionated automated checks**: Flags common pitfalls (async, exceptions, test hygiene, suppressions, and more) on the changed lines.

## Automated checks (highlights)
ReviewG33k runs a set of checks against changed files/lines. A few of the most useful ones are:
- **Async footguns**: flags `async void` and `Task.Run(async ...)` patterns that tend to hide bugs.
- **Unobserved tasks**: catches fire-and-forget `Task` calls where the result is ignored.
- **Exception mistakes**: flags `throw ex;` and suspicious/empty catch blocks.
- **Warning suppressions**: highlights newly added `#pragma warning disable` and `[SuppressMessage]` usage.
- **Test gates**: nudges when new code appears without corresponding test changes (including newly added public methods).
- **Dead code cleanup**: detects unused `using`s and unused private members.

And more: `IDisposable` not disposed, multiple enumeration, public mutable static state, lock targets, constructor length, parameter count, brace consistency, and other small "paper cut" checks.

## Review results UX
- **Preview**: Shows surrounding file content around the selected finding.
- **Open Selected**: Opens the selected finding in VS Code at the right line.
- **Fix/Codex actions (local mode)**: Use the wand **Fix** button for auto-fixable findings, or **Codex** to copy a focused prompt.
- **Export To Clipboard**: Copies the findings text (included items by default).
- **Triage tools**: Tick/untick findings, bulk toggle, and "untick all of this rule id".
- **Optional PR commenting**: When a Bitbucket PR is in context, findings can be posted as inline PR comments.

## Typical workflow
### PR review mode
1. Set your repo root folder (for example, `C:\source`).
2. Paste/drop a Bitbucket PR URL.
3. Click **Prepare Review Checkout**.
4. Review findings, open them in VS Code, and optionally comment back to the PR.

### Local committed-change mode
1. Choose **Local committed review**.
2. Select a local repository folder and base branch (for example, `main`).
3. Run the review and iterate before opening a PR. Use **Fix** where available, or **Codex** / **Export To Clipboard** for AI-assisted fixes.

## Build and run
```bash
dotnet build ReviewG33k.sln
dotnet run --project ReviewG33k.csproj
```

## Notes
- Requires `git` on `PATH`.
- For VS Code integration, install the `code` CLI (in VS Code: Command Palette, "Shell Command: Install 'code' command in PATH").
- Clone URL format: `https://<host>/scm/<project-lower>/<repo>.git`.
- PR fetch ref: `refs/pull-requests/<id>/from`.

## License
Licensed under the MIT License. See [LICENSE](LICENSE) for details.
