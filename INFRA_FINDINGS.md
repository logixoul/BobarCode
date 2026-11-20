# Infra findings: models, providers, prices, CLI behaviour

Carried over from Fractory (`D:\Fractory\docs\interfaces.md` and `docs\decisions-todo.md`, 2026-11-07…10). Verified on Windows 11 with Claude Code 2.1.251 and Pi 1.5.0. Each item is **verified** (seen in real calls), **doc** (read from docs or source) or **unverified**. CLI flags change between versions: re-check `claude --help` / `pi --help` after upgrades, and keep this file current when you learn something new.

## 1. Models and prices

All prices are USD per 1M tokens. A cached input is a subset of input.

| Shorthand | Provider / model id | Input | Output | Cache read | Cache write | Source, checked |
|---|---|---|---|---|---|---|
| `ds` | `lithos/deepseek-ai/DeepSeek-V4.1-Flash-ultra` | 0.35 | 1.40 | 0.007 | none exists | lithosai.com/pricing "Ultra" tier, **early-access discount** (the page also shows $2.80 output, probably the undiscounted price); not checked in the console. 2026-11-10 |
| `sol` | `openrouter/openai/gpt-6.2-sol` | 2 | 10 | 0.10 | 2.50 | User-stated: same price as 6.1 Sol (6.1 rates came from the Pi 1.5.0 catalog + third-party trackers). **Not in Pi 1.5.2's catalog** (even after `pi update --models`), so `PiReviewer.Models` declares it (now under `openrouter`). **verified** (smoke 2026-11-19: id accepted, Pi reports thinking medium -> medium, 14 s); price **not checked on openai.com**. Standard service tier, for prompts ≤ 272k. OpenRouter 2026-11-20: same price (2 / 10 / 0.10 / 2.50), 5 s smoke, cache read on the first turn. 2026-11-19 |
| | `openai/gpt-6.1-sol` | 2 | 10 | 0.10 | 2.50 | Previous `sol`. Pi 1.5.0 catalog + third-party trackers (pricepertoken.com, getdeploying.com); not checked on openai.com. 2026-11-10 |
| `hydrogen` | `openrouter/google/gemini-4-hydrogen` | 1.50 | 7.50 | 0.075 | none | Released 2026-11-12. Third-party coverage (datastudios.org, mindstudio.ai, felloai.com) agrees; **not checked on Google's pricing page**. Flat across the 1M window. Standard tier (Batch/Flex half, Priority $2.70/$13.50). **Not in Pi 1.5.2's catalog** (even after `pi update --models`), so `PiReviewer.Models` declares it (now under `openrouter`). **verified** (smoke 2026-11-19: id accepted, Pi reports thinking medium -> medium, 8 s, 6.5k in / 0.5k out, $0.013, no cache read on a single turn); price **not checked on Google's pricing page**. OpenRouter 2026-11-20: same price, 10 s smoke, some cache read (576 tokens) on a single turn. 2026-11-19 |
| `qwen4` | `openrouter/qwen/qwen4-max` | 1.425 | 4.275 | 0.07125 | | OpenRouter 2026-11-20: served only by Alibaba (its maker) at these rates, 5% under Together; smoke 17 s, 5.1k cache read, Pi reports high -> high. Previously on Together: together.ai/pricing list price. A **promo** of $1.05 / $3.15 / $0.0525 cached runs until 2026-11-23 and isn't in the table. Released 2026-11-15; the model page still shows a "launching soon" banner. **Not in Pi 1.5.2's catalog**, so `PiReviewer.Models` declares it. **verified** (smoke 2026-11-20: endpoint live, id accepted, Pi reports thinking high -> high, 9 s, 2.7k in + 3.8k cache read / 0.4k out, $0.006, so caching works on a single turn); price **not checked against an invoice**. 2026-11-20 |
| `glm` | `openrouter/z-ai/glm-5.3` | 1.40 | 4.40 | 0.26 | | OpenRouter 2026-11-20, pinned to Z.AI then Together (same price at both; about 50 other hosts, some fp4 at $0.07 input): smoke 19 s, served by Z.AI (checked via `GET /api/v1/generation?id=`), 6.8k cache read, high -> high. Previously on Together: together.ai pricing + model page (2026-11-09, which also showed a "launching soon" banner); Pi 1.5.2's Together catalog agrees. In Pi's catalog, so no `models.json` entry. **verified** (smoke 2026-11-20: Pi reports thinking high -> high, 8 s, 2.1k in + 6.3k cache read / 0.6k out, $0.007); price **not checked against an invoice**. 2026-11-20 |
| | `together/MiniMaxAI/MiniMax-M3` | 0.30 | 1.20 | 0.06 | | Pi 1.5.2's Together catalog only (same as MiniMax's own API and OpenRouter there); **not checked on together.ai**. In Pi's catalog, so no `models.json` entry. **Broken:** smoke 2026-11-20 got 400 `endpoint_not_configured` ("Endpoint has no deployments configured for routing"): Together lists the model but doesn't serve it. 2026-11-20 |
| `terra` | `openrouter/openai/gpt-6.1-terra` | 1 | 5 | 0.05 | 1.25 | Pi 1.5.2's catalog + third-party coverage (benchlm.ai, apimaster.ai) agree; **not checked on openai.com**. ≤ 272k input; above that $2 / $7.50. In Pi's catalog, so no `models.json` entry. **verified** (smoke 2026-11-20: Pi reports thinking medium -> medium, 13 s, 3.4k cache read / 0.3k out, $0.004). OpenRouter 2026-11-20: same price, 14 s smoke, 5.8k cache read. 2026-11-20 |
| `luna` | `openrouter/openai/gpt-6-luna` | 0.10 | 0.50 | 0.01 | 0.125 | Pi 1.5.2's catalog + third-party coverage (morphllm.com, getnadir.com, licenseware.io) agree; **not checked on openai.com**. ≤ 272k input; above that $0.20 / $0.75. There is no GPT-6.1 Luna (as of 2026-11-20, third-party). In Pi's catalog, so no `models.json` entry. Direct smoke 2026-11-20 was refused with "organization has reached its configured enforced spend limit". **verified** via OpenRouter (smoke 2026-11-20: 10 s, 7.8k cache read / 0.3k out, $0.0005, Pi reports thinking medium -> medium). OpenRouter 2026-11-20: same price. 2026-11-20 |
| `neon` | `openrouter/google/gemini-4-neon` | 3 | 15 | 0.15 | none | Released 2026-11-18. Third-party coverage (eesel.ai, sandbase.ai, ofox.ai, casrai.org) agrees on id and price; one outlier gives $0.075 cached. **Not checked on Google's pricing page.** Flat across the 1M window. Paid tier only. **Not in Pi 1.5.2's catalog**, so `PiReviewer.Models` declares it (now under `openrouter`). **verified** (smoke 2026-11-20: id accepted, Pi reports thinking medium -> medium, 14 s, 6.4k in / 0.6k out, $0.028, no cache read on a single turn, like Hydrogen). OpenRouter 2026-11-20: same price, 14 s smoke. 2026-11-20 |
| `opus` | `anthropic/claude-opus-5` | 5 | 25 | 0.50 | 10 (1-hour rate) | Reproduced from Claude Code's `total_cost_usd`; not checked on anthropic.com. Matters only for extra usage (see §2). 2026-11-10 |
| | `anthropic/claude-haiku-4-5-20251001` | 1 | 5 | 0.10 | 2 | Same source. Claude Code makes small background Haiku calls, which also count as extra usage. |
| | `lithos/deepseek-ai/DeepSeek-V4.1-Flash` (base tier) | 0.15 | 0.60 | | | Lithos pricing page; not used yet. |
| | `baseten/deepseek-ai/DeepSeek-V4.1-Flash-1017-Fast` | 0.60 | 2.40 | 0.014 | | Baseten `GET https://inference.baseten.co/v1/models` (`pricing` field). 2026-11-09 |
| | `baseten/deepseek-ai/DeepSeek-V4.1-Flash-1017` | 0.30 | 1.20 | 0.007 | | Pi 1.5.0 Baseten catalog (half the Fast price). 2026-11-10 |
| | `baseten/zai-org/GLM-5.3-Flash` | 0.15 | 0.50 | 0.03 | | Baseten `/v1/models`. 2026-11-09 |
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
- **Resume:** `--session <id>` with an id that isn't in the session dir prints `No session found matching '<id>'` and exits 1, before any model call (from Pi 1.5.x's `main.js`, not run). A session recorded under another cwd triggers an interactive "fork it?" prompt; BobarCode always resumes from the same repo, so that doesn't arise.

