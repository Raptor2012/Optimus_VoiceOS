# Optimus Voice OS

Optimus Voice OS is a Windows-first, local-first voice layer between a human and coding agents. It captures speech through a global hotkey or Pixel 9a, transcribes and conservatively cleans it locally, requires an explicit destination and confirmation, then sends it to Claude Code or Codex.

The repository is developed through a gated multi-model workflow:

1. Claude Opus 5 designs architecture and difficult changes.
2. Gemini 3.8 Flash implements bounded workhorse tasks.
3. GPT-5.6 Sol independently reviews every merge unit.
4. The appropriate implementer fixes findings and Sol re-reviews.
5. Opus integrates only a reviewed `PASS` task.

Read these files before doing any work:

- `PROJECT_PLAN.md` — canonical product and implementation plan.
- `AGENTS.md` — mandatory responsibilities and collaboration rules.
- `docs/WORKFLOW.md` — operational task/worktree procedure.
- `tasks/T001-architecture.md` — first task, owned by Claude Opus 5.

## Start here

Open this repository in Claude Code and use `prompts/claude-t001.md` as the first prompt. Do not start Gemini implementation until T001 has been reviewed by GPT-5.6 Sol and integrated.
