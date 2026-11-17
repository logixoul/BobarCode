# BobarCode: spec

Working name. BobarCode replaces Fractory's review workflow with something much lighter.

## 1. What it is

A Windows tool with two exes built from one codebase. It runs a review loop between a **reviewer model** and the **author**: the live Claude Code session that wrote the code.

- `bobar-cli.exe`: headless. Claude calls it from the `/lx-review` skill. It does all the work: it runs the reviewer, records everything and decides when the loop ends.
- `bobar.exe`: the desktop app. You open it from the Start menu like any other app. It shows what is going on in all runs, and it is where you decide the disagreements left at the end. It never starts reviewers and never owns a run. If it isn't open, nothing breaks.

**There is no daemon.** The run directory on disk is the only channel between the two. The CLI appends events and writes files; the GUI watches the directory and redraws.

### Design principles
- **Lightweight.** No sandboxes, worktrees, snapshot gates, anonymization or judges. The author edits your working tree directly, the reviewer runs in your repo with read-only tools, and nothing is guarded beyond that.
- **The CLI owns the loop.** When to stop is decided by the code in §5, from the structured outputs. No model decides it.
- **JSON is the source of truth.** Every model output is validated JSON stored in the run dir. Markdown is only ever rendered from it.
- **Automated tests never call real models.** They use `FakeReviewer`. Real calls happen only in an opt-in smoke script.

### Out of scope
- Implementing features from a prompt: you do that in your Claude Code session.
- Reviewing committed work (`--base`): dropped.
- Running build/test checks: the author's session runs them with its own tools.
- Token or cost caps: the 5-round cap is the only limit.
- Non-Windows platforms (Avalonia keeps the door open, but nothing is promised).
- Fractory itself. Renaming its old flow to `review-legacy` is a separate task in the Fractory repo.

## 2. The loop as you see it

1. You finish a change in Claude Code, uncommitted.
2. You type `/lx-review ds`.
3. Claude writes a short description of the change and calls `bobar-cli review start --reviewer ds --desc -`.
4. The reviewer (DeepSeek through Pi) reviews the uncommitted changes and returns findings.
5. Claude decides fix or reject for each finding, applies the fixes, and calls `bobar-cli review next <run> --response -` with its reasoning.
6. The reviewer re-reviews the new state. It sees the author's response, can withdraw or maintain the findings the author rejected, and can raise new ones.
7. Steps 5–6 repeat until the loop ends (§5): there are no findings, the author and reviewer are deadlocked, or the round cap is hit.
8. If disagreements are left, the CLI opens the GUI on the run. There you mark each one *fix it* or *dismiss* and optionally add a comment, then press Submit once. Claude, which was waiting on `bobar-cli review wait <run>`, gets your decisions and acts on them.

## 3. Repository layout

```
BobarCode.sln
Directory.Build.props          net10.0, nullable, warnings as errors, LangVersion latest
src/Bobar.Core/             class library: everything except UI and argv parsing
src/Bobar.Cli/              console exe, AssemblyName bobar-cli
src/Bobar.App/              Avalonia WinExe, AssemblyName bobar
tests/Bobar.Core.Tests/     xUnit; FakeReviewer only
skill/lx-review/SKILL.md       the Claude Code skill (installed to ~/.claude/skills/lx-review/)
scripts/smoke.ps1              opt-in real-model smoke test (cheapest reviewer by default)
```

**Stack:** .NET 10, C#, Avalonia 11 (XAML, compiled bindings on by default via `AvaloniaUseCompiledBindingsByDefault`), CommunityToolkit.Mvvm, System.Text.Json with source-generated contexts, System.CommandLine for the CLI.

**Publish:** `dotnet publish` of both exes, self-contained single-file for win-x64, into one folder (`dist/`). Install = copy the folder, add it to `PATH` (so Claude can call `bobar-cli`), add a Start menu shortcut for `bobar.exe`. A `scripts/install.ps1` does all three.

## 4. Data and config locations