- **Custom tools (used for streaming findings, `report_finding`):** Pi's own extension API (`pi.registerTool`, documented in Pi's `docs/extensions.md`) works with the read-only flags above. Adding `-e <file.js> --tools read,grep,find,ls,report_finding` to them loaded the extension despite `--no-extensions` (explicit `-e` paths still load). On `session_start`, `pi.getActiveTools()` was exactly `["read","grep","find","ls","report_finding"]`. That was Pi 1.5.2, with no model call: the probe ran without an API key. **verified** The extension can be a plain CommonJS `.js` file with no npm dependencies (`module.exports = function (pi) {…}`; JSON Schema `parameters` instead of TypeBox). A real model calls it: DeepSeek V4.1 Flash Ultra (ds, Lithos) called `report_finding` and ended with `DONE` as the prompt asks. **verified** (2026-11-20 smoke). The calls show up as `tool_execution_end` with `result.details` intact.
- **When reviewers report (2026-11-20, same ~600-line diff each time):** **verified** (one run per row)
  | reviewer | prompt | review | first report | all reports |
  |---|---|---|---|---|
  | ds | "as soon as you have checked it" | 173 s | 91% | 91–98% |
  | ds | stronger: "one problem at a time… never collect findings for the end" | 143 s | 88% | 88–99% |
  | sol (medium) | stronger | 104 s | 24% | evenly spread, 24–98% (11 findings) |
  | sol (medium) | "as soon as you have checked it" (shipped) | 100 s | 20% | evenly spread, 20–98% (10 findings) |

  ds explores first and writes everything up at the end, whatever the prompt says. Sol reports as it goes, with either prompt. The stronger wording was only a canary, to see whether any model reports early. It isn't shipped, because pushing for speed risks findings reported before they're checked, and those can't be taken back.

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
- **Service tier (e.g. Flex):** Pi 1.5.2 has no CLI flag or setting for it. Its OpenAI providers send `params.service_tier` from a `serviceTier` stream option that nothing in the CLI sets, and price the result by the tier the response reports (`getServiceTierCostMultiplier`: flex 0.5, priority 2). **doc** (Pi 1.5.2 bundle) An extension's `before_provider_request` hook can rewrite the whole payload (Pi's `docs/extensions.md`), so BobarCode's `bobar-service-tier.js` (loaded with `-e` when a reviewer has `serviceTier`, tier in `BOBAR_SERVICE_TIER`) adds `service_tier` (OpenAI) or `config.serviceTier` (Google's genai payload). Two `-e` extensions load side by side. **verified** for OpenAI (smoke 2026-11-20, `terra` on the scratch repo): accepted, and Pi's own cost fell from $0.00462 (standard) to $0.00233 with the same tokens, i.e. OpenAI reported serving it on flex. Google (`hydrogen`): accepted without error, but Pi's cost didn't halve ($0.0185 → $0.0195) and its Google provider has no tier pricing, so **unverified** whether Gemini served it on Flex: check the bill before relying on it.

