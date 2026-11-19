---
name: breview
description: Have another model review your uncommitted changes (or the last N commits too) and iterate on its findings via bobar-cli. Use when the user types /breview <reviewer> [N].
---

You are the **author** in a review loop run by `bobar-cli`. `$ARGUMENTS` is `<reviewer> [N]`: the reviewer is a shorthand from BobarCode's config (default `sol`); the optional N means "also review the last N commits".

1. Don't commit anything during the loop.
2. Start: run (Bash, `run_in_background: true`, don't poll; you'll be notified):
   - Without N: `bobar-cli review start --reviewer <reviewer> --desc -` with a heredoc on stdin containing a short description of the uncommitted changes: what changed and why, and anything a reviewer should know.
   - With N: `bobar-cli review start --reviewer <reviewer> --from HEAD~<N-1>` (so N=1 is `--from HEAD`). The commit messages become the description, and anything uncommitted is reviewed too. If you know something the messages don't say, add `--desc -` with a short heredoc of notes. Your fixes stay uncommitted on top of HEAD; the user commits them.
3. Read the command's output. Its first line is `STATUS: …`.
   - `findings`: for each finding, decide honestly whether it needs action. Fix real problems. Reject noise, misreadings and taste disagreements, with a concrete reason (cite path:line). Don't accept a finding just to end the loop, and don't reject one just because you wrote the code. Apply the fixes and run the project's tests if relevant. Then run `bobar-cli review next <run> --response -` (background, as above) with the response JSON on stdin, in the format the output shows. Go to 3.
   - `invalid_response`: fix the JSON and rerun `next`.
   - `awaiting_decisions`: tell the user BobarCode is open for their decisions. Run `bobar-cli review wait <run>` in the background. When it returns, read its output as in 3.
   - `decided`: apply the findings marked fix (and follow the comments), then go to 4.
   - `busy`: another bobar-cli process is working on the run; wait for it, or run `bobar-cli review show <run>`.
   - `done` / `cancelled` / `error`: go to 4.
4. Hindsight: if the output has a "## Hindsight" section, it asks you to look back on the whole run; otherwise (e.g. a cancelled run, or `start` failing before a run existed) skip to 5. Send it as the output shows (`bobar-cli review hindsight <run> --input -`, in the foreground: it returns at once). Rate honestly, and list the findings you now think you shouldn't have fixed or shouldn't have rejected. Don't revert or redo anything because of it: it's a report. On `invalid_hindsight`, fix the JSON and send it again.
5. Tell the user in a few lines: rounds, what you fixed, what you rejected and why, and how it ended. After a hindsight, add your usefulness rating and the findings you listed as accepted or rejected wrongly.

If you lose track of a run (e.g. after a context reset), `bobar-cli runs` lists recent runs and `bobar-cli review show <run>` prints where it stands, including the current findings.
