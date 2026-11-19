# Infra findings: models, providers, prices, CLI behaviour

Carried over from Fractory (`D:\Fractory\docs\interfaces.md` and `docs\decisions-todo.md`, 2026-11-07…10). Verified on Windows 11 with Claude Code 2.1.251 and Pi 1.5.0. Each item is **verified** (seen in real calls), **doc** (read from docs or source) or **unverified**. CLI flags change between versions: re-check `claude --help` / `pi --help` after upgrades, and keep this file current when you learn something new.

## 1. Models and prices

All prices are USD per 1M tokens. A cached input is a subset of input.

| Shorthand | Provider / model id | Input | Output | Cache read | Cache write | Source, checked |
|---|---|---|---|---|---|---|
| `ds` | `lithos/deepseek-ai/DeepSeek-V4.1-Flash-ultra` | 0.35 | 1.40 | 0.007 | none exists | lithosai.com/pricing "Ultra" tier, **early-access discount** (the page also shows $2.80 output, probably the undiscounted price); not checked in the console. 2026-11-10 |
| `sol` | `openai/gpt-6.2-sol` | 2 | 10 | 0.10 | 2.50 | User-stated: same price as 6.1 Sol (6.1 rates came from the Pi 1.5.0 catalog + third-party trackers). **Not in Pi 1.5.2's catalog** (even after `pi update --models`), so `PiReviewer.Models` declares it. **verified** (smoke 2026-11-19: id accepted, Pi reports thinking medium -> medium, 14 s); price **not checked on openai.com**. Standard service tier, for prompts ≤ 272k. 2026-11-19 |
| | `openai/gpt-6.1-sol` | 2 | 10 | 0.10 | 2.50 | Previous `sol`. Pi 1.5.0 catalog + third-party trackers (pricepertoken.com, getdeploying.com); not checked on openai.com. 2026-11-10 |
| `opus` | `anthropic/claude-opus-5` | 5 | 25 | 0.50 | 10 (1-hour rate) | Reproduced from Claude Code's `total_cost_usd`; not checked on anthropic.com. Matters only for extra usage (see §2). 2026-11-10 |
| | `anthropic/claude-haiku-4-5-20251001` | 1 | 5 | 0.10 | 2 | Same source. Claude Code makes small background Haiku calls, which also count as extra usage. |
| | `lithos/deepseek-ai/DeepSeek-V4.1-Flash` (base tier) | 0.15 | 0.60 | | | Lithos pricing page; not used yet. |
| | `baseten/deepseek-ai/DeepSeek-V4.1-Flash-1017-Fast` | 0.60 | 2.40 | 0.014 | | Baseten `GET https://inference.baseten.co/v1/models` (`pricing` field). 2026-11-09 |
| | `baseten/deepseek-ai/DeepSeek-V4.1-Flash-1017` | 0.30 | 1.20 | 0.007 | | Pi 1.5.0 Baseten catalog (half the Fast price). 2026-11-10 |
| | `baseten/zai-org/GLM-5.3-Flash` | 0.15 | 0.50 | 0.03 | | Baseten `/v1/models`. 2026-11-09 |
| | `together/zai-org/GLM-5.3` | 1.40 | 4.40 | 0.26 | | together.ai pricing + model page (which also showed a "launching soon" banner). 2026-11-09 |
| | `together/deepseek-ai/DeepSeek-V4.1-Flash(-1017)` | 0.30 | 1.20 | 0.006 | | together.ai/pricing. 2026-11-07 |

Pricing notes:
- **Vendor cost fields are wrong or misleading, so compute cost from tokens × this table.**
  - Pi's `usage.cost` used Pi's own catalog guess for Together ($3/$15, ten times the real price).
  - Pi's vendor cost for Sol was exactly 2× the standard rates, because the OpenAI project's default service tier was "fast" (priority). The user switched it to Standard on 2026-11-10.
- **Claude on the Pro subscription costs nothing per call.** Extra usage ("overage", see §2) is billed at API rates.
  - Usage doesn't split 5-minute from 1-hour cache writes, so the 1-hour rate (2× input) is an upper bound; 5-minute writes cost 1.25× input.
  - Claude Code reports usage once per invocation, so an invocation that touched overage at any point counts entirely as overage.
