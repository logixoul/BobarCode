# Decisions made

Judgement calls made while building BobarCode from `spec.md`, per milestone: the choice, why, and what changing it affects. (This file replaces the `docs/decisions-todo.md` the spec mentions.)

## M1: core + CLI on FakeReviewer

**Layout and libraries**
- **Avalonia 11.3.23, System.CommandLine 2.0.15, xUnit 2.9.3.** Avalonia 12 and System.CommandLine 3.0 exist, but the spec says Avalonia 11, and SCL 2.0 is the stable API I know well. *Change:* bump the package versions; SCL 3 may need small API edits in `src/Bobar.Cli/Program.cs`.
- **`BOBAR_CONFIG_DIR` / `BOBAR_DATA_DIR` env vars** override `%APPDATA%\BobarCode` / `%LOCALAPPDATA%\BobarCode` (`BobarPaths.FromEnvironment`). Needed for tests, the smoke script and trying things without touching real runs.
- **The engine is a library** (`ReviewEngine` in Core returns a `CommandResult`), and the CLI is just argv parsing and printing. Tests drive the engine directly with an injected `FakeReviewer` factory, delay function and GUI launcher.

**JSON and events**
- **Enums are serialized as lowercase strings** via `JsonStringEnumMemberName`; events are a polymorphic `RunEvent` record hierarchy with `type` as the discriminator. Unknown event types and torn last lines are skipped by the fold, so adding event types later is safe.
- **Extra event `reviewer_repair`** (round, error): makes the repair turn visible in the feed. Not in the spec's table.
- **`review_received` carries a `findings` list** (`id`, `severity`, `origin`) besides the counts, so the feed can say "1 risk (maintained), 1 nit (new)" from events alone. It also has a `repeated` count (see below) and a `repaired` flag.
- **`review.json` stores derived data too**: each finding's `origin` and `warnings`, and the round's `withdrawn` ids. They are computed once when the review arrives.
- **A torn last line in `events.jsonl`** (a writer killed mid-append) gets a newline before the next append, so the next event isn't glued to it.