| What | Where |
|---|---|
| Config (user-editable) | `%APPDATA%\BobarCode\config.json` (written with defaults on first use) |
| Secrets | `%APPDATA%\BobarCode\keys.env` (`NAME=value` lines; never printed or logged) |
| Runs | `%LOCALAPPDATA%\BobarCode\runs\<runId>\` |
| Pi's isolated agent dir | `%LOCALAPPDATA%\BobarCode\pi-agent\` (`settings.json`, `models.json`) |
| Pi sessions | `%LOCALAPPDATA%\BobarCode\pi-sessions\` |

`runId` = `yyyyMMdd-HHmmss-<repo dir name>-<4 hex>`, e.g. `20261117-142301-Fractory-a7f2`.

### Run directory
```
<runId>/
  events.jsonl           append-only; the run's source of truth
  run.lock               held (OS file lock) by the CLI process while it works on the run; contains its pid
  desc.md                the description of the change
  round-1/
    tree.txt             git tree sha of the working state this round reviewed (§6)
    diff.patch           base..tree: everything uncommitted
    delta.patch          previous round's tree..this tree (round ≥ 2 only)
    history.md           earlier rounds' findings and author responses, rendered (round ≥ 2 only)
    prompt.md            the exact prompt sent to the reviewer
    reviewer.jsonl       raw reviewer stream (for the Details modal)
    review.json          validated reviewer output plus CLI-assigned ids
    response.json        validated author response
  round-2/ …
  decisions.json         your decisions (written by the GUI)