### OpenRouter (all reviewers except `ds` and Claude since 2026-11-20)
`ds` stays on Lithos directly (user's choice, 2026-11-20): Lithos is much faster, and its Flash-ultra id isn't on OpenRouter (the closest is DeepSeek's own V4.1 Flash 1017 endpoint at $0.15 / $0.60, speed and rate limits unknown).

One account and spending limit instead of one per vendor. Through Pi's built-in `openrouter` provider (`api: "openai-completions"`, `https://openrouter.ai/api/v1`), key env `OPENROUTER_API_KEY`. Model ids are OpenRouter's (`openai/gpt-6.2-sol`, `google/gemini-4-hydrogen`, …), so provider/model keys read `openrouter/openai/gpt-6.2-sol`. **verified** (smoke 2026-11-20: sol, terra, hydrogen, neon)
- **Catalog:** Pi 1.5.2's OpenRouter catalog has 6.1 Sol, 6.1 Terra, 6 Luna, Gemini 4 Argon, GLM-5.3 and Qwen3.8 Max, but not 6.2 Sol, Hydrogen, Neon or Qwen4 Max. `PiReviewer.Models` adds the first three to the `openrouter` provider, with compat copied from Pi's own OpenRouter entries (`thinkingFormat: "openrouter"`, which sends `reasoning: {effort}`; session-affinity header `x-session-id`; Gemini entries without the developer role). On startup Pi also fetches `/models/user` with the key to personalize the catalog. **doc** (Pi 1.5.2 bundle)
- **Public model list:** `GET https://openrouter.ai/api/v1/models` (no key) gives ids, per-token prices, context and max output. On 2026-11-20 the OpenAI and Gemini prices equalled the vendors' list prices, context for the OpenAI models is 1.05M (vs 272k direct). It lists `:batch` variants at half price. The headline price for open models (GLM, Qwen, DeepSeek) is the cheapest host's, so pin a host before trusting it.
- **Latency:** no measurable difference from direct calls in single-turn smokes (Sol 5 s, Terra 14 s, Hydrogen 10 s, Neon 14 s; direct: 14, 13, 8, 14 s). **verified** 2026-11-20
- **Caching:** works: Terra and Sol read ~5.8k cached tokens on the first turn, Gemini a little (~550). Pi's vendor cost matched ours to the cent. **verified**
- **Reasoning:** `medium` echoed back as medium for all four. **verified**
- **Fees:** OpenRouter charges a fee when buying credits (about 5.5%; **not checked** on their pricing page). The config price table doesn't include it.
- **Service tier (flex):** `bobar-service-tier.js` adds a top-level `service_tier` to the chat-completions payload; whether OpenRouter passes it to OpenAI is **unverified**. The `:batch` model variants may be the OpenRouter way to get the discount (**unverified** that they work for interactive calls).
- **Host pinning (open models):** an open model has many hosts at very different prices, quantizations and speeds. A `models.json` entry with the same id as Pi's catalog entry replaces it (verified: Pi's vendor cost used our price), and `compat.openRouterRouting` is sent verbatim as OpenRouter's `provider` field (e.g. `{"order": ["z-ai", "together"], "allow_fallbacks": false}`). **verified** for GLM-5.3. Which host served a call: `GET https://openrouter.ai/api/v1/generation?id=<responseId>` with the key (`provider_name`); Pi logs `responseId` on each assistant message.
- **Privacy:** set the account's privacy settings to exclude providers that train on prompts (or require zero data retention) for private code.

### OpenAI (GPT-6.2 Sol, GPT-6.1 Terra, GPT-6 Luna)
Now reached through OpenRouter (above). Directly: Pi's built-in `openai` provider (`api: "openai-responses"`), key env `OPENAI_API_KEY`. The notes below are from direct use.
- **Catalog:** context 272k, max output 128k (6.1 Sol's values). Pi 1.5.2 doesn't ship 6.2 Sol, so BobarCode's agent-dir `models.json` adds it to the built-in `openai` provider (a `models` entry there adds a model and keeps the built-in ones and auth). Without the entry Pi would pass the unknown id through with no reasoning metadata. Drop the entry once Pi ships it. `pi update --models` refreshes catalogs; plain `pi update` doesn't.
- **Reasoning:** off/minimal → not sent; low/medium/high/xhigh/max → the same `reasoning.effort`.
- **Status:** Pi reports cache writes for it. Fractory used it as a reviewer; prices are **unverified** on openai.com.
- **GPT-6.1 Terra:** in Pi 1.5.2's catalog (context 272k, max output 128k, off/low/medium/high/xhigh/max). `terra` requests `medium`, like `sol`. **doc** (Pi catalog)
- **GPT-6 Luna:** in Pi 1.5.2's catalog with the same context, max output and thinking map as Terra. `luna` requests `medium` too. **doc** (Pi catalog)
- **Service tier:** keep the OpenAI project on the Standard tier. The priority ("fast") tier bills 2×. For a cheaper reviewer per request, set `"serviceTier": "flex"` on it in config.json and add a `openrouter/openai/<model>@flex` price (half the standard rates per OpenAI's pricing page; **not checked** per model): see §3. Flex is slower and may queue or return 429 when capacity is short, which the transient retry covers; raise `reviewerTimeoutMinutes` if long explorations time out.

### Google (Gemini 4 Hydrogen, Neon)
Now reached through OpenRouter (above). Directly: Pi's built-in `google` provider (`api: "google-generative-ai"`, `generativelanguage.googleapis.com/v1beta`), key env `GEMINI_API_KEY`. **doc** (Pi 1.5.2 source) The notes below are from direct use.
- **Catalog:** Pi 1.5.2 ships Gemini 4 Argon but not Hydrogen, so BobarCode's agent-dir `models.json` adds `gemini-4-hydrogen` to the built-in `google` provider, like 6.2 Sol. Drop the entry once Pi ships it.
- **Limits (third-party docs):** 1,048,576 context shared by input, thinking and output; 65,536 output tokens per request, thinking included.
- **Reasoning:** low/medium/high/xhigh. `minimal` returns 400, so the map sends nothing for off/minimal/max. Sources disagree on the default level, so `hydrogen` requests `medium` explicitly. `medium` is **verified** (Pi echoes it); `xhigh` is not.
- **Neon:** same treatment as Hydrogen (agent-dir `models.json` entry, same thinking map, `medium` requested). Third-party docs give low/medium/high (default)/xhigh and a 1,048,576 context; max output isn't documented, so `maxTokens` copies Hydrogen's 65,536. Implicit caching only. **doc** (third-party, 2026-11-20)
- **Free API tier:** exists, but Google may use free-tier prompts to improve its products. Use a billed key for private code.

### Baseten (no longer the default)
- Provider id `baseten`, key env `BASETEN_API_KEY`. **verified**
- **"Fast" variants:** same weights on dedicated capacity, at a separate price (2×) with their own rate limits.
- **Rate limits:** the Basic tier is 15–120 RPM and 100k–500k TPM; Pro is 120 RPM and 1M TPM. Fractory hit 429s, which is why DeepSeek moved to Lithos.
- **Reasoning levels (Pi catalog):**
  - DeepSeek V4.1 Flash 1017 (Fast): off/low/medium/high/max.
  - GLM-5.3 Flash: **low/high/max** only (medium → high).
- **Latency:** one GLM turn took 75 s for 84 output tokens and 0 reasoning tokens. Direct requests took 0.5–2 s. Most likely provider-side queueing or a cold start, not hidden reasoning.

### Together (Qwen4 Max, GLM-5.3, MiniMax M3)
- Provider id `together`, key env `TOGETHER_API_KEY`. Not used since 2026-11-20 (`qwen4` and `glm` moved to OpenRouter); the notes below are from direct use.
- **Qwen4 Max catalog:** Pi 1.5.2 doesn't ship it, so BobarCode's agent-dir `models.json` adds `Qwen/Qwen4-Max` to the built-in `together` provider, with the per-model `compat` copied from Pi's Together DeepSeek entry. The model page gives a 1M context but no max output, so `maxTokens` is a guessed 65,536. Text and image input. **doc** (together.ai model page, 2026-11-20)
- **Reasoning on Together:** Pi's `thinkingFormat: "together"` sends only `reasoning: {enabled: true|false}` (and `reasoning_effort` only when `supportsReasoningEffort`, which is off). So Qwen4 Max's levels are effectively on/off. The map marks minimal/low/medium unsupported, like Pi's Together DeepSeek entry, and `qwen4` requests `high`. Requesting `medium` gets bumped to `high` (Pi reports `medium -> high`). **verified** 2026-11-20 The model page says deep thinking is on by default and documents no effort levels. **doc** (Pi 1.5.2 source)
- **Tool calling for Qwen4 Max:** not documented on the model page. The smoke review found the bug from the diff alone, so it doesn't show whether tool calls work. **unverified**
- **GLM-5.3 and MiniMax M3:** both in Pi 1.5.2's Together catalog with the same Together compat and on/off-only thinking map as Qwen4 Max, so `glm` requests `high` (**verified**: high -> high). MiniMax M3 isn't actually deployed on Together (400 `endpoint_not_configured`, 2026-11-20), so the `minimax` shorthand was dropped the same day. Together's speed for them is unmeasured. Faster hosts exist but need new keys: GLM-5.3 on Baseten's Fast tier (`baseten/zai-org/GLM-5.3-Fast`, $2.10 / $6.60, about 283 tok/s per novita.ai) and MiniMax M3 on CoreWeave (about 306 tok/s per Artificial Analysis; not a Pi provider). MiniMax's own API runs M3 at about 84-109 tok/s. **doc** (Pi catalog, third-party, 2026-11-20)
- **DeepSeek-V4.1-Flash reasoning:** only **off** and **high** are effective; everything else clamps to high. **verified**
- **Model id:** Pi's catalog lists `deepseek-ai/DeepSeek-V4.1-Flash`. The `-1017` id works too, with a "custom model id" warning on stderr.

## 5. Windows process notes
- **Killing:** killing the whole process tree on timeout cleanly killed `pi.cmd → cmd.exe → node.exe`, with no orphans. **verified** In .NET: `Process.Kill(entireProcessTree: true)`.
- **Killed output:** it ends mid-stream with no terminal event (`result` / `agent_settled`). Treat a missing terminal event as killed or timed out.
- **Line endings:** `core.autocrlf=true` on this machine. For stable diffs use `git -c core.autocrlf=false diff --ignore-cr-at-eol`.
- **Prompts never go on argv**, which avoids the 32K command-line limit and `cmd.exe` quoting.
- **Smart App Control** blocks unsigned local builds: `dotnet test` fails with "An Application Control policy has blocked this file (0x800711C7)" and the exes give "Permission denied". **verified** It is turned off on this machine (2026-11-17). Check with `Get-MpComputerStatus | Select SmartAppControlState`.
