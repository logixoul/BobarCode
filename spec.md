# BobarCode: spec

Working name. BobarCode replaces Fractory's review workflow with something much lighter.

## 1. What it is

A Windows tool with two exes built from one codebase. It runs a review loop between a **reviewer model** and the **author**: the live Claude Code session that wrote the code.

- `bobar-cli.exe`: headless. Claude calls it from the `/breview` skill (or `/bplan`, which runs the same loop on a plan file, see §12). It does all the work: it runs the reviewer, records everything and decides when the loop ends.
- `bobar.exe`: the desktop app. You open it from the Start menu like any other app. It shows what is going on in all runs, and it is where you decide the disagreements left at the end. It never starts reviewers and never owns a run. If it isn't open, nothing breaks.

**There is no daemon.** The run directory on disk is the only channel between the two. The CLI appends events and writes files; the GUI watches the directory and redraws.

### Design principles
- **Lightweight.** No sandboxes, worktrees, snapshot gates, anonymization or judges. The author edits your working tree directly, the reviewer runs with read-only tools, and nothing is guarded beyond that. The one exception is a streaming reviewer (Pi, §9). It reviews a plain copy of the round's tree, so the author can fix its findings in your working tree while it is still reviewing (§5).
- **The CLI owns the loop.** When to stop is decided by the code in §5, from the structured outputs. No model decides it.
- **JSON is the source of truth.** Every model output is validated JSON stored in the run dir. Markdown is only ever rendered from it.
- **Automated tests never call real models.** They use `FakeReviewer`. Real calls happen only in an opt-in smoke script.

### Out of scope
- Implementing features from a prompt: you do that in your Claude Code session.
- Reviewing arbitrary committed work (`--base`, or one older commit on its own). Only a range ending at HEAD is supported (`--from`, §6): its fixes land cleanly as uncommitted changes on top of HEAD.
- Running build/test checks: the author's session runs them with its own tools.
- Token or cost caps: the 5-round cap is the only limit.
- Non-Windows platforms (Avalonia keeps the door open, but nothing is promised).
- Fractory itself. Renaming its old flow to `review-legacy` is a separate task in the Fractory repo.

## 2. The loop as you see it

1. You finish a change in Claude Code, uncommitted.
2. You type `/breview ds` (or `/breview ds 3` to also review the last 3 commits, if you committed before reviewing).
3. Claude writes a short description of the change and calls `bobar-cli review start --reviewer ds --desc -`.
4. The reviewer (DeepSeek through Pi) reviews the uncommitted changes and returns findings.
5. Claude decides fix or reject for each finding, applies the fixes, and calls `bobar-cli review next <run> --response -` with its reasoning.
6. The reviewer re-reviews the new state. It sees the author's response, can withdraw or maintain the findings the author rejected, and can raise new ones.
7. Steps 5–6 repeat until the loop ends (§5): there are no findings, the author and reviewer are deadlocked, or the round cap is hit.
8. If disagreements are left, the CLI opens the GUI on the run. There you mark each one *fix it* or *dismiss* and optionally add a comment, then press Submit once. Claude, which was waiting on `bobar-cli review wait <run>`, gets your decisions and acts on them.
9. Once the run has ended (any way but cancelled), Claude sends its **hindsight** with `bobar-cli review hindsight <run> --input -` (§7). This is a 1–10 usefulness rating for the whole run, the findings it now thinks it accepted or rejected wrongly, and notes on what BobarCode should change. It is a report only: nothing is reverted, and the run's outcome doesn't change. The GUI shows it with the run and compares reviewers by it (§11).

## 3. Repository layout

```
BobarCode.sln
Directory.Build.props          net11.0, nullable, warnings as errors, LangVersion latest
src/Bobar.Core/             class library: everything except UI and argv parsing
src/Bobar.Cli/              console exe, AssemblyName bobar-cli
src/Bobar.App/              Avalonia WinExe, AssemblyName bobar
tests/Bobar.Core.Tests/     xUnit; FakeReviewer only
skill/breview/SKILL.md       the Claude Code skill (installed to ~/.claude/skills/breview/)
skill/bplan/SKILL.md           writes the agreed plan to a file and runs breview's loop on it (installed to ~/.claude/skills/bplan/)
scripts/smoke.ps1              opt-in real-model smoke test (cheapest reviewer by default)
scripts/make-ico.ps1           regenerates src/Bobar.App/Assets/icon.ico from icon.png (exe and window icon)
```

