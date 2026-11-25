---
name: commit
description: Review and commit requested Little Launcher changes with their affected documentation. Use when the user asks to commit.
---

# Commit changes

This replaces the shared user-level `commit` skill and adds one step to it: bring the affected docs up to date in the same commit.

1. Follow the shared commit workflow (review the diff, keep to the requested scope, validate, stage explicit paths).
2. Before staging, check the "Keeping docs current" table in [AGENTS.md](../../../AGENTS.md) against the diff. Read each guide it names and update what the change made stale, for Windows and Linux alike, then stage those docs with the change.
