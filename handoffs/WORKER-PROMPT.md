# Copy-paste worker assignment

Replace every `<...>` field before handing this to another model.

```text
Work on Geppetto desktop ticket <ID>, and ONLY that ticket.

Workspace: /home/poo/games/projects/geppetto-desktop
Read, in order:
  AGENTS.md
  handoffs/RULES.md
  handoffs/CONTRACTS.md
  handoffs/<assigned-brief>.md, ticket <ID>

Current feature branch/snapshot or baseline: <identifier or snapshot path>
Prerequisites already implemented and verified: <IDs + exact code locations>
You own these files: <explicit list; include your test file>
Shared files the coordinator will integrate: <explicit list>
Do NOT edit anything else without reporting why it is required.

This is not UI work. Never open a visible window. Use headless CPU tests and
--headless EGL GPU checks only; no fallback to SDL/a visible app.

First confirm the actual existing method signatures. Proposed APIs in the handoff
are new code to implement, not magic methods you can assume exist.
Write the assigned regression tests, implement the smallest complete slice, and
connect it through the named integration seam. Preserve existing tests/behaviour.

If a prerequisite is absent, a shared file is owned, or correctness needs a kernel
change/new dependency, STOP and name the exact blocker. Do not fabricate a substitute.
Do not spend the assignment implementing the rest of the category.

Report back:
1. Ticket ID and supported behaviour.
2. Files changed and exact public API added/changed.
3. Exact test commands, exit codes, artifact paths and any measured numbers.
4. Invalid-input/data-loss/threading cases exercised.
5. Integration still needed and explicit limitations.

Do not claim the ticket complete from compilation alone. Do not mark other tickets done.
```

Reviewer: compare the changed-file list to the granted list, run the ticket's tests,
check source-asset hashes when applicable, and update BOARD.md only after reviewing evidence.