- **Baseten reasoning tokens are included in `completion_tokens`** (and Pi's `usage.output`), so tokens × price already covers them. **verified** (probe). Billing was inferred, not checked against an invoice.

## 2. Claude Code (`claude -p`)

**Executable:** `C:\Users\<u>\.local\bin\claude.exe`, a native binary (not an npm shim).

**Read-only invocation:** **verified**
```
claude -p --output-format stream-json --verbose --model <opus|haiku|…> --effort <low|medium|high|xhigh|max>
  --session-id <uuid>          (fresh, id we choose)  |  -r <uuid>  (resume; same id kept)
  --tools Read,Grep,Glob --permission-mode dontAsk
  --setting-sources ""         (no user/project settings, no CLAUDE.md, including the global one; auth still works)
  --strict-mcp-config          (no MCP servers)
  --add-dir <dir>              (lets Read reach files outside the cwd; otherwise denied)
  --json-schema '<schema>'     (structured output → result.structured_output; result.result has the same JSON as a string)
  [--no-session-persistence]
  < prompt on stdin
```

**Auth and env:**
- `--verbose` is required with `stream-json` in `-p` mode.
- **Never `--bare`.** It forces `ANTHROPIC_API_KEY` auth and ignores the subscription login.
- Delete `ANTHROPIC_API_KEY`, `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN` from the child env. Auth still works without them. **verified**
- `system/init.apiKeySource == "none"` means the subscription login is in use. Assert it.

**Stream:** `system/init` → `rate_limit_event` → `assistant`* / `user`* (tool results) → `result`.
- **Resolved model:** `system/init.model` (e.g. `claude-opus-5`), plus the keys of `result.modelUsage`, which also include background Haiku calls.
- **Effort is not echoed anywhere**, so log the requested value.
- **Usage:** `result.usage` (`input_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `output_tokens`) and `modelUsage` per model.

**Quota and extra usage:**
- Every call emits an early `rate_limit_event`. `rate_limit_info` carries:
  - `status` (`allowed`, `allowed_warning`, `rejected`);
  - `resetsAt` (epoch **seconds**);
  - `rateLimitType`;
  - `unifiedWindows.{five_hour,seven_day}.utilization` (0–1).
- Once relevant, it also carries `overageStatus`, `isUsingOverage`, `overageResetsAt` and `overageDisabledReason`.
- `status:"rejected", overageStatus:"allowed", isUsingOverage:true` means it keeps working on extra usage at API rates. **verified** 2026-11-10
- `overageStatus:"rejected"` (e.g. `overageDisabledReason:"org_level_disabled_until"`, the monthly cap) ends the call with an `is_error` result ("You've hit your monthly spend limit …"). **verified**
- Also treat as quota: `is_error` with `api_error_status` 429, or result text matching `/usage limit|rate limit|limit reached/i`. Never retry a Claude quota error.

**Gotchas:**
- Claude cites absolute paths as well as relative ones; normalize them to repo-relative.
- BobarCode smoke, 2026-11-17 (Claude Code 2.1.251): `--model haiku --effort low` with `--json-schema` and `--setting-sources ""` resolved to `claude-haiku-4-5-20251001`, `apiKeySource` was `"none"`, and `result.structured_output` held the findings. One review of a 5-line diff: 13 s, ~32k cache-read tokens (Claude Code's own system prompt). **verified**
- Its built-in system prompt includes env info and git status.

## 3. Pi (`pi --mode json`)

**Executable:** `C:\Users\<u>\AppData\Roaming\npm\pi.cmd`, an npm shim for `node …\@earendil-works\pi-coding-agent\dist\bundle\cli.js`. Spawn `node` + that JS directly; going through `cmd.exe` mangles arguments.

**Read-only invocation:** **verified**
```
pi --mode json --provider <p> --model <id> --thinking <off|minimal|low|medium|high|xhigh|max>
  --no-extensions --no-skills --no-prompt-templates --no-mcp --no-context-files
  --tools read,grep,find,ls
  --session-dir <dir>  [--session <id>]  (resume by full/partial id)  |  --no-session
  < prompt on stdin
env: PI_CODING_AGENT_DIR=<our own agent dir>, plus the provider key
```

**Prompts and isolation:**
- **Never pass prompts as `@file`.** Pi wraps the file in `<file>` tags, and the model treated the instructions as untrusted content: it refused, calling it prompt injection. Use stdin, which arrives as a plain user message. **verified**
- **Isolated agent dir:** `PI_CODING_AGENT_DIR` pointing at our own dir keeps your personal Pi config out of it. Pi creates an empty `auth.json` there and does **not** persist the key. **verified**
- **Filesystem:** Pi's built-in tools are **not** confined to the cwd: `read` and `write` on absolute paths elsewhere both succeed. **verified** Never give it `bash` or `powershell` (Windows: `bash` = Git Bash).

**Stream:** `session` (first line, carries the session `id`) → `agent_start` → `turn_start` → `message_start/end` → `message_update`* → `tool_execution_start/end` → `turn_end` … → `agent_end{willRetry}` → `agent_settled{aborted}`.
- **Final text:** the text parts of the **last `message_end` with `role:"assistant"`**. `message_end` also appears for system and user messages, so filter on role.
- **Usage:** sum `message_end(role=assistant).message.usage` (`input`, `output`, `cacheRead`, `cacheWrite`, `reasoning`, `totalTokens`). `usage` on `message_update` is cumulative per response, so summing it double-counts.
- **Model and reasoning level:** `message_end.message.{provider, model, thinkingLevel}`. `thinkingLevel` is the effective level Pi sent.
- **Errors arrive with exit code 0.** A bad API key gives exit 0 with `stopReason:"error"` and an `errorMessage`. So treat `stopReason ∈ {error, aborted, length}` on the last assistant message as failure. **verified**
- **JSONL framing:** split on `\n` only (strip a trailing `\r`). Line readers that also split on U+2028/U+2029 break JSON strings.

**Retries:** **doc** (Pi 1.5.0 source and docs). Put this in the agent dir's `settings.json`:
```json
{ "retry": { "enabled": true, "maxRetries": 4, "baseDelayMs": 2000, "maxAgentDelayMs": 60000,
             "provider": { "maxRetries": 4, "maxRetryDelayMs": 60000 } } }
```
It gives two levels of retries:

| Level | Retries | Backoff | Visible in the stream |
|---|---|---|---|
| **Provider** (one HTTP request) | 429/408/409/5xx/connection errors | honours `retry-after` / `retry-after-ms` up to 60 s, otherwise exponential 0.5–8 s | no |
| **Agent** (the whole turn) | retryable error texts | 2, 4, 8, 16 s | yes: `auto_retry_start{attempt,maxAttempts,delayMs,errorMessage}` / `auto_retry_end{success,attempt,finalError?}` |

- A server-requested delay above 60 s fails at once with `Server requested Ns retry delay (max: 60s)`.
- Quota and billing texts (`insufficient_quota`, `quota exceeded`, `billing`) are not retried.
- Worst case: about 25 HTTP attempts per turn, over 2–5 minutes.

**Context loading:** **verified** (canary tests)
- With context files on, Pi loads the repo `AGENTS.md` and its agent-dir `AGENTS.md`. It does **not** load `CLAUDE.md` when an `AGENTS.md` exists in the same dir.
- `--no-context-files` loads none of them.

## 4. Providers

### LithosAI (DeepSeek)
Used since 2026-11-10, after Baseten 429s. **verified** (tool round trip, caching, reasoning reported).

**Pi setup:**
- **Pi has no built-in `lithos` provider.** Write it to the agent dir's `models.json`:
  ```json
  { "providers": { "lithos": {
      "name": "LithosAI", "baseUrl": "https://api.lithosai.cloud/v1", "api": "openai-completions",
      "apiKey": "$LITHOS_API_KEY", "authHeader": true,
      "compat": { "supportsStore": false, "supportsDeveloperRole": false, "supportsReasoningEffort": true,
                  "supportsUsageInStreaming": true, "maxTokensField": "max_tokens", "thinkingFormat": "openai",
                  "sendSessionAffinityHeaders": false, "supportsLongCacheRetention": false },
      "models": [ { "id": "deepseek-ai/DeepSeek-V4.1-Flash-ultra", "name": "DeepSeek V4.1 Flash Ultra (LithosAI)",
                    "reasoning": true, "input": ["text"], "contextWindow": 1048576, "maxTokens": 262144,
                    "thinkingLevelMap": { "off": "none", "minimal": null, "low": "low", "medium": null, "high": "high", "xhigh": null, "max": "max" },
                    "cost": { "input": 0.35, "output": 1.4, "cacheRead": 0.007, "cacheWrite": 0 } } ] } } }
  ```
  `$LITHOS_API_KEY` is Pi's env interpolation, so the file holds no secret.
- **Compat settings:** Lithos returns **400 for undeclared request fields**. That's why `store`, the developer role, session-affinity headers and long cache retention are off, and why the field is `max_tokens` rather than `max_completion_tokens`.

**Models and limits:**
- **Model ids:** `…-Flash`, `…-fast` and `…-ultra` serve the same weights with different prices and rate limits.
- **Context and output:** 1,048,576 context, max output 393,216. Input + `max_tokens` must fit the context, or the request gets a 400. Hence `maxTokens` 262,144.
- **Reasoning:** it accepts none/minimal/low/medium/high/xhigh/max/ultra but **runs only off/low/high/max**: minimal → low; medium, xhigh → high; ultra → max. Request `high` explicitly.

**Errors and limits:**
- **Rate limits** are per model and organization: requests, input tokens and output tokens per minute. 429 bodies say `rate_limit_exceeded` or `provider_overloaded` and carry `retry-after` / `retry-after-ms`.
- **402 `insufficient_quota`** = the prepaid balance is empty.
- **Auth:** `Authorization: Bearer`, from env `LITHOS_API_KEY`.
- **Replaying assistant turns without `reasoning_content` works.** **verified**

### OpenAI (GPT-6.2 Sol)
Through Pi's built-in `openai` provider (`api: "openai-responses"`), key env `OPENAI_API_KEY`.
- **Catalog:** context 272k, max output 128k (6.1 Sol's values). Pi 1.5.2 doesn't ship 6.2 Sol, so BobarCode's agent-dir `models.json` adds it to the built-in `openai` provider (a `models` entry there adds a model and keeps the built-in ones and auth). Without the entry Pi would pass the unknown id through with no reasoning metadata. Drop the entry once Pi ships it. `pi update --models` refreshes catalogs; plain `pi update` doesn't.
- **Reasoning:** off/minimal → not sent; low/medium/high/xhigh/max → the same `reasoning.effort`.
- **Status:** Pi reports cache writes for it. Fractory used it as a reviewer; prices are **unverified** on openai.com.
- **Service tier:** keep the OpenAI project on the Standard tier. The priority ("fast") tier bills 2×.

### Baseten (no longer the default)
- Provider id `baseten`, key env `BASETEN_API_KEY`. **verified**
- **"Fast" variants:** same weights on dedicated capacity, at a separate price (2×) with their own rate limits.
- **Rate limits:** the Basic tier is 15–120 RPM and 100k–500k TPM; Pro is 120 RPM and 1M TPM. Fractory hit 429s, which is why DeepSeek moved to Lithos.
- **Reasoning levels (Pi catalog):**
  - DeepSeek V4.1 Flash 1017 (Fast): off/low/medium/high/max.
  - GLM-5.3 Flash: **low/high/max** only (medium → high).
- **Latency:** one GLM turn took 75 s for 84 output tokens and 0 reasoning tokens. Direct requests took 0.5–2 s. Most likely provider-side queueing or a cold start, not hidden reasoning.

### Together (no longer used)
- Provider id `together`, key env `TOGETHER_API_KEY`.
- **DeepSeek-V4.1-Flash reasoning:** only **off** and **high** are effective; everything else clamps to high. **verified**
- **Model id:** Pi's catalog lists `deepseek-ai/DeepSeek-V4.1-Flash`. The `-1017` id works too, with a "custom model id" warning on stderr.

## 5. Windows process notes
- **Killing:** killing the whole process tree on timeout cleanly killed `pi.cmd → cmd.exe → node.exe`, with no orphans. **verified** In .NET: `Process.Kill(entireProcessTree: true)`.
- **Killed output:** it ends mid-stream with no terminal event (`result` / `agent_settled`). Treat a missing terminal event as killed or timed out.
- **Line endings:** `core.autocrlf=true` on this machine. For stable diffs use `git -c core.autocrlf=false diff --ignore-cr-at-eol`.
- **Prompts never go on argv**, which avoids the 32K command-line limit and `cmd.exe` quoting.
- **Smart App Control** blocks unsigned local builds: `dotnet test` fails with "An Application Control policy has blocked this file (0x800711C7)" and the exes give "Permission denied". **verified** It is turned off on this machine (2026-11-17). Check with `Get-MpComputerStatus | Select SmartAppControlState`.