```
State is never stored separately: both exes rebuild it by folding `events.jsonl` (one shared `RunState.Fold` in Core).

### config.json
```jsonc
{
  "maxRounds": 5,
  "reviewerTimeoutMinutes": 30,
  "openGuiOnDecisions": true,
  "reviewers": {
    "ds":   { "kind": "pi", "provider": "lithos", "model": "deepseek-ai/DeepSeek-V4.1-Flash-ultra", "thinking": "high", "env": ["LITHOS_API_KEY"] },
    "sol":  { "kind": "pi", "provider": "openai", "model": "gpt-6.1-sol", "thinking": "medium", "env": ["OPENAI_API_KEY"] },
    "opus": { "kind": "claude", "model": "opus", "effort": "medium" }
  },
  "prices": {   // USD per 1M tokens; sources and caveats in INFRA_FINDINGS.md §1
    "lithos/deepseek-ai/DeepSeek-V4.1-Flash-ultra": { "input": 0.35, "output": 1.4, "cacheRead": 0.007 },
    "openai/gpt-6.1-sol": { "input": 2, "output": 10, "cacheRead": 0.1 }
  }
}
```
- A reviewer's **shorthand is its key** (`ds`, `sol`, `opus`). Add more by adding keys.
- `env` lists the `keys.env` entries passed to that reviewer's process, and only those.
- A `claude` reviewer on the subscription costs nothing in BobarCode's books. A missing price shows the cost as "unknown", never a guess.
- The config file is read fresh on every CLI call. The GUI re-reads it when it changes.

## 5. The loop: exact rules

Round *k* = one reviewer call plus one author response.

**Review k:**
- **k = 1:** the reviewer gets `desc.md` and `diff.patch`.
- **k ≥ 2:** it also gets `delta.patch` (what changed since its last review) and `history.md` (all earlier findings, each with the author's action and reasoning).

Each finding returned may carry `previousId`, meaning "this is my earlier finding X again". Every earlier finding the author rejected that the reviewer does not repeat counts as **withdrawn**.

**Stopping**, checked after each step:

| When | Condition | Result |
|---|---|---|
| after review k | 0 findings | **done: clean** |
| after response k | k = `maxRounds` and ≥ 1 rejected | **awaiting decisions** (cap) |
| after response k | k = `maxRounds` and 0 rejected | **done: cap** (the last fixes are not re-reviewed; the summary says so) |
| after response k | author accepted ≥ 1 | next round |
| after response k | author accepted 0, and every finding in round k is **maintained** (its `previousId` points to a finding the author rejected in round k−1) | **awaiting decisions** (deadlock) |
| after response k | author accepted 0, and some finding is new (or repeats a previously *accepted* one) | next round, so the reviewer can defend the newly rejected ones |

Severities are `bug | risk | design | nit`, and all of them count, nits included.

**Awaiting decisions:** the panel lists the findings the author rejected in the last round, each with its thread across rounds. `bobar-cli review wait <run>` blocks until `decisions.json` exists, prints the decisions and ends the run as **done: decided**.

**Other endings:**
- **cancelled:** `bobar-cli review cancel <run>`, or Cancel in the GUI.
- **error:** the reviewer failed twice, or crashed.

Both are final.

## 6. Capturing "the uncommitted changes"

The working tree is snapshotted with a **temporary git index**, so your real index, stash and branches are never touched:

```
GIT_INDEX_FILE=<round dir>\index  git read-tree <base>
GIT_INDEX_FILE=<round dir>\index  git add -A
GIT_INDEX_FILE=<round dir>\index  git write-tree      → tree sha (tree.txt)
```

- `<base>` = `HEAD` at `start`, recorded in `run_started`. Every round diffs against that same base, so it doesn't matter if something gets committed mid-run (the skill tells Claude not to commit).
- `diff.patch` = `git -c core.autocrlf=false diff --ignore-cr-at-eol <base> <tree>`.
- `delta.patch` = the same between the previous round's tree and this one.
- `git add -A` respects `.gitignore` and includes untracked files. Its blobs go into the repo's object store as loose objects, which is harmless; `git gc` cleans them up.
- `start` fails with a clear message if the diff is empty.

All git and child processes are spawned with `ProcessStartInfo.ArgumentList` (never a command string). Process trees are killed with `Process.Kill(entireProcessTree: true)` on cancel, timeout or Ctrl+C.

## 7. Structured outputs

Every output is validated in code (required fields, enums, ids). On a validation failure the CLI does **one repair turn**: it resumes the reviewer session, sends the error text, and validates again. A second failure is an error.

### Reviewer output
```jsonc
{
  "findings": [
    {
      "severity": "bug",                       // bug | risk | design | nit
      "title": "Null deref when the list is empty",
      "explanation": "…what is wrong, why, and what to do instead…",
      "refs": ["src/Foo.cs:42"],               // repo-relative path:line; may be empty
      "previousId": "R1.3"                     // optional: this is my earlier finding R1.3 again
    }
  ]
}
```
The CLI assigns the ids `R<k>.<n>` and stores them in `review.json`. It doesn't validate refs beyond a warning (shown in the GUI) when a path doesn't exist. A `previousId` that doesn't name an earlier finding is a validation error.

### Author response (sent on stdin by Claude)
```jsonc
{
  "responses": [
    { "id": "R2.1", "action": "fix",    "reasoning": "Right; added the empty check." },
    { "id": "R2.2", "action": "reject", "reasoning": "The list is never empty here: Bar() guarantees ≥1 item (Bar.cs:17)." }
  ]
}
```
It must answer exactly the round's ids, each once. A mismatch → exit code 2 with the error on stdout, so Claude fixes it and calls again. No round is consumed.

### decisions.json (written by the GUI)
```jsonc
{ "decisions": [ { "id": "R3.1", "decision": "fix", "comment": "Agree, but keep the old overload." },
                 { "id": "R3.2", "decision": "dismiss" } ],
  "comment": "optional overall note" }
