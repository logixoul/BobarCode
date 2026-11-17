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
- **Ctrl+C** during a review ends the run as `cancelled` ("interrupted (Ctrl+C)").
- **`start` with no changes** fails before a run is created: no run dir appears.
- **`next` on a run that isn't waiting for the author** prints the run's current state (like `show`), prefixed with "nothing to respond to", exit 1.
- **New statuses beyond the spec's table:** `busy` (another process holds `run.lock`; exit 1) and `running` (`show` while the reviewer works). Exit codes: 0 for normal results, 1 for `error`/`busy`/misuse, 2 for `invalid_response`.
- **`runs` prints a Markdown table** without a `STATUS:` line (the spec says its status is n/a).
- **Run ids can be abbreviated**: any command accepts a unique prefix or suffix of a run id.

**Locking and concurrency**
- **`run.lock` is a `FileStream` opened read/write with `FileShare.Read`.** A second writer fails at once; readers can still read the pid. The file is left behind when released (the pid in it is then stale).
- **`wait` and `show` don't hold the lock.** `wait` polls once a second and takes the lock only briefly to append its final events, so `cancel` never kills a waiting `wait`.
- **The GUI's "is anyone holding it" check reads the pid** and checks for a live `bobar-cli` process instead of trying to take the lock (which could make a starting CLI command fail as `busy`).
- **`cancel` kills the pid in `run.lock` only if that process is named `bobar-cli`** (never itself), then waits up to 10 s for the lock and appends `run_finished(cancelled)`. A killed `start`/`next` simply dies, with no output for Claude; `show` then reports `cancelled`.

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
- **install.ps1** publishes both exes self-contained single-file into `dist\`, copies to `%LOCALAPPDATA%\Programs\BobarCode`, adds that to the user PATH, makes a Start menu shortcut, and copies the skill to `~\.claude\skills\lx-review\`. It closes a running `bobar.exe` from the install dir first. Not run here (it changes your PATH and Start menu).
- **The skill** is the spec's text plus a `busy`/`decided` line and a hint about `runs`/`show` after a lost session.
