# Multi-Model Delivery Workflow

## Control checkout

Keep the primary checkout on `main` as the control and integration directory. Product implementation occurs in task-specific sibling worktrees.

## Create a task worktree

After the task contract is `READY` and its dependencies are on `main`:

```powershell
git worktree add ..\Optimus_T002 -b optimus/T002-scaffold main
```

Open only that sibling directory in the assigned coding tool. Do not open the same worktree in another agent until the active agent has stopped and committed.

## Implementation handoff

The implementer must:

1. Read `AGENTS.md`, `PROJECT_PLAN.md`, the task contract, and referenced ADRs.
2. Confirm the base commit and task-owned files.
3. Implement only the contract.
4. Add and run the required tests.
5. Record evidence in the task file.
6. Review `git diff` and `git status` for unrelated changes.
7. Commit and set the task status to `IMPLEMENTED`.

## Review handoff

After implementation stops, open the same worktree in Codex using GPT-5.6 Sol. The reviewer compares the task branch against its declared base, runs checks, and creates `reviews/T###-sol.md` from the review template.

If the verdict is `CHANGES_REQUIRED`, close the reviewer before opening the worktree in the assigned fixer. After corrections are committed, Sol appends a new review round and reissues the verdict.

## Integration

Opus verifies that the newest review covers the current branch head and says `PASS`. Integration uses a non-fast-forward merge so the task boundary remains visible:

```powershell
git switch main
git merge --no-ff optimus/T002-scaffold
```

Run the accumulated milestone checks, then remove the completed worktree:

```powershell
git worktree remove ..\Optimus_T002
git branch -d optimus/T002-scaffold
```

Do not remove a worktree with uncommitted changes. Do not force-delete a branch that has not been integrated.

## Parallel work

Parallel tasks are permitted only when Opus records that:

- Both tasks depend on commits already present on `main`.
- Their owned files and interfaces do not overlap.
- Neither task consumes an interface that the other is still defining.
- Each task uses a different worktree.