```

## 8. CLI contract

Every command prints **one result to stdout**. It is Markdown for Claude to read, and its first line is machine-parsable: `STATUS: <status> run=<runId> [round=<k>]`. Progress lines (the same text as the GUI summary, §10) go to stderr.

| Command | Does | Result statuses |
|---|---|---|
| `review start --reviewer <shorthand> --desc <file\|->` [`--repo <dir>`, default cwd] | creates the run and runs review 1 | `findings`, `done`, `error` |
| `review next <run> --response <file\|->` | records the response, applies §5, then runs the next review if there is one | `findings`, `awaiting_decisions`, `done`, `error`, `invalid_response` (exit 2) |
| `review wait <run>` | blocks until decisions exist (or the run is cancelled) | `decided`, `cancelled` |
| `review cancel <run>` | kills the run's active process (pid from `run.lock`) and ends the run | `cancelled` |
| `review show <run>` | prints the current state, e.g. after a lost session | any |
| `runs` | lists recent runs | n/a |

`findings` output: a header line with counts, then one block per finding:
```
### R2.1 [bug] Null deref when the list is empty   (maintained from R1.3)
src/Foo.cs:42
…explanation…
```
then a reminder of the response format and the exact `next` command line. `awaiting_decisions` output: "the user is deciding in BobarCode; run `bobar-cli review wait <run>` in the background". `decided` output: each decision and comment, plus "apply the findings marked fix; then report to the user".

Concurrency: a command on a run that another process holds `run.lock` on fails at once with a clear message.

## 9. Reviewer adapters

Interface (Core):
```csharp
interface IReviewer {
  Task<ReviewerResult> RunAsync(ReviewerCall call, IProgress<ReviewerActivity> activity, CancellationToken ct);
  Task<ReviewerResult> RepairAsync(ReviewerResult previous, string error, IProgress<ReviewerActivity> activity, CancellationToken ct);
}
// ReviewerResult: final text, parsed JSON (or null), usage {input, output, cacheRead, cacheWrite}, requested/resolved model, session id, duration.
```
`FakeReviewer` returns scripted outputs per round, for tests.

**Common to both adapters:**
- The prompt goes over **stdin**, never argv.
- cwd = the repo.
- Tools: read-only (read/grep/find/ls), never a shell.
- The reviewer reads `diff.patch` etc. from the round dir by absolute path.
- The prompt tells it to read the repo's `CLAUDE.md` / `AGENTS.md` if present. Harness context loading is off, so both kinds behave the same.
- The full raw stream goes to `reviewer.jsonl`.
- Requested and resolved model ids and reasoning levels go into the `review_received` event.

The flags below were verified in Fractory; details, provider quirks and prices are in `INFRA_FINDINGS.md`.

**Pi (`kind: "pi"`):**
```
<node> <pi's cli.js> --mode json --provider <p> --model <m> --thinking <t>
  --no-extensions --no-skills --no-prompt-templates --no-mcp --no-context-files
  --tools read,grep,find,ls --session-dir <pi-sessions>
  [--session <id>]   (repair turn)
env: PI_CODING_AGENT_DIR=<pi-agent dir>, plus the reviewer's `env` keys
```
- **Launching:** `pi` is an npm `.cmd` shim. Resolve it to its `node` + JS entry and spawn that directly; going through `cmd.exe` mangles arguments.
- **Agent dir:** before each call, write the custom `lithos` provider (`models.json`, copied from Fractory's `PI_MODELS` in `packages/core/src/workers/pi.ts`) and the retry settings (`settings.json`).
- **Output:** the prompt asks for **a final message containing only the JSON object** (unverified that every model does this reliably; the repair turn covers lapses). The adapter takes the text of the last assistant `message_end`, strips a code fence if any, and parses it. Pi has no `write` tool here.
- **Failure:** a last assistant message with `stopReason` `error`, `aborted` or `length` is a failure, even with exit code 0.
- **Usage:** sum `message_end(role=assistant).message.usage` (never `message_update`).
- **Effective reasoning level:** `message_end.message.thinkingLevel`.
- **JSONL framing:** split on `\n` only.

**Claude (`kind: "claude"`):**
```
claude -p --output-format stream-json --verbose --model <m> --effort <e>
  --session-id <uuid> | -r <uuid> (repair)
  --tools Read,Grep,Glob --permission-mode dontAsk
  --setting-sources "" --strict-mcp-config --add-dir <run dir>
  --json-schema <reviewer schema>
