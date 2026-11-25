---
name: audit-docs
description: Audit the project documentation and agent files against recent code changes and fix what is stale. Use before a release or after a batch of changes. Do not use to write new feature documentation from scratch.
---

# Audit documentation

Goal: every guide, nested `AGENTS.md` and skill describes the code as it is now.

1. List what changed since the last release: `git log $(git describe --tags --abbrev=0)..HEAD --oneline`, or `git log --oneline` when there is no tag.
2. For each change, find the docs to update in the "Keeping docs current" table in [AGENTS.md](../../../AGENTS.md).
3. Read each affected file, then correct only what is stale, missing or wrong. Leave accurate sections alone, and read the source when unsure whether something changed.
4. Check that every path, command, npm script and skill named in `AGENTS.md`, the nested `AGENTS.md` files (`git ls-files '*AGENTS.md'`), `docs/`, `.agents/guides/` and `.agents/skills/` still exists.
5. Check that each guide's Governs line still matches the files it covers, and that the guidance map in `AGENTS.md` lists every guide and skill.

Report: the files changed and why, and any stale reference left for a person to decide.
