# Preserve saved drawing appearance

Opening or activating a DWG is not permission to restyle it. Paper-space annotation repair now runs only when `CE_PAPERTEXTANNOOFF` is explicitly invoked. COGO attachment no longer queues a universal refresh. Universal, COGO, comment and sewer watchers clear pending work when changing databases and ignore passive opening, startup, regeneration, layout/navigation, scale-variable and save events.

No text height, dimension height, dimension style, drawing unit, annotation-scale value or viewport scale is assigned by this fix. Existing saved values are authoritative. Explicit editing/creation commands retain their own settings. This prevents the identified startup paths; it does not reconstruct text sizes already changed and saved in an older session. Object changes made without a command (for example some palette edits) no longer trigger the broad automatic presentation rebuild; the explicit refresh command remains available.

Civil 3D 2024 acceptance: record text/attribute heights, annotative flags/contexts, dimension overrides, DIMTXT/DIMSCALE/TEXTSIZE/CANNOSCALE, Civil drawing scale and viewport scales; close/reopen, wait through idle, change layouts, switch to another DWG and back, then compare. None should change solely from opening/activation. Check explicit source edits and manual repair separately.

PR #381 merged the earlier sewer/sheet-import/theme work. The application-wide legacy input migration remains separate. This preservation fix does not claim that migration or the native road-sheet crash is runtime-verified.
