# BobarCode: rules for every session

A lightweight Windows and macOS tool: `bobar-cli` runs a review loop between a reviewer model and the live Claude Code session (the author), and `bobar` (Avalonia) shows runs and collects the user's final decisions. Full spec: `spec.md`. Verified CLI flags, provider quirks and prices: `INFRA_FINDINGS.md` (keep it current when you learn something new).

## Keep it light
- The user wants this lightweight, not airtight or enterprise-grade. Don't add sandboxes, worktrees, snapshot gates, anonymization, judges, token caps or guards unless asked.
- Assume models follow instructions reasonably accurately. Don't try to make the system "fair" or "just" with regard to one model possibly dominating another. Instead, be biased toward the simpler, cleaner and faster solution that can work.
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
.NET 11, C#, Avalonia 12 with XAML and compiled bindings, CommunityToolkit.Mvvm (no ReactiveUI), System.Text.Json source generation, System.CommandLine, xUnit.

## Workflow
When you finish a task, remind the user that they might want to run `scripts\install.ps1` on Windows or `scripts/install.sh` on macOS (they republish both exes and the skills, and closes the running GUI). Don't run it yourself.

To check visual changes without a screen, render the GUI offscreen: `dotnet run --project tools/Bobar.Shots -- --run <id or prefix> [--zoom 1,2] [--size 1280x2400] [--expand] [--out dir]` prints the PNG paths; read them. It copies the run into a temp data dir, so real runs and `gui.json` are untouched.

If you want to run the Bobar GUI to check some visual changes, but you find it's already running, ask the user whether they want you to start it anew or they prefer that you skip the visual check instead.