**Stack:** .NET 11, C#, Avalonia 11 (XAML, compiled bindings on by default via `AvaloniaUseCompiledBindingsByDefault`), CommunityToolkit.Mvvm, System.Text.Json with source-generated contexts, System.CommandLine for the CLI.

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
  cancel.request         written by a cancel while a CLI holds run.lock; the CLI notices it and ends the run (§5)
  desc.md                the description of the change
  round-1/
    tree.txt             git tree sha of the working state this round reviewed (§6)
    diff.patch           base..tree: everything uncommitted
    delta.patch          previous round's tree..this tree (round ≥ 2 only)
    history.md           earlier rounds' findings and author responses, rendered (round ≥ 2 only)
    prompt.md            the exact prompt sent to the reviewer
    reviewer.jsonl       raw reviewer stream (for the Details modal)
    findings.jsonl       streaming reviewers only, append-only: each finding as it was reported ({"id", ...finding, "t"}), and {"restart": true} where a retried call started over
    verdicts.jsonl       the author's verdicts declared before its response (`review verdicts`), one line per call ({"t", "fix": [ids], "reject": [ids]}); display only
    review.json          validated reviewer output plus CLI-assigned ids
    response.json        validated author response (an explore run's also has `chosen`)
    ledger.json          explore runs only: which earlier rejection each P id in the prompt stands for (§8a)
  round-2/ …
  decisions.json         your decisions (written by the GUI)
  hindsight.json         the author's hindsight on the finished run (§7)
```
State is never stored separately: both exes rebuild it by folding `events.jsonl` (one shared `RunState.Fold` in Core).

### config.json
```jsonc
{
  "maxRounds": 5,
  "reviewerTimeoutMinutes": 30,
  "openGuiOnDecisions": true,
  "reviewerInstructions": "",   // e.g. "Prefer lightweight designs."
  "reviewers": {
    "ds":   { "kind": "pi", "provider": "lithos", "model": "deepseek-ai/DeepSeek-V4.1-Flash-ultra", "thinking": "high", "env": ["LITHOS_API_KEY"] },
    "sol":  { "kind": "pi", "provider": "openrouter", "model": "openai/gpt-6.2-sol", "thinking": "medium", "env": ["OPENROUTER_API_KEY"] },
    "hydrogen": { "kind": "pi", "provider": "openrouter", "model": "google/gemini-4-hydrogen", "thinking": "medium", "env": ["OPENROUTER_API_KEY"] },
    "qwen4": { "kind": "pi", "provider": "openrouter", "model": "qwen/qwen4-max", "thinking": "high", "env": ["OPENROUTER_API_KEY"] },
    "glm":  { "kind": "pi", "provider": "openrouter", "model": "z-ai/glm-5.3", "thinking": "high", "env": ["OPENROUTER_API_KEY"] },
    "terra": { "kind": "pi", "provider": "openrouter", "model": "openai/gpt-6.1-terra", "thinking": "medium", "env": ["OPENROUTER_API_KEY"] },
    "luna": { "kind": "pi", "provider": "openrouter", "model": "openai/gpt-6-luna", "thinking": "medium", "env": ["OPENROUTER_API_KEY"] },
    "neon": { "kind": "pi", "provider": "openrouter", "model": "google/gemini-4-neon", "thinking": "medium", "env": ["OPENROUTER_API_KEY"] },
    "opus": { "kind": "claude", "model": "opus", "effort": "medium" }
  },
  "prices": {   // USD per 1M tokens; sources and caveats in INFRA_FINDINGS.md §1
    "lithos/deepseek-ai/DeepSeek-V4.1-Flash-ultra": { "input": 0.35, "output": 1.4, "cacheRead": 0.007 },
    "openrouter/openai/gpt-6.2-sol": { "input": 2, "output": 10, "cacheRead": 0.1 },
    "openrouter/google/gemini-4-hydrogen": { "input": 1.5, "output": 7.5, "cacheRead": 0.075 },
    "openrouter/qwen/qwen4-max": { "input": 1.425, "output": 4.275, "cacheRead": 0.07125 },
    "openrouter/z-ai/glm-5.3": { "input": 1.4, "output": 4.4, "cacheRead": 0.26 },
    "openrouter/openai/gpt-6.1-terra": { "input": 1, "output": 5, "cacheRead": 0.05 },
    "openrouter/openai/gpt-6-luna": { "input": 0.1, "output": 0.5, "cacheRead": 0.01 },
    "openrouter/google/gemini-4-neon": { "input": 3, "output": 15, "cacheRead": 0.15 }
  }
}
```
- A reviewer's **shorthand is its key** (`ds`, `sol`, `hydrogen`, `qwen4`, `glm`, `terra`, `luna`, `neon`, `opus`). Add more by adding keys.
- `env` lists the `keys.env` entries passed to that reviewer's process, and only those.
- `serviceTier` (Pi only, optional), e.g. `"flex"`: the provider's service tier, set by a small Pi extension (INFRA_FINDINGS §3). Its prices are keyed `provider/model@tier` (e.g. `openrouter/openai/gpt-6.2-sol@flex`); without such a key the cost is unknown, never the standard price.
- `reviewerInstructions` is your standing taste for every review, in every repo (empty by default). It goes into the reviewer prompt as its own section, which says the repo's `CLAUDE.md` / `AGENTS.md` wins on conflict. Project-specific rules belong there, not here: the author reads them too. The GUI's Settings window edits this field; the rest of the file is edited by hand ("Open config").
- A `claude` reviewer on the subscription costs nothing in BobarCode's books. A missing price shows the cost as "unknown", never a guess.
- The config file is read fresh on every CLI call. The GUI re-reads it when it changes.

## 5. The loop: exact rules

Round *k* = one reviewer call plus one author response.

**Review k:**
- **k = 1:** the reviewer gets `desc.md` and `diff.patch`.
- **k ≥ 2:** it also gets `delta.patch` (what changed since its last review) and `history.md` (all earlier findings, each with the author's action and reasoning).
- **k ≥ 2 resumes the reviewer's round k-1 session** (its id is in that round's `review_received`), so it doesn't explore the repo again. The prompt is then short: the delta, the updated diff, `history.md`, the author's responses to round k-1 with their ids, the current `reviewerInstructions`, the earlier-findings rules and the output format. If the resume fails (other than by timeout), the round is redone once in a fresh session with the full prompt (`reviewer_retry` with `fresh: true`), and `prompt.md` holds that prompt.

**Streaming reviewers** (Pi): before review k, the CLI checks out round k's tree into `%TEMP%\bobar-snapshots\<data-dir hash>\<runId>\` with a temporary index (`read-tree` + `checkout-index --prefix`; the real index is untouched). The reviewer's cwd is that copy, which is deleted when the reviewer is done (also when the checkout fails). Git LFS files stay pointer files (`GIT_LFS_SKIP_SMUDGE=1`), and submodules aren't checked out: the copy has only their commit pointers. It is outside the run dir because it holds the repo's files unredacted; before each streaming round, the CLI deletes any other run's snapshot under its own data dir's folder whose run no live CLI holds (`run.lock`), so one left behind by a killed or crashed CLI doesn't linger. The path is the same every round, because Pi resumes a session only from the cwd it was recorded in. The reviewer reports each finding with a `report_finding` tool call as soon as it has checked it, and ends with `DONE` (§7). The CLI appends each reported finding to `findings.jsonl` as it arrives. `bobar-cli review stream` prints them, so the author can fix them in the working tree while the reviewer works on the copy. Nothing else changes: the round still ends when the reviewer does, `review.json` holds the reported findings in id order, and the author answers them all at once with `next`. If a reviewer call in a fresh session is retried (round 1's transient retry, or a fresh session after a failed resume) after reporting something, a `{"restart": true}` line is appended: the findings before it no longer count, and ids are reused. A call that resumes a session (a transient retry in round ≥ 2, a repair) keeps what was reported, since the session holds those calls, and numbering continues after them. Refs are made repo-relative before they are written, so the author is never pointed into the snapshot, and string values are redacted before serializing.

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
- **cancelled:** `bobar-cli review cancel <run>`, or Cancel in the GUI. If a CLI holds `run.lock`, cancel writes `cancel.request` in the run dir; the CLI polls for it, kills its reviewer tree and ends the run itself, so its caller gets `STATUS: cancelled` (with the reason and "don't retry") rather than a killed process. Only if it hasn't done so within 10 s is the CLI's tree killed.
- **error:** the reviewer failed twice, or crashed.

Both are final.

**After the end:** the hindsight (§7) is recorded after `run_finished`, for every outcome except cancelled. It never changes the outcome or reopens the loop.

## 6. Capturing "the uncommitted changes"

The working tree is snapshotted with a **temporary git index**, so your real index, stash and branches are never touched:

```
GIT_INDEX_FILE=<round dir>\index  git read-tree <base>
GIT_INDEX_FILE=<round dir>\index  git add -A
GIT_INDEX_FILE=<round dir>\index  git write-tree      → tree sha (tree.txt)
```

- `<base>` is where diffs start: `HEAD` at `start`, or with `--from <rev>` the first parent of `<rev>` (the empty tree for a root commit). The temp index is always seeded (`read-tree`) from `HEAD` at `start` (`seed`), not from the base: otherwise a file committed in the range that `.gitignore` matches would be skipped by `add -A` and show as deleted. Both are recorded in `run_started`.
- `--from <rev>` must be HEAD or an ancestor of it. The commits are `git log --reverse --first-parent <base>..HEAD`; their messages, oldest first, become `desc.md`, followed by `--desc` as the author's notes (optional with `--from`). Uncommitted changes are part of the review too, and fixes land uncommitted on top of HEAD.
- Every round diffs against that same base, so it doesn't matter if something gets committed mid-run (the skill tells Claude not to commit).
- `diff.patch` = `git -c core.autocrlf=false diff --ignore-cr-at-eol <base> <tree>`.
- `delta.patch` = the same between the previous round's tree and this one.
- `git add -A` respects `.gitignore` and includes untracked files. Its blobs go into the repo's object store as loose objects, which is harmless; `git gc` cleans them up.
- `start` fails with a clear message if the diff is empty (with `--from`: the range plus the working tree makes no net change).

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
**Streaming reviewers** (§9) don't return this JSON. They call `report_finding` once per finding, with the same fields, and end with the text `DONE`. The tool checks `previousId` and non-empty `title`/`explanation` itself (a failed call goes back to the model to correct) and numbers the findings: its reply is `Recorded as R<k>.<n>`. Their review output is the reported findings, in that order, once their final text is just `DONE` (any case, markdown and a full stop aside; "DONE, but…" doesn't count). Without `DONE`, a final text holding a `{"findings": [...]}` object is used as for any other reviewer, but only if nothing was reported. Anything else gets the repair turn: a refusal, prose, or reports without `DONE` (the review may be unfinished). The repair resumes the session with the tool on: findings already reported count, it is asked to finish and say `DONE`, and numbering continues.

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

### hindsight.json (sent on stdin by Claude after the run ended)
```jsonc
{
  "usefulness": 7,                                                   // 1..10, anchors below
  "acceptedWrongly": [ { "id": "R1.2", "why": "Defensive null check; the caller guarantees non-null." } ],
  "rejectedWrongly": [ { "id": "R2.3", "why": "I talked the reviewer out of it, but it was right." } ],
  "notes": "…Markdown: why this rating, and what (if anything) BobarCode should change…"
}
```
- `acceptedWrongly` ids must be findings the author answered `fix`, in any round. Fixes the user ordered in the GUI aren't the author's calls, so they don't count.
- `rejectedWrongly` ids must be findings the author answered `reject`, in any round. That includes findings the reviewer then withdrew and ones the user sided with the author on.
- Each item needs a `why`, no id may appear twice, and `notes` must be non-empty.
- Failing any of these rules → `invalid_hindsight`, exit 2. Sending a hindsight again replaces the earlier one.

**The rating counts how many significant or severe issues the run caught, not just the worst one.** Wrong findings and churn pull it down; cost doesn't count (the GUI shows cost next to the rating). The anchors (`Hindsight.Anchors` in Core) are:

| | |
|---|---|
| 1 | Net harm: wasted time or pushed me into worse code |
| 2 | Nothing useful, only noise |
| 3 | Only nits |
| 4 | One or two minor improvements |
| 5 | Several minor improvements |
| 6 | Made useful suggestions |
| 7 | Caught a significant issue |
| 8 | Caught several significant issues |
| 9 | Caught a severe issue |
| 10 | Caught several severe issues, or a severe one plus significant ones |

The terms are defined as follows:
- **Severe:** wrong results, data loss, a security hole, or a crash on a main path.
- **Significant:** a real bug or design flaw worth fixing before merge.

## 8. CLI contract

Every command prints **one result to stdout**. It is Markdown for Claude to read, and its first line is machine-parsable: `STATUS: <status> run=<runId> [round=<k>]`. Progress lines (the same text as the GUI summary, §10) go to stderr.

| Command | Does | Result statuses |
|---|---|---|
| `review start --reviewer <shorthand> --desc <file\|->` and/or `--from <rev>` [`--repo <dir>`, default cwd] | creates the run and runs review 1 | `findings`, `done`, `cancelled`, `error` |
| `review next <run> --response <file\|->` | records the response, applies §5, then runs the next review if there is one | `findings`, `awaiting_decisions`, `done`, `cancelled`, `error`, `invalid_response` (exit 2) |
| `review stream [<run>] [--round <k>]` [`--repo <dir>`] | read-only, takes no lock: prints round k's findings as a streaming reviewer reports them (one `### R<k>.<n> [severity] title` block each), then a line starting `END` once the round's review is in or the run ended. A `RESTART` line means the reviewer started the round over. Without a run, it waits up to 2 min for the first run of `--repo` (default cwd) that starts after it was called, prints `RUN <runId> round=1` (so the author has the id for `review verdicts` before `start` returns), and streams round 1; so it is armed before `start`. Without `--round`, the run's latest round. A round that won't happen because the run is awaiting decisions also ends it. The skill runs it under Claude Code's Monitor tool | n/a (exit 0, or 1 when there is no run) |
| `review verdicts <run> [--fix <ids>] [--reject <ids>]` | lock-free: before applying fixes, the author declares which of the current round's findings (in `review.json`, or streamed so far) it will fix and which it rejects, appended to `round-k/verdicts.jsonl`. A later call overrides an earlier one per id; lines from before a streaming `RESTART` don't count. Display only (the GUI shows them until `response.json` exists); never affects §5 | `verdicts_saved`, `invalid_verdicts` (exit 2), `error` |
| `review wait <run>` | blocks until decisions exist (or the run is cancelled) | `decided`, `cancelled` |
| `review cancel <run>` | asks the CLI holding `run.lock` to stop (killing it after 10 s) and ends the run (§5) | `cancelled`; the run's own finished status (`done`, `decided`, `error`) if it had already ended or ends before the cancel takes effect |
| `review show <run>` | prints the current state, e.g. after a lost session | any |
| `review hindsight <run> --input <file\|->` | after the run finished (not cancelled): validates and stores the author's hindsight (§7) | `hindsight_saved`, `invalid_hindsight` (exit 2), `error` |
| `runs` | lists recent runs | n/a |

`findings` output: a header line with counts, then one block per finding:
```
### R2.1 [bug] Null deref when the list is empty   (maintained from R1.3)
src/Foo.cs:42
…explanation…
```
then a reminder of the response format and the exact `next` command line. `awaiting_decisions` output: "the user is deciding in BobarCode; run `bobar-cli review wait <run>` in the background". `decided` output: each decision and comment, plus "apply the findings marked fix; then report to the user".

Concurrency: a command on a run that another process holds `run.lock` on fails at once with a clear message.

## 8a. Explore runs

A one-shot exploration of the whole repo, used by the `bexplore-cleanup` skill (§12). Same run dir, events, reviewers, streaming and hindsight as a review, but one reviewer call and no diff.

| Command | Does | Result statuses |
|---|---|---|
| `explore start --reviewer <shorthand> --prompt <file\|->` [`--repo <dir>`] | creates the run (`kind: "explore"`, maxRounds 1): snapshots the working state (for a streaming reviewer), writes the task to `desc.md`, and makes one reviewer call | `findings`, `done` (no findings), `cancelled`, `error` |
| `explore finish <run> --response <file\|->` | the author's answer: `{"responses": [{"id", "action": "fix"\|"reject", "reasoning"}], "chosen": [ids]}`, where `fix` means accepted, every finding is answered, a reject needs a reasoning, and `chosen` (accepted ids to implement now; may be empty) is required. Ends the run `explored` | `done`, `invalid_response` (exit 2), `error` |

- Finding ids are `E.<n>`. `review next` refuses an explore run, and `explore finish` refuses a review run or an explore run that isn't waiting for the author (answered, cancelled). `review show`, `stream`, `verdicts` (display only: "accept"), `cancel` and `hindsight` work on both; their messages point an explore run at `explore finish`.
- There is no user-decisions step: the author's answer is final, and the GUI shows it.
- **Prompt:** the task, the repo's `CLAUDE.md` / `AGENTS.md`, `reviewerInstructions`, the rule that each title names the symbol it is about (line numbers drift), and the **past rejections**: one line per record, `P<n>  <paths>  <title> - rejected: <reason>`, with "don't propose these again unless you have a new argument; if you do, set `previousId` to its P id". `previousId` must name one of them.
- **The ledger** (`ExploreLedger.Build`, pure) is built from the files of every finished (`explored`) explore run of the same repo, oldest first by start time: each rejected finding becomes a record (refs without line numbers, title, reason). A finding whose `previousId` names a P id (resolved through that run's `ledger.json`) replaces the record it re-proposes, so only the latest verdict of each chain counts: a re-proposal the author accepted drops the old rejection. All records go in the prompt, with no cap.
- `review start --explore-run <run>` records the exploration a cleanup review implements; the GUI shows it in the run's header.

## 9. Reviewer adapters

Interface (Core):
```csharp
interface IReviewer {
  Task<ReviewerResult> RunAsync(ReviewerCall call, IProgress<ReviewerActivity> activity, CancellationToken ct);
  Task<ReviewerResult> RepairAsync(ReviewerResult previous, string error, IProgress<ReviewerActivity> activity, CancellationToken ct);
  bool StreamsFindings => false;   // true: reports findings through call.OnFinding (report_finding), §5
}
// ReviewerResult: final text, parsed JSON (or null), usage {input, output, cacheRead, cacheWrite}, requested/resolved model, session id, duration.
```
`FakeReviewer` returns scripted outputs per round, for tests. With `streams: true` (or `BOBAR_FAKE_STREAM=1` for `--reviewer fake`), it reports each scripted finding through `OnFinding` and returns `DONE`.

**Common to both adapters:**
- The prompt goes over **stdin**, never argv.
- cwd = the repo (for a streaming reviewer, the round's snapshot, §5).
- Tools: read-only (read/grep/find/ls), never a shell.
- The reviewer reads `diff.patch` etc. from the round dir by absolute path.
- The prompt tells it to read the repo's `CLAUDE.md` / `AGENTS.md` if present. Harness context loading is off, so both kinds behave the same.
- The prompt includes `reviewerInstructions` (§4) when set (a resume prompt repeats the current ones as replacing earlier ones, or says there are none now), and from round 2 on tells the reviewer that new findings should be as serious as round-1 ones, not ever smaller edge cases (problems the fixes introduced still count).
- The full raw stream goes to `reviewer.jsonl`.
- Requested and resolved model ids and reasoning levels go into the `review_received` event.

The flags below were verified in Fractory; details, provider quirks and prices are in `INFRA_FINDINGS.md`.

**Pi (`kind: "pi"`):**
```
<node> <pi's cli.js> --mode json --provider <p> --model <m> --thinking <t>
  --no-extensions --no-skills --no-prompt-templates --no-mcp --no-context-files
  --tools read,grep,find,ls,report_finding -e <pi-agent dir>\bobar-report-finding.js
  --session-dir <pi-sessions>
  [--session <id>]   (round ≥ 2 and repair turn)
env: PI_CODING_AGENT_DIR=<pi-agent dir>, BOBAR_ROUND=<k>, BOBAR_EARLIER_IDS=<ids, comma-separated>,
     plus the reviewer's `env` keys
```
- **Launching:** `pi` is an npm `.cmd` shim. Resolve it to its `node` + JS entry and spawn that directly; going through `cmd.exe` mangles arguments.
- **Agent dir:** before each call, write the custom `lithos` provider (`models.json`, copied from Fractory's `PI_MODELS` in `packages/core/src/workers/pi.ts`) and the retry settings (`settings.json`).
- **Streaming:** Pi reviewers stream (§5). `report_finding` comes from a small extension that BobarCode writes into the agent dir (`PiReviewer.ReportExtension`), loaded with `-e` through Pi's own extension API (`pi.registerTool`). Explicit `-e` paths load despite `--no-extensions`. The tool writes nothing: it validates and numbers the finding and returns it in `details`. The adapter picks it up from the successful `tool_execution_end` events in the stream, and the engine writes it, redacted, to `findings.jsonl`. Pi has no `write` tool here.
- **Output:** the final message is `DONE`. A Pi reviewer that ignores the tool and returns the JSON object instead is still handled (§7): the adapter takes the text of the last assistant `message_end`, strips a code fence if any, and parses it.
- **Failure:** a last assistant message with `stopReason` `error`, `aborted` or `length` is a failure, even with exit code 0.
- **Usage:** sum `message_end(role=assistant).message.usage` (never `message_update`).
- **Effective reasoning level:** `message_end.message.thinkingLevel`.
- **JSONL framing:** split on `\n` only.

**Claude (`kind: "claude"`):**
```
claude -p --output-format stream-json --verbose --model <m> --effort <e>
  --session-id <uuid> | -r <uuid> (round ≥ 2 and repair)
  --tools Read,Grep,Glob --permission-mode dontAsk
  --setting-sources "" --strict-mcp-config --add-dir <run dir>
  --json-schema <reviewer schema>
```
- **Env:** delete `ANTHROPIC_API_KEY`, `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN` from the child env. **Never `--bare`.** Fail the call unless `system/init.apiKeySource == "none"`, so it never bills an API key.
- **Output:** `result.structured_output`.
- **Quota:** a `rate_limit_event` with a status other than `allowed*` (and no overage in use) is reported as "Claude quota exhausted until <resetsAt>".

**Transient errors** (429, overloaded, network): Pi retries internally (agent dir settings). On top of that, the CLI retries a failed reviewer call **once** after 60 s, or after the Retry-After from the error text if there is one (cap 15 min). The retry is shown in the summary feed.

**Timeout:** `reviewerTimeoutMinutes` kills the process tree, which is an error.

**Cost** = tokens × `prices["<provider>/<model>"]`, computed by the CLI. Vendor-reported cost is ignored. While a Pi round is in progress, the GUI shows its cost so far (`Pricing.LiveCost`: the per-message usage in that round's `reviewer.jsonl` × the same price table, retried attempts included) in the round's row and in the total. Display only, never recorded.

## 10. Events and the summary feed

`events.jsonl` lines are `{ "t": "<ISO time>", "type": "...", ... }`:

| type | fields |
|---|---|
| `run_started` | repo, base, seed, commits (sha + message; `--from` only), reviewer shorthand + resolved config, maxRounds; `kind: "explore"` for an explore run (absent: a review); `exploreRun`, the explore run a review implements (`--explore-run`) |
| `review_requested` | round |
| `reviewer_retry` | round, reason, delaySeconds, fresh (a failed resume redone in a fresh session) |
| `review_received` | round, counts by severity, maintained/new/withdrawn counts, durationMs, usage, costUsd (or null), models, reasoning, sessionId |
| `author_responded` | round, fix counts by severity, reject count |
| `awaiting_decisions` | round, ids, reason (`deadlock`\|`cap`) |
| `decisions_submitted` | fix/dismiss counts |
| `run_finished` | outcome (`clean`\|`cap`\|`decided`\|`cancelled`\|`error`\|`explored`), message |
| `hindsight_submitted` | usefulness, acceptedWrongly and rejectedWrongly counts (after `run_finished`; the last one wins) |

Secrets never appear in events: a single redactor masks every `keys.env` value in anything written to the run dir.

`Summary.Lines(RunState)` in Core is a pure function that turns the state into the feed. The CLI prints it to stderr as it goes. For example:

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
- **Toolbar:** **Reviewers**, a modal with two tabs:
  - **Reviewers:** one row per reviewer shorthand, kind, provider and model, over finished runs (cancelled ones excluded). Each row shows runs, rated runs, average usefulness, average cost and time per run (error runs excluded: the failed call's time and cost aren't recorded), and "N of M fixes" accepted wrongly / "N of M rejects" rejected wrongly, over rated runs (`ReviewerStats.Build` in Core, pure).
  - **Notes:** every hindsight, newest first: rating, repo, reviewer, the regret counts and the notes.

  **Settings**, a modal that edits `reviewerInstructions` (§4) and saves config.json (rewritten from the parsed config, so comments and unknown keys are dropped; a broken file is never overwritten). **Open config**, which opens `config.json` with the system's default app for `.json` (`Process.Start` with `UseShellExecute = true`). The config is created with defaults first if missing.
- **Left pane:** the runs, grouped by repo, newest first. Each shows a status badge (running, waiting for author, awaiting decisions, done, error, cancelled) and its age, plus the usefulness rating ("7/10") once the hindsight is in. A run that isn't finished and has no `run.lock` holder for over an hour is shown as *idle*.
- **Right pane, for the selected run:**
  - **Summary**: a table with one row per round (`SummaryTable.Build` in Core, pure): severity chips for what the reviewer found (marked maintained/repeated, plus withdrawals, retries and repairs), the author's fix/reject counts, time and cost. While a streaming reviewer is still on a round, its chips and findings come from `findings.jsonl` (after the last restart), so they appear as they are reported; `review.json` replaces them when the round ends. Until the author's response, its declared verdicts (`verdicts.jsonl`) show as the fix/reject counts and as "fixing"/"rejected" on each finding, with "N to decide…" while some are undeclared. Click a round to list its findings (id, severity, title, fixed/rejected), from `review.json`/`response.json`; click a finding to expand it, showing what it repeats or maintains, the reviewer's explanation and refs (with warnings), the author's reasoning, and your decision if there was one. A "You" row shows a pending or submitted decision, a Total row sums findings, fixes, time and cost, and the outcome sits below the table. The run's status is a badge next to its id. Colours are theme resources (light and dark). Below the outcome, a **Hindsight** section shows:
    - the rating and its anchor;
    - the findings accepted or rejected wrongly, each with its severity, title and why. Click one to expand it, showing the reviewer's explanation and refs, what the author answered, and your decision if there was one;
    - the notes, as plain text.

    Before the hindsight arrives, the section says it hasn't been sent yet. It isn't shown for cancelled runs.
  - **Copy exchange** button: copies every round's findings with the author's responses and your decisions as plain text.
  - **Details** button: a modal with the raw reviewer activity per round from `reviewer.jsonl` (text, thinking, tool calls with their arguments, errors), tailing live.
  - **Cancel** button (unfinished runs): runs the `cancel` logic.
- **Decisions panel** (below the table while the run awaits decisions): one card per rejected finding, showing severity, title, explanation, refs, the author's reasoning and the earlier thread. Each card has:
  - a two-way toggle: **Fix it** (side with the reviewer) / **Dismiss** (side with the author);
  - an **Add comment** button that reveals a textarea.

  Below the cards are an optional overall comment and **Submit**, enabled once every card has a choice. Submit writes `decisions.json` atomically (temp file + rename) and appends `decisions_submitted`.

**Live updates:** a `FileSystemWatcher` on the runs dir, plus a 1 s poll as a fallback (watchers drop events). Watcher callbacks marshal onto the UI thread with `Dispatcher.UIThread.Post`. The GUI only reads, except for `decisions.json`, the cancel action and Settings (config.json).

**MVVM:** CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`), with view models over the Core `RunState`. No ReactiveUI.

**Rendering:** plain text for now. Markdown and diff rendering can come later.

## 12. The skill (`skill/breview/SKILL.md`)

A third skill, `skill/bexplore-cleanup/SKILL.md` (`/bexplore-cleanup <explorer> [reviewer]`, reviewer default `sol`), is meant to run unattended overnight: it stops on a dirty working tree, runs `explore start` with a cleanup task, accepts or rejects each finding (one-sentence rejects, since later explorations see them), picks one cohesive change from the accepted ones (or none, a valid outcome), sends `explore finish`, implements the change, runs the full tests, reviews it with this skill's loop (`--explore-run`), then sends the exploration's hindsight. Design notes: plan.md (while it exists) and DECISIONS_MADE.md.

A second skill, `skill/bplan/SKILL.md` (`/bplan <reviewer> [file]`), writes the plan agreed in the conversation to an uncommitted file (default `plan.md`, kept as it is if it exists) and runs this skill's loop on it with a `--desc` saying to review it as a plan. It defers the loop to this skill instead of repeating it. Design notes: DECISIONS_MADE.md.

```markdown
---
name: breview
description: Have another model review your uncommitted changes (or the last N commits too) and iterate on its findings via bobar-cli. Use when the user types /breview <reviewer> [N].
---

You are the **author** in a review loop run by `bobar-cli`. `$ARGUMENTS` is `<reviewer> [N]`: the reviewer is a shorthand from BobarCode's config (default `sol`); the optional N means "also review the last N commits".

1. Don't commit anything during the loop.
2. Start: run (Bash, `run_in_background: true`, don't poll; you'll be notified):
   - Without N: `bobar-cli review start --reviewer <reviewer> --desc -` with a heredoc on stdin containing a short description of the uncommitted changes: what changed and why, and anything a reviewer should know.
   - With N: `bobar-cli review start --reviewer <reviewer> --from HEAD~<N-1>` (so N=1 is `--from HEAD`). The commit messages become the description, and anything uncommitted is reviewed too. If you know something the messages don't say, add `--desc -` with a short heredoc of notes. Your fixes stay uncommitted on top of HEAD; the user commits them.

   Just before launching `start` (or `next`), arm the Monitor tool on `bobar-cli review stream` (for `start`; run it in the repo, and arm it first: it waits for the next run that starts) or `bobar-cli review stream <run> --round <k+1>` (for `next` after round k), with `timeout_ms` 1800000. Some reviewers report each finding as soon as they find it: each one arrives as a notification (`### R<k>.<n> [severity] title`, refs, explanation) while the reviewer is still working. It reviews a frozen copy, so you can edit the working tree meanwhile. Decide each streamed finding as in 3, declare the verdict as in 3 (the stream's first line, `RUN <run> round=1`, gives the run id before `start` returns), then apply its fix right away; keep your reasoning for the response. `END` means the reviewer is done. `RESTART` means it started the round over: the findings before it no longer count. Keep the edits you made for them, but when the full result comes, undo any of those edits whose finding isn't in it and that you wouldn't keep on its own merits. Other reviewers stream nothing until `END`. Either way, the background command's own output is what counts.
3. When the background command finishes, read its output. Its first line is `STATUS: …`.
   - `findings`: for each finding, decide honestly whether it needs action (findings you already handled from the stream keep the decision you made, unless the full list changes your mind). Fix real problems. Reject noise, misreadings and taste disagreements, with a concrete reason (cite path:line). Don't accept a finding just to end the loop, and don't reject one just because you wrote the code. Before applying any fix, declare the verdicts you haven't declared yet, so the user sees them in BobarCode: `bobar-cli review verdicts <run> --fix R<k>.1,R<k>.3 --reject R<k>.2` (foreground, returns at once; omit an empty option; a later call overrides an earlier one per id). It's only for display: on `invalid_verdicts`, carry on. Then apply the fixes. Don't run the full test suite or a full typecheck between rounds; at most do a quick targeted check when a fix is non-trivial. Then run `bobar-cli review next <run> --response -` (background, as above) with the response JSON on stdin, in the format the output shows, arming the Monitor first as in 2. Go to 3.
   - `invalid_response`: stop the Monitor you armed for this `next` (TaskStop: no round started, so its stream would wait until it times out), fix the JSON, and rerun `next` as in 3, arming a new Monitor first.
   - `awaiting_decisions`: tell the user BobarCode is open for their decisions. Run `bobar-cli review wait <run>` in the background. When it returns, read its output as in 3.
   - `decided`: apply the findings marked fix (and follow the comments), then go to 4.
   - `busy`: another bobar-cli process is working on the run; wait for it, or run `bobar-cli review show <run>`.
   - `done` / `cancelled` / `error`: go to 4.
4. If you changed code in this run (round fixes, fixes the user ordered at the end, or fixes before a cancel; not just a plan file), run the project's full tests and typecheck once now, and fix anything they catch.
   Hindsight: if the output has a "## Hindsight" section, it asks you to look back on the whole run; otherwise (e.g. a cancelled run, or `start` failing before a run existed) skip to 5. Send it as the output shows (`bobar-cli review hindsight <run> --input -`, in the foreground: it returns at once). Rate honestly, and list the findings you now think you shouldn't have fixed or shouldn't have rejected. Don't revert or redo anything because of it: it's a report. On `invalid_hindsight`, fix the JSON and send it again.
5. Tell the user in a few lines: rounds, what you fixed, what you rejected and why, and how it ended. After a hindsight, add your usefulness rating and the findings you listed as accepted or rejected wrongly.
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
3. **M3: GUI.** Single instance, run list, Summary, Details modal, Open config, Cancel, live updates.
4. **M4: decisions.** The decisions panel, `wait`, `openGuiOnDecisions` (the CLI launches `bobar.exe --run <id>`), polish.

At the end of each milestone, append a section to `DECISIONS_MADE.md` with the judgement calls made in it (the choice, why, what changing it affects).
