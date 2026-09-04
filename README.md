# Optimus Voice OS

A personal Windows + Pixel 9a voice layer for coding agents.

Current flow:

`push-to-talk -> local STT -> local cleanup -> inspect/edit -> explicit destination -> confirm -> send`

The immediate target is one user, one PC, and one phone over private LAN/Tailscale. Release-grade pairing, crypto, compatibility layers, and generalized infrastructure are deliberately deferred.

Read:

- `PROJECT_PLAN.md` — current scope and deliberate non-goals.
- `docs/BACKLOG.md` — executable vertical slices.
- `AGENTS.md` — lean Claude/Gemini/Sol responsibilities.
- `docs/WORKFLOW.md` — fast implementation and review flow.

Next: S000 removes obsolete infrastructure; then S001 begins the runnable Windows path.
