# Reference AutoLISP intake checklist

When an existing AutoLISP routine is supplied as a workflow reference, CE Tools will:

1. Record the command name, prompts and selection method.
2. Record the exact label, text, layer, colour, precision and output behavior.
3. Preserve useful shortcuts while replacing unsafe or brittle implementation details.
4. Add one-step undo, locked-layer handling, error recovery and clear completion reporting.
5. Decide whether the final CE Tools implementation should remain AutoLISP or become a managed Civil 3D command.
6. Add a test plan before release.

Reference source is used to understand the workflow. CE Tools will implement and maintain its own professional version rather than blindly copying third-party code.
