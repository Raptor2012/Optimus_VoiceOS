# Fast multi-model workflow

The user controls the Claude, Antigravity/Gemini, and Codex sessions. Sol reviews only.

## Starting work

Use the next slice from `docs/BACKLOG.md`. Give the implementer:

- the goal and done conditions;
- the exact repository/worktree;
- an instruction to build and run;
- an instruction not to add protocols, security layers, fallback engines, generalized frameworks, or unrelated hardening.

Ask Opus only a narrow question when a concrete difficult blocker exists. Do not ask Opus to design the whole product before Gemini can code.

## Implementation

Gemini makes the smallest complete change, runs targeted tests/builds, and explains exactly how the user can try it. Worktrees are useful for isolating code, but documentation gates and multi-round design reviews are not required.

## Review

Sol asks:

- Can it send without confirmation?
- Can it send to the wrong destination?
- Can cleanup change intent?
- Is ordinary PC or phone use broken?
- Does the promised runnable path actually work?

If none apply, pass and move on. Future hardening and hypothetical release risks do not block this personal tool.

## Current direction

The T003 protocol/crypto branch is parked and must not be merged into the MVP. Start with S000 cleanup, then build the Windows path and the minimal Pixel 9a path.
