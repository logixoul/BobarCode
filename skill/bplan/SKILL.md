---
name: bplan
description: Write the plan agreed in this conversation to a file (unless it already exists) and have another model review it, iterating on its findings via bobar-cli. Use when the user types /bplan <reviewer> [file].
---

`$ARGUMENTS` is `<reviewer> [file]`: the reviewer is a shorthand from BobarCode's config (default `sol`); the file defaults to `plan.md` in the repo root. A lone argument that looks like a path (contains `/` or `\`, or ends in `.md`) is the file, with the default reviewer.

1. If the file exists, it is the plan: review it as it is, don't rewrite it from the conversation. (This is how the user gets another model's review of an already improved plan.)
   Otherwise, write the plan the user and you agreed on in this conversation to it. Include:
   - the goal and scope (and what is out of scope),
   - the decisions and why they were made, including alternatives that were rejected and why, so the reviewer doesn't propose them again,
   - the steps, concrete enough to implement from.
   Write only what was agreed. Mark open questions as open; don't settle them yourself.
2. Don't commit the plan file: bobar-cli reviews the uncommitted diff. Before starting, check that `git -C "<repo root>" status --porcelain -uall -- "<file>"` (path relative to the repo root, forward slashes, quoted) prints something. If it doesn't, the file is committed, ignored or outside the repo, or the path is wrong: fix the path if you can, otherwise tell the user why the plan can't be reviewed and stop.
3. Run the review loop exactly as in the breview skill, without N. If its steps aren't loaded, read them with Bash: `cat ~/.claude/skills/breview/SKILL.md`. Quote the heredoc delimiter (`<<'EOF'`) so nothing in it is expanded. As the `--desc`, use: "This is a plan, not code. Read <file> in full in the working tree (the diff may show only part of it) and review it as a plan: gaps, risks, wrong assumptions, missing steps, unclear or contradictory parts. Its recorded decisions were agreed with the user; challenge one only with a concrete reason." Then add a line or two of context the plan doesn't say. If other uncommitted changes exist, say they're not part of this review.
4. Fixes are edits to the plan file, in place. If a finding challenges a decision the user made explicitly, reject it and cite that decision; don't change it silently. If the reviewer maintains it, it goes to the user's decisions in BobarCode, which is the right place for it. Likewise, if a finding needs a decision the user hasn't made, don't make it: keep it under open questions and list it for the user in your report.
5. Send the hindsight and report as in breview (the hindsight rates the plan review), and say where the plan file is. Remind the user that it is uncommitted, so it will show up in the next `/breview` too, and that after their last plan review they should commit it, delete it, .gitignore it or move it out of the repo (an ignored plan, or a committed one with no edits since, can't go through `/bplan`). Don't do any of these yourself.
