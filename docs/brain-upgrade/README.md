# Brain Upgrade Plan

Eight lanes of work to upgrade the desktop AI brain. Each lane is independent enough to
be worked in its own worktree; shared-file ownership must be assigned in `BOARD.md` before
parallel edits.

## Lanes

### A. Durable memory
Persistent, durable storage of facts, preferences, and context across sessions. Covers
schema, migrations, write/read paths, and compaction.

### B. Trustworthy perception
Reliable sensing of the world state: viewport capture, scene graph, material/geometry
state, IK targets. Perception must be grounded and verifiable before it informs
judgment.

### C. Judgment and curiosity
Decision-making over perceived state: what to try, when to ask, when to act, and when
to explore. Includes the judge/critic loop and curiosity-driven exploration.

### D. Relevant recall
Retrieval of the right memory at the right time. Embeddings/ranking, recency, and
relevance gating so the brain surfaces what matters, not everything.

### E. Reliable skills
Skill loading, selection, and execution. Skills must be discoverable, validated, and
fail loudly rather than silently.

### F. Stable learning
Learning that does not regress: stable updates, rollback, and evaluation of learned
behavior over time.

### G. Runtime and integration
The glue: CLI, serve mode, provider integration, tools, and runtime lifecycle. Keeps
the brain reachable and observable.

### H. Evaluation
Metrics, benchmarks, and regression checks for every other lane. No lane is "done"
without an evaluation.

## Working rules

- One ticket at a time. Read `handoffs/README.md`, `handoffs/RULES.md`,
  `handoffs/CONTRACTS.md`, and the assigned ticket.
- Shared-file ownership assigned in `handoffs/BOARD.md` before parallel edits.
- Do not mark a ticket verified from compilation alone.
- Headless only for automated renders/smoke tests.
