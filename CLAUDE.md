# BobarCode: rules for every session

A lightweight Windows tool: `bobar-cli.exe` runs a review loop between a reviewer model and the live Claude Code session (the author), and `bobar.exe` (Avalonia) shows runs and collects the user's final decisions. Full spec: `spec.md`. Verified CLI flags, provider quirks and prices: `INFRA_FINDINGS.md` (keep it current when you learn something new).

## Keep it light
- The user wants this lightweight, not airtight or enterprise-grade. Don't add sandboxes, worktrees, snapshot gates, anonymization, judges, token caps or guards unless asked.
- No daemon. The run dir on disk is the only channel between the CLI and the GUI. The GUI never starts reviewers.

## Hard rules
- When the loop stops is decided by code (spec §5), from validated JSON. Never by a model.
- JSON is the source of truth; Markdown is rendered from it. `events.jsonl` is append-only; state is folded from it.
- Automated tests use `FakeReviewer` only. Real model calls happen only in `scripts/smoke.ps1`, with the cheapest reviewer (Claude: `--model haiku`) unless a specific model is under test.
- Spawn processes with `ArgumentList`, never command strings. Kill whole trees on cancel/timeout. Prompts go over stdin.
- Reviewers get read-only tools, never a shell.
- Claude child processes: delete `ANTHROPIC_API_KEY`, `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`. Never `--bare`. Assert `apiKeySource == "none"`.
- Never print or log secrets (`keys.env`). Everything written to the run dir goes through the redactor.
- Cost = tokens × the config price table. Vendor cost fields are ignored.
- Never touch the user's git index, stash or branches (snapshots use a temp index, spec §6). Never commit or push in target repos.

## Stack
.NET 11, C#, Avalonia 11 with XAML and compiled bindings, CommunityToolkit.Mvvm (no ReactiveUI), System.Text.Json source generation, System.CommandLine, xUnit.

## Workflow
Milestones M1–M4 (spec §14). Commit after each with `dotnet build` and `dotnet test` green, then stop for review.
