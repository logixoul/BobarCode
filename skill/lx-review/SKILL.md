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
   - `busy`: another bobar-cli process is working on the run; wait for it, or run `bobar-cli review show <run>`.
   - `done` / `decided` / `cancelled` / `error`: go to 4.
4. Tell the user in a few lines: rounds, what you fixed, what you rejected and why, and how it ended.

If you lose track of a run (e.g. after a context reset), `bobar-cli runs` lists recent runs and `bobar-cli review show <run>` prints where it stands, including the current findings.
