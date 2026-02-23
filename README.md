[![Twitter URL](https://img.shields.io/twitter/url/https/twitter.com/deanthecoder.svg?style=social&label=Follow%20%40deanthecoder)](https://twitter.com/deanthecoder)

# ReviewG33k
A lightweight Avalonia desktop tool that prepares isolated Bitbucket pull-request checkouts for fast local code reviews.

## Purpose
ReviewG33k removes the branch/context juggling from PR reviews.
Instead of touching your existing working tree, it creates a dedicated review checkout using `git worktree` under a `CodeReview` folder.

## Highlights
- **Paste or drop PR URL** - Supports Bitbucket PR links directly from clipboard or drag/drop.
- **Smart local repo matching** - Reuses your existing local repository when available.
- **Clone on demand** - Clones missing repos automatically into your chosen repo root.
- **Isolated review checkout** - Fetches PR source ref and creates a separate worktree (`CodeReview/<repo>/PR-<id>`).
- **Branch-based review state** - Uses a local review branch (`review/pr-<id>`) instead of detached HEAD.
- **Fast handoff to IDE** - Finds the highest-level `.sln` and opens it with your default solution app.
- **Cleanup option** - Clears all `CodeReview` worktrees/folders in one click.
- **Persisted repo root** - Remembers your repo root between sessions.

## Typical workflow
1. Set your repo root folder (for example, `C:\dtcsource` or `C:\ggsource`).
2. Paste/drop a Bitbucket PR URL.
3. Click **Prepare Review Checkout**.
4. Review in the opened solution from the isolated worktree.
5. Click **Clear CodeReview Folder** when done.

## Build and run
```bash
dotnet build ReviewG33k.sln
dotnet run --project ReviewG33k.csproj
```

## Notes
- Clone URL format: `https://<host>/scm/<project-lower>/<repo>.git`
- PR fetch ref: `refs/pull-requests/<id>/from`
- Requires `git` on `PATH`

## License
Licensed under the MIT License. See [LICENSE](LICENSE) for details.