```
- **Env:** delete `ANTHROPIC_API_KEY`, `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN` from the child env. **Never `--bare`.** Fail the call unless `system/init.apiKeySource == "none"`, so it never bills an API key.
- **Output:** `result.structured_output`.
- **Quota:** a `rate_limit_event` with a status other than `allowed*` (and no overage in use) is reported as "Claude quota exhausted until <resetsAt>".

**Transient errors** (429, overloaded, network): Pi retries internally (agent dir settings). On top of that, the CLI retries a failed reviewer call **once** after 60 s, or after the Retry-After from the error text if there is one (cap 15 min). The retry is shown in the summary feed.

**Timeout:** `reviewerTimeoutMinutes` kills the process tree, which is an error.

**Cost** = tokens × `prices["<provider>/<model>"]`, computed by the CLI. Vendor-reported cost is ignored.

## 10. Events and the summary feed

`events.jsonl` lines are `{ "t": "<ISO time>", "type": "...", ... }`:

| type | fields |
|---|---|
| `run_started` | repo, base, reviewer shorthand + resolved config, maxRounds |
| `review_requested` | round |
| `reviewer_retry` | round, reason, delaySeconds |
| `review_received` | round, counts by severity, maintained/new/withdrawn counts, durationMs, usage, costUsd (or null), models, reasoning |
| `author_responded` | round, fix counts by severity, reject count |
| `awaiting_decisions` | round, ids, reason (`deadlock`\|`cap`) |
| `decisions_submitted` | fix/dismiss counts |
| `run_finished` | outcome (`clean`\|`cap`\|`decided`\|`cancelled`\|`error`), message |

Secrets never appear in events: a single redactor masks every `keys.env` value in anything written to the run dir.

`Summary.Lines(RunState)` in Core is a pure function that turns the state into the feed. The GUI's first tab shows it, and the CLI prints it to stderr as it goes. For example:

```
Requesting review from ds (DeepSeek-V4.1-Flash-ultra)…
Reviewer returned 6 findings: 1 bug, 2 risk, 1 design, 2 nit.        1m48s · $0.004
Author will fix 1 bug, 1 risk, 1 nit; rejects 3 (and is now acting on them…)
Sending the author's response to the reviewer and requesting re-review…
Reviewer returned 2 findings: 1 risk (maintained), 1 nit (new); withdrew 2.   2m03s · $0.005
Author will fix none; rejects 2.
Sending the author's response to the reviewer and requesting re-review…
Reviewer returned 1 finding: 1 risk (maintained); withdrew 1.         0m55s · $0.003
Author will fix none; rejects 1.
Reviewer and author disagree on 1 finding. Waiting for your decision…
You decided: fix 1. Ending review session.
```

- The line for a step in progress carries a live "…" with the elapsed time and the time since the last reviewer activity. That is how a stuck model shows up.
- "(and is now acting on them…)" stays on until the next event arrives.

## 11. GUI (`bobar.exe`)

**Single instance:** a named mutex. A second launch passes its arguments (e.g. `--run <id>`) over a named pipe to the first instance, which comes to the front, and then exits.

**Window:**
- **Toolbar:** **Open config**, which opens `config.json` with the system's default app for `.json` (`Process.Start` with `UseShellExecute = true`). The config is created with defaults first if missing.
- **Left pane:** the runs, grouped by repo, newest first. Each shows a status badge (running, waiting for author, awaiting decisions, done, error, cancelled) and its age. A run that isn't finished and has no `run.lock` holder for over an hour is shown as *idle*.
- **Right pane, for the selected run:**
  - **Summary** tab (first, default): the feed from §10.
  - **Exchange** tab: every round's findings with the author's responses, as plain text for now.
  - **Details** button: a modal with the raw reviewer activity per round from `reviewer.jsonl` (text, thinking, tool calls with their arguments, errors), tailing live.
  - **Cancel** button (unfinished runs): runs the `cancel` logic.
- **Decisions panel** (replaces the Summary tab's lower half when the run awaits decisions): one card per rejected finding, showing severity, title, explanation, refs, the author's reasoning and the earlier thread. Each card has:
  - a two-way toggle: **Fix it** (side with the reviewer) / **Dismiss** (side with the author);
  - an **Add comment** button that reveals a textarea.

  Below the cards are an optional overall comment and **Submit**, enabled once every card has a choice. Submit writes `decisions.json` atomically (temp file + rename) and appends `decisions_submitted`.

**Live updates:** a `FileSystemWatcher` on the runs dir, plus a 1 s poll as a fallback (watchers drop events). Watcher callbacks marshal onto the UI thread with `Dispatcher.UIThread.Post`. The GUI only reads, except for `decisions.json` and the cancel action.

**MVVM:** CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), with view models over the Core `RunState`. No ReactiveUI.

**Rendering:** plain text for now. Markdown and diff rendering can come later.

## 12. The skill (`skill/lx-review/SKILL.md`)

```markdown
---
name: lx-review
description: Have another model review your uncommitted changes and iterate on its findings via bobar-cli. Use when the user types /lx-review <reviewer>.
---

