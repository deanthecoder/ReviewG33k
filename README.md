[![Twitter URL](https://img.shields.io/twitter/url/https/twitter.com/deanthecoder.svg?style=social&label=Follow%20%40deanthecoder)](https://twitter.com/deanthecoder)

<p align="center">
  <img src="img/logo.png" alt="ReviewG33k Logo">
</p>

# ReviewG33k
ReviewG33k is a lightweight Avalonia desktop app for fast, local code reviews. Give it a Bitbucket PR URL (or a local repo + base branch), run automated checks on the diff, and jump straight to findings in VS Code.

![Screenshot](img/ReviewG33k.png)

## What it can do
- Review **Bitbucket pull requests** (paste/drop a PR URL) and optionally post inline PR comments.
- Review **local committed changes** against a base branch (auto-detected from `origin/HEAD`, for example `main` or `master`) before you raise a PR (no Bitbucket required).
- Switch scan scope between **changed lines** (faster) and **full modified files** (more thorough) for checks that are normally line-scoped.
- Prepare an isolated review checkout using `git worktree` so your working tree stays untouched.
- Run opinionated automated checks on the changed files/lines (async, exceptions, suppressions, test hygiene, and more).
- Open findings at `file:line` in VS Code (requires the `code` CLI); apply a few safe auto-fixes in local mode.
- Export findings to the clipboard and copy a focused Codex prompt for issues that need manual/AI help.

## How it works (high level)
1. You give it either a Bitbucket PR URL or a local repo folder + base branch.
2. It reuses an existing local clone when possible (or clones into your repo root), then creates an isolated review worktree under `CodeReview/...`.
3. It computes the diff (PR source → target, or `origin/<base>...HEAD`) and runs checks against the changed code.
4. It shows findings with context plus actions to open, fix/export, and (for PRs) comment.

## Quick start
- **PR review**: set repo root → paste/drop PR URL → **Prepare Review Checkout** → review findings/open in VS Code → optionally comment back to the PR.
- **Local review**: choose **Local committed review** → pick repo + base branch → run review → fix/export before opening a PR.

## Build and run
Prereqs: .NET 8 SDK and `git`. Optional: VS Code `code` CLI.
```bash
dotnet build ReviewG33k.sln
dotnet run --project ReviewG33k.csproj
```

## Supported checks
### Async and threading
| Check | What it flags |
| --- | --- |
| Async void (non-event handlers) | `async void` methods that are likely to hide failures. |
| Async method naming | `async` methods that do not end with `Async`. |
| Task.Run(async ...) | Async work wrapped in `Task.Run(...)` where it may be unnecessary or risky. |
| Unobserved task results | Fire-and-forget task calls whose result is ignored. |
| Thread.Sleep usage | Blocking sleeps in newly added code paths. |
| Lock targets | `lock(this)` or locks on likely public objects. |

### Exceptions and reliability
| Check | What it flags |
| --- | --- |
| Empty catch blocks | `catch` blocks with no real handling logic. |
| Swallowing catch blocks | `catch` blocks that silently consume exceptions. |
| `throw ex;` in catch blocks | Re-throw patterns that lose original stack trace context. |
| `IDisposable` not disposed | Disposable objects created without clear disposal. |
| Dispose method without `IDisposable` | Types that define `Dispose()` but do not implement `IDisposable`. |
| Constructor event subscription lifecycle | Constructors that subscribe to events without clear unsubscribe/disposal lifecycle. |
| Multiple enumeration | Re-enumerating deferred `IEnumerable` values unexpectedly. |
| Public method argument guards | Missing null guards in newly added public methods. |

### Design and maintainability
| Check | What it flags |
| --- | --- |
| Property can be auto-property | Verbose property patterns that can be simplified to auto-properties. |
| Private get-only property should be field | Private get-only auto-properties better represented as fields. |
| Private property should be field | Simple private properties that are effectively field wrappers. |
| Private field can be readonly | Private fields written only during construction. |
| Method can be static | Instance methods that do not use instance state. |
| Local variable can be const | Local values that never change and can safely be `const`. |
| Unused local variables | Local variables that are declared/assigned but never read. |
| Multiple classes per file | Files that define more than one class (prefer one class per file). |
| Redundant self lookup | Needlessly resolving an object from itself (or equivalent redundant lookup). |
| Public mutable static state | Exposed mutable static fields/properties. |
| Unused private members | Newly added private code that is never used. |
| Unused `using` directives | Newly added imports that are never referenced. |

### Readability and style
| Check | What it flags |
| --- | --- |
| Missing blank lines between methods | Method blocks that run together and reduce readability. |
| High parameter count | Methods/constructors with too many parameters. |
| Generic type name suffix | Generic type names that do not follow expected suffix conventions. |
| If/else brace consistency | Mismatched bracing style between `if` and `else` blocks. |
| Unnecessary if/else braces | Extra braces around simple single-line branches. |
| Large constructors | Constructors doing too much inline setup work. |
| Boolean literal comparison | Comparisons like `== true` / `== false` that can be simplified. |
| Unnecessary casts | Casts that do not change type or behavior. |
| Unnecessary enum member values | Explicit enum values that simply match the default sequential numbering. |
| Unnecessary verbatim string prefix | `@` string prefix where no escaping benefit is used. |
| Repeated string concatenation to same target | 4+ concatenations to the same string target in one block (consider `StringBuilder`). |

### Test and documentation coverage
| Check | What it flags |
| --- | --- |
| Missing XML docs | New public types without XML documentation. |
| Missing unit test updates | New production changes with no corresponding test changes. |
| Missing tests for new public methods | Added public methods without test coverage changes. |
| Missing README for new project | New project additions without an accompanying README. |

### Localization (RESX)
| Check | What it flags |
| --- | --- |
| Missing locale keys | Localized `.resx` files missing keys present in base resources. |
| Unexpected extra locale keys | Localized `.resx` files containing keys not present in base resources. |
| Empty translation values | Localized `.resx` entries with empty/blank translation text. |

### Framework and suppressions
| Check | What it flags |
| --- | --- |
| Missing typed binding context (Avalonia) | XAML bindings without typed context where expected. |
| Warning suppressions | New `#pragma warning disable` or `[SuppressMessage]` suppressions. |

## License
Licensed under the MIT License. See [LICENSE](LICENSE) for details.
