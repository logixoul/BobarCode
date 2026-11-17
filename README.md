# BobarCode

A lightweight Windows tool that runs a review loop between a reviewer model and your live Claude Code session. Spec: [spec.md](spec.md). Decisions taken while building it: [DECISIONS_MADE.md](DECISIONS_MADE.md). CLI flags, provider quirks and prices: [INFRA_FINDINGS.md](INFRA_FINDINGS.md).

- `bobar-cli.exe` runs the loop (Claude calls it from the `/lx-review` skill).
- `bobar.exe` shows the runs and is where you decide disagreements.

## Build and test

```
dotnet build
dotnet test          # FakeReviewer only, no model calls
```

## Install

```
powershell -File scripts\install.ps1
```

Publishes both exes into `dist\`, copies them to `%LOCALAPPDATA%\Programs\BobarCode`, adds that to your user PATH, adds a Start menu shortcut and installs the skill to `~\.claude\skills\lx-review\`.

Then put your provider keys in `%APPDATA%\BobarCode\keys.env`:

```
LITHOS_API_KEY=...
OPENAI_API_KEY=...
```

Reviewers live in `%APPDATA%\BobarCode\config.json` (created with defaults on first use; "Open config" in the app opens it).

## Use

In Claude Code, with uncommitted changes: `/lx-review ds` (or `sol`, `opus`). To try the loop without model calls: `/lx-review fake`.

By hand:

```
bobar-cli review start --reviewer fake --desc - <<< "what changed"
bobar-cli review next <run> --response - <<< '{"responses":[{"id":"R1.1","action":"fix","reasoning":"..."}]}'
bobar-cli review wait <run>
bobar-cli review show <run>
bobar-cli review cancel <run>
bobar-cli runs
```

## Smoke test (real model, opt-in)

```
powershell -File scripts\smoke.ps1                  # ds via Pi: a few cents
powershell -File scripts\smoke.ps1 -Reviewer opus   # Claude as haiku, on the subscription
```

`BOBAR_CONFIG_DIR` / `BOBAR_DATA_DIR` override where config and runs live; `BOBAR_FAKE_SCRIPT` scripts the fake reviewer (see DECISIONS_MADE.md).
