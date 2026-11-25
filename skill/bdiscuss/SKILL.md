---
name: bdiscuss
description: Have several models discuss an open question as peers over a few rounds via bobar-cli (each answers independently, then all revise after seeing each other's solutions and messages), then synthesize a report with merged solutions, a recommendation and the rejected ideas. Use when the user types /bdiscuss <models> [prompt].
---

You are the **author** of this discussion's report. The participants are other models, run by `bobar-cli`. You don't take part in the rounds: you read their final solutions, settle what you safely can, ask the user about the rest, and report. The user trusts your judgement in that synthesis.

`$ARGUMENTS` is `<models> [--rounds=<n>] [prompt…]`:
- the first token is the participants: comma-separated reviewer shorthands from BobarCode's config (e.g. `ds,opus`), at least two;
- if the second token is exactly `--rounds=<n>`, it sets the number of rounds (default 4) and is not part of the prompt. This is the only option, and only in that position;
- everything after that is the user's prompt, **verbatim**.

1. **The question.** If there is prompt text, it is the question, word for word: don't edit, shorten or fix it. If (and only if) it relies on earlier conversation that the participants can't see (e.g. "the option we discussed"), append a clearly separated section after it: `## Context from the conversation (added by the user's assistant)`, with just the facts needed. If there is no prompt text, write a faithful summary of the question discussed in this conversation instead: the question itself, the constraints and options raised so far, and what the user leans toward (marked as their leaning, not a conclusion).
2. **Folder.** Pass `--dir <working dir>` whenever this session has a project folder (always in VS Code; in the Claude desktop app, whenever a folder was chosen). Pass no `--dir` when the session has no folder: a Claude desktop session started without choosing one has a working dir under a `scratch-workspaces` folder (e.g. `…/Claude-Data/<id>/scratch-workspaces/<…>/scratch-<date>-<…>`). If the working dir is neither that nor clearly a project, ask the user once in the chat. The participants then read the project read-only; without a folder they have only web tools.
3. **Slug.** Pick a short topic slug for the run id: a-z, 0-9 and `-`, at most 40 characters (e.g. `srs-scheduler-aims`, `speakers-black-friday`).
4. **Start** (Bash, `run_in_background: true`; don't poll, you'll be notified; it can take a long time). Quote the heredoc delimiter so nothing in it is expanded:
   ```
   bobar-cli discuss start --models <models> --slug <slug> [--dir "<dir>"] [--rounds <n>] --prompt - <<'EOF'
   <the question>
   EOF
   ```
   Tell the user the discussion has started, with the participants, and that BobarCode shows it live.
5. When it finishes, read its output (first line `STATUS: …`).
   - `discussed`: read **every** solution file it lists, in full. A participant listed with "no solution" is only mentioned in the report. Then list the disagreements left between the final solutions, and decide each one you safely can, on the merits (not by majority). For each one you can't settle safely (it turns on the user's preferences, values, budget or circumstances you don't know, or the arguments are evenly balanced), ask the user: run `bobar-cli discuss ask <run> --questions -` (foreground) in the format the output shows: per question, the disagreement in `context` (who holds which view and why you can't pick) and 2–4 `options`; list the disagreements you settled yourself under `decided`. It may be called once per discussion, so ask everything at once. Relay what its output says about BobarCode (open, or the user should open it and select the run), then run `bobar-cli review wait <run>` in the background. When it returns `answered`, follow the answers. If you have no questions, skip asking.
   - `invalid_questions`: fix the JSON and run `discuss ask` again.
   - `cancelled` / `error`: tell the user what happened, and stop (no hindsight is wanted unless the output asks for one).
6. **The report**, in Markdown, written for the user, with three main sections:
   1. **Final solutions**: one or more coherent, merged solutions built from all the final solutions (more than one only where real alternatives remain). Each stands on its own.
   2. **Recommendations**: which of them you recommend, in what form, and why. Follow the user's answers.
   3. **Rejected ideas**: the ideas in the final solutions you left out of section 1, each with why.

   Credit participants where it helps the user weigh an idea, and say if a participant dropped out. Send it: `bobar-cli discuss report <run> --input -` (foreground) with the report on stdin (quoted heredoc).
7. **Hindsight**: when the output asks for it, send it as shown (`bobar-cli review hindsight <run> --input -`, foreground): one score from 1 to 10 on your own judgement, for how productive the discussion was and how much the differing viewpoints enriched the result, the reason for it, and what BobarCode should change.
8. **Tell the user**: show the report in the chat (in full; if it's very long, a summary plus a pointer to it in BobarCode), then your hindsight score and its reason in a line or two.

If you lose track of the run (e.g. after a context reset), `bobar-cli runs` lists recent runs and `bobar-cli review show <run>` prints where it stands.
