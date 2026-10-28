# RC1 — agent reference-image controls

User request: better controls for agents manipulating the reference image.
Scope: named views, explicit pane selection, list/add/set/move/scale/place/remove,
fit reference to model bounds, optional frame model and reference together.
Return correlated renderer acknowledgements and exact reusable refimage lines.
Edits are preview state only; never overwrite a document or modify a live script.
Use existing reference pass and camera; no UI, kernel, or visible test windows.
Validate finite numbers and all options before committing edits. Test rejected
edits leave state intact and test real windowless serve command integration.