You are the **author** in a review loop run by `bobar-cli`. The reviewer is `$ARGUMENTS` (a shorthand from BobarCode's config; default `ds`).

1. Don't commit anything during the loop.
2. Start: run (Bash, `run_in_background: true`, don't poll; you'll be notified):
   `bobar-cli review start --reviewer <reviewer> --desc -` with a heredoc on stdin containing a short description of the uncommitted changes: what changed and why, and anything a reviewer should know.
3. Read the command's output. Its first line is `STATUS: …`.
   - `findings`: for each finding, decide honestly whether it needs action. Fix real problems. Reject noise, misreadings and taste disagreements, with a concrete reason (cite path:line). Don't accept a finding just to end the loop, and don't reject one just because you wrote the code. Apply the fixes and run the project's tests if relevant. Then run `bobar-cli review next <run> --response -` (background, as above) with the response JSON on stdin, in the format the output shows. Go to 3.
   - `invalid_response`: fix the JSON and rerun `next`.
   - `awaiting_decisions`: tell the user BobarCode is open for their decisions. Run `bobar-cli review wait <run>` in the background. When it returns, apply the findings marked fix (and follow the comments), then go to 4.
   - `done` / `cancelled` / `error`: go to 4.
4. Tell the user in a few lines: rounds, what you fixed, what you rejected and why, and how it ended.
```

## 13. Tests

All tests use `FakeReviewer` and temp git repos. They cover:
- §5's stop table, row by row, and the round cap.
- Withdrawn/maintained counting, `previousId` validation, response id matching (exit 2, no round consumed).
- The temp-index snapshot: untracked files included, ignored files excluded, the real index unchanged, a mid-run commit handled.
- Event folding and `Summary.Lines` (golden text).
- The repair turn, the transient retry, timeout tree-kill (with a fake child process), and the redactor.

`scripts/smoke.ps1` runs one real `ds` review on a tiny scratch repo (opt-in, a few cents). `-Reviewer sol|opus` is behind a flag. `opus` there uses `--model haiku` unless Opus behaviour is under test.

## 14. Milestones

Stop for review after each, with `dotnet build` and `dotnet test` green.

1. **M1: core + CLI on FakeReviewer.** Config, run dir, events/fold, snapshots, §5 loop, all CLI commands, summary lines, tests. Plus a `--reviewer fake` shorthand, wired from an env var, for trying the skill end to end without model calls.
2. **M2: real reviewers.** Pi and Claude adapters, `keys.env`, the redactor, retries, cost, the smoke script, the skill and `install.ps1`.
3. **M3: GUI.** Single instance, run list, Summary/Exchange tabs, Details modal, Open config, Cancel, live updates.
4. **M4: decisions.** The decisions panel, `wait`, `openGuiOnDecisions` (the CLI launches `bobar.exe --run <id>`), polish.

At the end of each milestone, append a section to `docs/decisions-todo.md` with the judgement calls made in it (the choice, why, what changing it affects).
