# BobarCode

A lightweight Windows and macOS tool that runs a review loop between a reviewer model and your live Claude Code session. Spec: [spec.md](spec.md). Decisions taken while building it: [DECISIONS_MADE.md](DECISIONS_MADE.md). CLI flags, provider quirks and prices: [INFRA_FINDINGS.md](INFRA_FINDINGS.md).

- `bobar-cli` runs the loop (Claude calls it from the `/breview` skill for code, or `/bplan` for a plan).
- `bobar` (BobarCode.app on macOS) shows the runs and is where you decide disagreements.

## Build and test

```
dotnet build
dotnet test          # FakeReviewer only, no model calls
```

## Install

```
powershell -File scripts\install.ps1
```

Publishes both exes into `dist\`, copies them to `%LOCALAPPDATA%\Programs\BobarCode`, adds that to your user PATH, adds a Start menu shortcut and installs the skills to `~\.claude\skills\` (`breview` and `bplan`).

On macOS:

```
scripts/install.sh
```

Publishes both into `dist/`, wraps them in `BobarCode.app` in `~/Applications`, links `~/.local/bin/bobar-cli` to the CLI inside it (adding `~/.local/bin` to PATH in `~/.zprofile` if needed) and installs the skills to `~/.claude/skills/`. Needs the .NET 11 SDK; `claude` (and `pi`, for Pi reviewers) must be on the PATH your Claude Code shell sees.

Then put your provider keys in `%APPDATA%\BobarCode\keys.env` (macOS: `~/Library/Application Support/BobarCode/keys.env`):

```
LITHOS_API_KEY=...
OPENROUTER_API_KEY=...
```

Reviewers live in `config.json` beside it (created with defaults on first use; "Open config" in the app opens it).

To steer every review, in every repo, write reviewer instructions under "Settings" in the app (stored as `"reviewerInstructions"` in config.json), e.g. "Prefer lightweight designs.". Put rules for one project in that repo's `CLAUDE.md` / `AGENTS.md` instead: the reviewer reads them, and so does Claude when it writes the code. They win when the two conflict.

## Use

In Claude Code, with uncommitted changes: `/breview ds` (or `sol`, `hydrogen`, `qwen4`, `glm`, `terra`, `luna`, `neon`, `opus`). To also review the last 3 commits (say you forgot to review before committing): `/breview ds 3`. To try the loop without model calls: `/breview fake`.

To review a plan you've agreed on with Claude: `/bplan ds` (or `/bplan ds docs/plan.md`). Claude writes the plan to `plan.md` in the repo root, unless the file already exists, and the reviewer and Claude iterate on it in place. Run it again with another reviewer to review the improved plan. The plan file must stay uncommitted while it's being reviewed, so bplan stops if it's committed with no edits since. After the last plan review, commit, delete or .gitignore it, or it will show up in your next `/breview`.

To have several models discuss an open question (product design, or anything: speakers to buy, a gym routine): `/bdiscuss ds,opus <your question>`, with the command first and your question after it, word for word. `/bdiscuss ds,opus` alone makes Claude summarize the question you've been discussing. Add `--rounds=<n>` right after the models to change the number of rounds (default 4). Each model first answers on its own; then, every round, each sees the others' latest solutions and messages and revises its own (messages in all but the last round). Claude then reads the final solutions, settles the disagreements it safely can, asks you about the rest in BobarCode, and writes a report: merged final solutions, its recommendation, and the ideas it left out and why. In VS Code the models can read the project folder (read-only); in a Claude desktop session without a folder they get only web tools.

Every reviewer and discussion participant has **web tools**: Claude its built-in WebSearch/WebFetch; Pi reviewers `web_fetch`, plus `web_search` via the Brave Search API when keys.env has `BRAVE_API_KEY` (each search is priced at `webSearch.pricePerCall` in config.json, $0.005 by default).

After every run (except a cancelled one), Claude sends its **hindsight**: a 1–10 usefulness rating for the whole run, the findings it now thinks it shouldn't have fixed or rejected, and notes on what BobarCode should change. The app shows it under the run's Summary, puts the rating next to the run in the list, and the **Reviewers** button compares reviewers (average usefulness, cost and time per run) and collects all the notes.

By hand:

```
bobar-cli review start --reviewer fake --desc - <<< "what changed"
bobar-cli review next <run> --response - <<< '{"responses":[{"id":"R1.1","action":"fix","reasoning":"..."}]}'
bobar-cli review wait <run>
bobar-cli review show <run>
bobar-cli review cancel <run>
bobar-cli review hindsight <run> --input - <<< '{"usefulness":7,"acceptedWrongly":[],"rejectedWrongly":[],"notes":"..."}'
bobar-cli runs

bobar-cli discuss start --models ds,opus --slug my-topic [--dir .] [--rounds 4] --prompt - <<< "the question"
bobar-cli discuss ask <run> --questions - <<< '{"questions":[{"id":"Q1","title":"...","context":"...","options":[{"label":"A"},{"label":"B"}]}]}'
bobar-cli review wait <run>
bobar-cli discuss report <run> --input - <<< "# Report"
bobar-cli review hindsight <run> --input - <<< '{"score":8,"reason":"...","notes":"..."}'
```

## Smoke test (real model, opt-in)

```
powershell -File scripts\smoke.ps1                  # ds via Pi: a few cents
powershell -File scripts\smoke.ps1 -Reviewer opus   # Claude as haiku, on the subscription
powershell -File scripts\smoke.ps1 -Web             # the reviewer must search and fetch; fails unless both succeed
```

On macOS, run the same script with `pwsh scripts/smoke.ps1` (`brew install powershell`).

## App icon

The icon source is `src\Bobar.App\Assets\icon.png`. The app uses `icon.ico` beside it: it's built into `bobar.exe` and used for both windows. After replacing the PNG, regenerate the `.ico` and rebuild:

```
powershell -File scripts\make-ico.ps1
```

`BOBAR_CONFIG_DIR` / `BOBAR_DATA_DIR` override where config and runs live; `BOBAR_FAKE_SCRIPT` scripts the fake reviewer (see DECISIONS_MADE.md).