**Loop semantics (§5)**
- **Three origins: `new`, `maintained`, `repeated`.** `maintained` = `previousId` names a finding the author rejected in round k−1 (what the deadlock rule needs). `repeated` = `previousId` names any other earlier finding (e.g. one the author said was fixed). For the stop table, `repeated` counts like `new` ("repeats a previously accepted one"). *Change:* `LoopRules.Classify` / `Decide`.
- **`previousId` may name any earlier round's finding**, not just k−1. Several findings may share a `previousId`.
- **Withdrawn** = ids rejected in round k−1 that no round-k finding references.
- **Response validation:** `reasoning` is required (non-empty) for `reject`, optional for `fix`. The stored response keeps the round's id order.
- **Reviewer validation is lenient where it's harmless:** missing `refs` = `[]`, extra fields ignored, a code fence or prose around the JSON is stripped. Required: `severity` (enum), non-empty `title` and `explanation`.
- **Refs** are normalized to repo-relative forward-slash paths (absolute paths inside the repo are made relative), and a missing path is a warning shown in the CLI output and GUI, never an error.
- **Retry policy:** only errors the adapter classifies as transient are retried, once, after `RetryAfter` (from the error or its text) or 60 s, capped at 15 min. Timeouts, auth/quota errors and validation failures are not retried. The repair call gets its own single retry. The spec's "the reviewer failed twice, or crashed" is read this way. *Change:* `ReviewEngine.CallWithRetry`.
- **Usage of failed calls is lost** (it isn't in any event), so a round's cost slightly undercounts when a retry happened. Kept simple.
- **Ctrl+C or a cancel request** during a `start`/`next` ends the run as `cancelled`, with the reason from `cancel.request` ("interrupted (Ctrl+C)" when there is none). A cancel that trips just after the round finished, before the run ended, still ends it as cancelled rather than handing the author findings.
- **`start` with no changes** fails before a run is created: no run dir appears.
- **`next` on a run that isn't waiting for the author** prints the run's current state (like `show`), prefixed with "nothing to respond to", exit 1.
- **New statuses beyond the spec's table:** `busy` (another process holds `run.lock`; exit 1) and `running` (`show` while the reviewer works). Exit codes: 0 for normal results, 1 for `error`/`busy`/misuse, 2 for `invalid_response`.
- **`runs` prints a Markdown table** without a `STATUS:` line (the spec says its status is n/a).
- **Run ids can be abbreviated**: any command accepts a unique prefix or suffix of a run id.

**Locking and concurrency**
- **`run.lock` is a `FileStream` opened read/write with `FileShare.Read`.** A second writer fails at once; readers can still read the pid. The file is left behind when released (the pid in it is then stale).
- **`wait` and `show` don't hold the lock.** `wait` polls once a second and takes the lock only briefly to append its final events, so `cancel` never kills a waiting `wait`.
- **The GUI's "is anyone holding it" check reads the pid** and checks for a live `bobar-cli` process instead of trying to take the lock (which could make a starting CLI command fail as `busy`).
- **`cancel` asks before it kills.** If a live `bobar-cli` holds `run.lock`, cancel writes `cancel.request` (the reason) to the run dir; `start`/`next` poll for it every 250 ms (`CancelWatch`), kill their reviewer through the usual cancellation path, append `run_finished(cancelled)` and print `STATUS: cancelled`, so Claude learns what happened instead of seeing a bare killed process (it used to guess at a shell failure and retry). If the CLI lets go of the lock without ending the run, cancel ends it itself. Only after 10 s with neither does cancel fall back to killing the pid in `run.lock`, and only if that process is named `bobar-cli` (never itself); it then waits up to 10 s for the lock and appends `run_finished(cancelled)`.

**Snapshots (§6)**
- **The temp index of round ≥ 2 lives in the round dir and is deleted after `write-tree`**; for round 1 it is in `%TEMP%` (the run dir doesn't exist yet, because an empty diff must not create a run).
- **A repo without commits** uses git's empty tree as the base.
- Possible later speed-up: seed the temp index from a copy of `.git/index` instead of `read-tree <base>`, so `git add -A` can use the stat cache on big repos.

**Summary feed (§10)**
- Wording for lines the spec doesn't show: retry ("Reviewer call failed (…); retrying in 1m00s…"), repair, cap ("Round cap (5) reached …"), clean ("The review is clean. Done."), cancelled, error.
- **Cost display:** `$0.004` (three decimals under $1, two above), "cost unknown" when the price is missing, and "subscription" for Claude reviewers.
- **"(and is now acting on them…)" is shown only in live views** (the GUI), while `author_responded` is the latest event. In the CLI's static stderr feed it made deadlock rounds read oddly.
- The live detail of an in-progress line is "elapsed · last activity Ns ago". Last activity comes from the caller (the GUI uses `reviewer.jsonl`'s modification time).

**Fake reviewer**
- **`--reviewer fake` is always available** (unless the config defines a reviewer named `fake`). Its script comes from the file named by **`BOBAR_FAKE_SCRIPT`**: `{"1": [{"output": {...}}, {"fail": "...", "transient": true}, {"hang": true}], "2": [...]}`, where each call in a round consumes the next step. Without a script (or for rounds the script doesn't cover) it uses a default: round 1 raises two sample findings (a bug on the first changed file, and a nit), later rounds maintain everything rejected in the previous round. That's enough to try fix → clean and reject → deadlock → decisions by hand.

**Config**
- `reviewerTimeoutMinutes` is a double (tests use fractions of a minute).
- Reviewers can have an optional `command` (path or name of the `pi`/`claude` executable).
- The config is validated on load (known kinds, a model, a provider for Pi) with a clear error.

## M2: real reviewers

- **Adapters share `CliReviewer`**: child env, raw-log markers, transient classification, result building. Pi's and Claude's stream parsers (`PiStream`, `ClaudeStream`) are separate small classes, and both feed `ReviewerLogFormatter`, which is also what the GUI's Details modal renders.
- **reviewer.jsonl gets BobarCode marker lines** (`{"bobar":"call",…}` with kind, run/repair and argv; `{"bobar":"exit",…}` with exit code, duration and a stderr tail) around each process's raw stream. Argv holds no secrets (keys go in env).
- **Child env:** the Anthropic trio and *every* `keys.env` name are removed from the inherited env; then only the reviewer's `env` entries are added back. A listed key missing from `keys.env` fails the call at once (not retried) with a message naming it. Claude children get no `keys.env` values at all.
- **Pi launcher:** `command` (default `pi`) is resolved to `pi.cmd` on PATH, and the JS entry is read from the shim (`%dp0%\…\cli.js`); `node.exe` next to the shim wins over `node` on PATH. `command` may also point straight at a `.cmd` or `.js`.
- **Pi agent dir** `settings.json`/`models.json` are rewritten only when their content differs. The `models.json` content is a C# constant (copied from Fractory's `PI_MODELS`). *Change:* `PiReviewer.Models` / `AgentSettings`.
- **Pi output parsing** is lenient (code fence or prose around the JSON is stripped) before validation; a non-JSON final message goes to the repair turn.
- **Transient (retried once by the CLI):** text matching 429/5xx/overloaded/rate limit/network errors, unless it also matches quota/billing/auth (402, `insufficient_quota`, 401/403, invalid key). Applies to Pi `stopReason=error`, a Pi/Claude process that ends without a terminal event (by its stderr), and Claude error results with a 5xx status. *Change:* `CliReviewer.IsTransient`.
- **Claude quota:** a `rate_limit_event` with a non-`allowed*` status counts as quota only if neither `isUsingOverage` nor `overageStatus == "allowed"`; the call isn't killed for it, but if it then fails, the error is "Claude quota exhausted until <local time>", never retried. 429 / "usage limit|rate limit|limit reached|spend limit" in an error result is also quota.
- **apiKeySource ≠ "none"** kills the Claude process tree at once and fails the call (not retried).
- **Claude's prompt** says "return your findings as the structured output" instead of "final message only JSON", since `--json-schema` is used; Pi's says the final message must be only the JSON.
- **Claude effort default** is `medium` when the config omits `effort`; Pi `thinking` default is `medium`.
- **Cost:** Claude reviewers cost 0 (shown as "subscription"); the extra-usage case isn't priced. For Pi, the resolved `provider/model` is looked up first, then the configured one.
- **Every CLI invocation writes the default config if it is missing**, so `bobar-cli runs` is enough to create it.
- **smoke.ps1** builds the CLI, makes a scratch repo with an off-by-one, uses a temp config dir (your `keys.env` copied in) and a temp data dir, runs one `start`, prints usage/cost, and cancels the run. `opus` runs as `haiku` (effort `low`) unless `-RealOpus`. `-Keep` keeps the temp dir. Ran it with `-Reviewer opus` (haiku): OK. Not run with `ds`/`sol` (no `keys.env` on this machine, and they cost money).
- **install.ps1** publishes both exes self-contained single-file into `dist\`, copies to `%LOCALAPPDATA%\Programs\BobarCode`, adds that to the user PATH, makes a Start menu shortcut, and copies the skills (`breview`, later also `bplan`) to `~\.claude\skills\<name>\`. It closes a running `bobar.exe` from the install dir first. Not run here (it changes your PATH and Start menu).
- **The skill** is the spec's text plus a `busy`/`decided` line and a hint about `runs`/`show` after a lost session.

## M3: GUI

- **Run list = one flat `ListBox`**, sorted by repo group (groups ordered by their newest run) and newest first within a group. The first run of each group shows the repo name as a header. Simpler than a TreeView and keeps single selection trivial. Selection survives re-sorting.
- **Refresh model:** every second (plus immediately on any FileSystemWatcher event, coalesced), each run's `events.jsonl` is stat'ed and re-folded only if its size or mtime changed. Live parts (elapsed times, ages, idle) are recomputed every tick. No per-run watchers.
- **Badges:** running, waiting for author, awaiting decisions, done, error, cancelled, idle. **Awaiting decisions is never shown as idle** (it's waiting on you, not on a dead process). Idle = unfinished, no live `bobar-cli` holding `run.lock`, and no event for over an hour.
- **Last reviewer activity** for the live "…" line is `reviewer.jsonl`'s mtime for the round in progress.
- **Cancel needs two clicks** within 4 s ("Click again to cancel") instead of a confirm dialog (Avalonia has no built-in message box).
- **Details is a modal window** (`ShowDialog`) that re-renders all rounds' `reviewer.jsonl` through `ReviewerLogFormatter` once a second when a file changed, with a "Follow" checkbox that keeps it scrolled to the end.
- **Single instance:** a `Local\` named mutex; the second instance sends its args over a per-user named pipe (`CurrentUserOnly`), retrying for ~3 s while the first starts up, and calls `AllowSetForegroundWindow(ASFW_ANY)` so the first may take focus. The first brings its window up with a Topmost flip.
- **`--run <id>`** accepts a unique prefix too, and is remembered until the run appears (the CLI may launch the GUI a moment before the GUI's first scan).
- **The GUI doesn't read config.json** (it has no use for its values yet); it only creates it for "Open config". It reloads `keys.env` when it changes, for the redactor used when writing `decisions.json`.
- **Theme follows the system** (Fluent, `RequestedThemeVariant="Default"`); badge colours are fixed and readable on both.
- Exchange/Details text use a monospace font and `SelectableTextBlock`, so text can be copied.

## M4: decisions

- **The decisions panel is built from `awaiting_decisions.ids`** (the ids the author rejected in the last round), each card with its thread back through `previousId` (`RunReader.Thread`), shown in a collapsed "Earlier rounds" expander. The card shows the reviewer's explanation, refs (with path warnings) and the author's reasoning.
- **Fix it / Dismiss are two radio buttons** (grouped per card), and **Add comment** reveals a text box. Submit is enabled once every card has a choice. The panel object is kept across refreshes while the same decision is pending, so typed text survives the 1 s refresh.
- **After Submit** the panel disappears and a green banner says the decisions are submitted and that `bobar-cli review wait` picks them up. The feed shows "You decided: …" from `decisions_submitted`.
- **Submit validates** that every awaited id is decided exactly once (Core `Validation.Decisions`), writes `decisions.json` atomically, then appends `decisions_submitted` under `run.lock` (10 s patience). If the lock can't be had, `decisions.json` alone is enough: `wait` appends the missing event.
- **`wait` ends the run** (`run_finished(decided)`) and prints each decision; for "fix it" findings it repeats the explanation and refs so Claude has what it needs without re-reading files. Calling `wait` again on a decided run reprints the decisions.
- **`openGuiOnDecisions`** launches `bobar.exe --run <id>` from the CLI's own folder (so it works from `dist\`/the install dir, and silently does nothing in a dev `bin\` folder where there is no `bobar.exe`). It uses ShellExecute with `ArgumentList`, so the GUI doesn't inherit the CLI's stdout pipe: verified that capturing the CLI's output completes in under a second while the GUI cold-starts.
- **Verified by hand** (fake reviewer, published `dist\`): deadlock → GUI opened on the run → Dismiss + Submit via UI Automation → `wait` printed `decided` → the GUI showed the run as done.
- **README.md** added with build/install/use notes.

## After M4: `/bplan` (plan review)

- **Plans are reviewed through the unchanged CLI.** `/bplan` writes the agreed plan to an uncommitted file (default `plan.md`), which shows up in the diff as a whole new file, and runs breview's loop with a `--desc` that says to review it as a plan. Accepted limits: other uncommitted changes get reviewed too; a plan file that is committed (with no edits), ignored or outside the repo isn't in the diff, so bplan checks `git -C <root> status --porcelain -uall -- <file>` and stops (`-uall` matters: without it, a wrong path inside an untracked directory still matches the directory); a tracked plan with edits shows only its hunks, so the desc tells the reviewer to read the whole file; and the reviewer prompt says "code reviewer". *Change:* a `review start --plan <file>` flag with a plan prompt, if these start to matter.
- **An existing plan file is reviewed as it is**, never rewritten from the conversation, so another reviewer can be run on the improved plan with the same command.
- **Fixes are in-place edits**, with no versioned copies (`plan1.md`): a second copy would be reviewed too, since both are uncommitted.
- **bplan defers the loop to breview's SKILL.md** instead of repeating it, so loop changes are made in one place.

## After M4: reviewer instructions

- **One global `reviewerInstructions` string in config.json, no per-repo `bobar.json`.** Project rules go in the repo's `CLAUDE.md` / `AGENTS.md`, which the reviewer is already told to read and the author reads too; a `bobar.json` would be a second, reviewer-only copy of them. The global field is for taste that follows the user across repos. It's empty by default, so no design bias ships to everyone. *Change:* `AppConfig.ReviewerInstructions`, `PromptBuilder`.
- **Read fresh each round** (like the rest of config.json), not frozen in `run_started`. Each round's `prompt.md` records what was sent.
- **A Settings window edits only this field** (a multi-line text box, which is painful as a JSON string). Everything else is still edited through "Open config". Saving reloads and validates config.json first and refuses to overwrite a broken file; it rewrites the file from the parsed config. The box shows "e.g. Prefer lightweight designs." as a placeholder, not as a value.
- **Built-in "later rounds" line:** from round 2 on, the prompt says new findings should be as serious as round-1 ones, and that problems the fixes introduced still count. Prompted by a run where `ds` raised new, ever smaller edge cases in all 5 rounds. Not configurable: it's about review quality, not taste.

## After M4: Summary table

- **The Summary tab is a table, one row per round** (`SummaryTable.Build` in Core, pure and unit-tested); the CLI keeps printing the sentence feed (`Summary.Lines`). Click a round to list its findings; rows update in place each second, so an open round stays open. Mocked up first (light, dark, live and decisions states) and chosen over "all rounds open".
- **Total row** sums findings reported (a maintained finding counts again in its round), fixes, rejections, reviewer time and cost ("cost unknown" if any round lacks a price).
- **Colours are theme resources** in `App.axaml` (light and dark), not hard-coded brushes, so the table follows the system theme.

## After M4: hindsight

- **The CLI asks for the hindsight, not just the skill.** The `done`/`decided`/`error` output ends with a "## Hindsight" section that lists the rating anchors and gives the command template, the same way `findings` shows the response format. So the prompt lives in one place (`CliOutput.HindsightRequest`), and `show` on a finished run asks again until a hindsight is in. The template uses placeholders (`<1-10>`, `"..."`), not sample values, so it doesn't anchor the rating.
- **A separate command, after `run_finished`:** `review hindsight <run> --input -` writes `hindsight.json` and appends `hindsight_submitted` (with the counts, so the fold and the Reviewers stats don't need to read files). It never touches the outcome. Sending it again replaces it (the fold keeps the last event, and the file is overwritten), so a slip can be corrected without editing files. Refused while the run is unfinished, and for cancelled runs. It takes `run.lock` like other writers.
- **Validation is strict about ids.** `acceptedWrongly` must be ids the author answered `fix`, and `rejectedWrongly` ids it answered `reject`, in any round. User-ordered fixes aren't the author's calls, so they're excluded, but rejections the user sided with are allowed. Each item needs a `why`, and `notes` must be non-empty. A failure is `invalid_hindsight`, exit 2, like `invalid_response`.
- **Error runs ask for a hindsight too.** "The reviewer died after two useful rounds" is worth recording. Cancelled runs don't.
- **Reviewers window:** rows are grouped by shorthand plus the configured reviewer kind, provider and model from `run_started` (the Model column shows provider/model), so remapping a shorthand doesn't mix two models' stats (or API billing with the subscription).
  - Runs and cost/time per run cover all finished, non-cancelled runs.
  - Usefulness and the wrongly-accepted/rejected rates cover only rated runs ("N of M fixes" over the fixes in rated runs).
  - Cost and time per run leave out error runs, because the failed call's time and cost aren't in any event, so those runs would look cheap and fast. Cost averages the remaining runs whose cost is known.
  - The window is a snapshot taken when it opens, with no live updates.
- **Regretted findings expand in place**, like round rows, and don't jump to the round table. The expanded row shows what you need to judge the regret: the reviewer's explanation and refs, what the author answered during the run, and your decision if there was one (`HindsightView.Build` joins review.json, response.json and decisions.json). Expansion survives the 1 s refresh because the section only rebuilds when events change.
- **Notes are plain text**, like the rest of the GUI for now (the spec's "Markdown rendering can come later").
- **`runs` gets a hindsight column** (`7/10`, `pending`, or empty for cancelled/unfinished runs).
