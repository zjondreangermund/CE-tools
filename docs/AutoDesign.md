# CE_AUTODESIGN — first functional version

Run `CE_AUTODESIGN` in a Civil 3D drawing with the newly built CE Tools DLL loaded.
One setup dialog selects a mode, Sewer / Roads / Sewer and Roads, and an explicit
existing gravity network for Sewer. The network must be CE sequenced. Selecting a
network explicitly avoids silently processing a water network as sewer. No ribbon
placeholder is added. All existing commands and saved engineering settings remain
available.

## Modes

- **Analyse Only** inventories existing design objects and stale sources, builds
  a dependency plan and reports missing deliverables / review decisions. No
  adapters run and no journal is written.
- **Complete Existing Design** executes compatible headless stages using saved
  settings, skips unchanged successful stages, and reports unresolved work.
- **Full AutoDesign** runs the same compatible stages and exposes an explicit
  engineering approval gate for new design creation. **V1 does not generate a
  complete network or road from raw survey data.** Choose routes, outfalls,
  surfaces, parts, slopes, assemblies, targets and layout placement through the
  existing manual design workflows, then rerun AutoDesign. Full mode never
  silently approves design defaults.

## Working stages and dependencies

1. Rebuild only surfaces marked out of date, and corridors for Road scopes.
2. Sewer: existing selected CE network → topology / branch numbering
   → missing native plan labels and linked CE profile parts / bands. Full mode can additionally create missing approved branch alignments.
3. Roads: existing road elevation links → one bounded pass rebuilding newly
   stale surfaces/corridors → linked junction labels and road names.
4. After prerequisites succeed: refresh existing linked annotations, schedules,
   quantities and drawing outputs, then run basic validation.

Sewer sequencing calls the existing implementation with its downstream refresh
switched off for batching. All existing callers keep the original default refresh
behavior. No grade-solving, sizing, routing or hydraulic calculation is replaced.
Missing plan-label styles require explicit saved choices. Missing views / corridors,
flat or degenerate pipes, disconnected ends, missing pipe reference surfaces and
invalid sump/rim relationships are reported. Referenced / locked sewer parts block
network changes. Stale locked or referenced design sources fail with a review
message. Open ends can be intentional outfalls; they are flagged, never connected
by guessing.

The setup dialog discloses that **road, profile-band and linked-output services
are drawing-wide**, even when one sewer network is selected. Civil source rebuilds
are also drawing-wide but only touch stale objects. V1 uses conservative
**drawing-wide invalidation**, not minimal per-object dependency invalidation.
A lightweight session revision tracks object-modified, appended and erased events.
The callbacks only increment a counter; they do not inspect objects or perform design work.
No native ComputeFingerPrint call, arbitrary property reflection, block-definition
walk or recursive dictionary scan is used for change detection. Unrelated drawing edits
can conservatively invalidate the plan. External preference/workbook/reference-file
changes still need their normal manual refresh and review.

## Recovery and refresh safety

The runner rejects cycles, missing dependencies and duplicate task IDs, runs each
stage at most once per invocation, and never runs design work from Idle or object events. A reentrancy guard prevents a second run. Stage failures
block dependent tasks while independent work can continue. Partial failures
invalidate earlier checkpoints so partial writes are not treated as current.
Existing output services perform their normal link-based refresh or label
existence check; no blind label/table creation loop is added.

Progress appears in the AutoCAD progress meter and command history; a scrollable
results dialog lists each status and engineering review finding. Ctrl+C is polled
between stages. An individual existing service can take time and cannot always be
interrupted mid-stage. Rerun `CE_AUTODESIGN` to resume. A Running checkpoint is
retried, not assumed successful. Checkpoints live in `CE_AUTODESIGN_V1` in the DWG
named-object dictionary. **Save the drawing to retain them across closing/crashes.**
They participate in normal drawing save/undo behavior. A reopened drawing gets a new
session revision, so completed stages are conservatively revalidated; Running/Failed work
remains retryable through the existing idempotent adapters. Successful work in an unchanged
open drawing is still skipped. AutoDesign's own checkpoint transactions are excluded from
revision tracking, so they cannot invalidate themselves.

A synchronously flushed diagnostic log is written before each stage and before each native
source rebuild to `%LOCALAPPDATA%\CE Tools\Diagnostics\AutoDesign-last-run.log`.
The next run preserves it as `AutoDesign-previous-run.log`. A native shutdown cannot be
caught reliably by managed exception handling, so the last written stage/object is the
recovery evidence. These files record the drawing name, stage and managed errors; they do
not save the drawing or certify crash recovery. Logging failures do not stop the command.

## Validation boundary and remaining integrations

Successful automation is not final engineering approval. V1 does not certify
hydraulic capacity, cover/slope limits, flow direction, corridor targets,
constructability, plotting or quantities for unlinked objects. It refreshes only
existing linked profiles/schedules/sheets; it does not invent missing sheet
placement or create missing BOQ deliverables. Some preserved services internally
skip unsupported objects; their exposed warnings/counts are checked where
available, but services returning only update counts cannot prove every output
was refreshed. These limitations remain visible in the report.

Next adapters should expose saved, approved input contexts for network creation,
gravity design rules, corridor production and missing profile/sheet/BOQ generation,
with native output ownership / source fingerprints and structured failure results.

## Verification

Host-independent tests cover dependency ordering, no-op reruns after internal
writes, strict read-only analysis, approval blocking, failure isolation/recovery,
between-stage cancellation, crash checkpoint recovery, invalid graphs,
mode/scope isolation, external changes, reentrancy, partial mutations and journal
write failure. They run with the normal Core test project. The C# syntax/shared
alias validator covers the Civil adapter source.

Civil 3D / Autodesk assemblies are not available in the development container;
a full adapter build and live DWG execution remain required on Windows. Test on a
copy of a Sewer drawing and a Road drawing:

1. Analyse Only: compare before/after objects, settings and journal (no changes).
2. Complete existing design: confirm only stale editable sources rebuild, CE
   branch numbering and missing plan labels refresh, existing linked profiles /
   schedules update, and validation findings remain visible.
3. Rerun unchanged: confirm successful stages are Current and labels do not double.
4. Move/delete a pipe, edit surface data or saved style, and remove a generated
   label: confirm affected plan is invalidated and outputs are restored/refreshed.
5. Lock a design layer, remove a required style or break a reference: confirm the
   error is reported and dependent work is blocked, with independent stages usable.
6. Ctrl+C between stages, save/reopen and rerun: completed stages resume correctly.
7. Exercise Full AutoDesign on an empty drawing: new-design decisions must be
   flagged, and the report must not claim an engineered design is complete.
8. Verify the existing manual sewer sequence and road commands still behave as
   before; the optional batching parameter defaults to the preserved manual path.

## Missing-alignment adapter and additional safeguards

Full AutoDesign now offers **Create missing using saved styles**. This reuses the existing Sewer branch-plan/creation routines, requires every pipe to carry approved CE sequence data and requires explicit available alignment styles. Existing branch alignments are excluded from the creation plan, preserving dependent profile references. Review changed existing alignments through the manual CE_SEWALIGN preview.

AutoDesign's numbering pass disables alignment replacement separately from downstream refreshes. Existing manual callers keep both original defaults. Run choices are transient, so Analyse Only does not save dialog settings. Execution shows a review report and requires run confirmation.

Additional topology validation blocks mutations for disconnected components, loops, isolated structures, duplicate/parallel edges, cross-network endpoints and self-connections. This does not certify engineering gradients, hydraulic capacity or cover. Strict excavation/BOQ/workbook refresh adapters report recoverable failures without changing manual command behaviour. Object-change revisions invalidate the drawing-wide plan, including profile edits, without querying native profile fingerprints.

The existing Sewer/Road production menus now launch the working CE_AUTODESIGN command. The additional topology/integration regression executable runs with:

```text
dotnet run --project tests/CE.Tools.AutoDesign.Tests
```

Autodesk-linked compilation and live Civil 3D DWG validation remain necessary; neither is available in the Linux development environment. Save the DWG to retain checkpoints after closing.

## Native-shutdown hardening

Analyse Only now uses the lightweight drawing revision and defers native style-collection enumeration to the explicit label execution stage. Initial analysis still reads selected network geometry and basic inventory; model-space object opening is restricted to registered corridor/profile-view classes, while other counts come from known Civil collections. Arbitrary proxy/custom/block objects are not opened for inventory. Autodesk-linked testing remains necessary. The source-level safety guard rejects reintroducing native fingerprint calls, arbitrary property enumeration or recursive dictionary scans into AutoDesign analysis.

## Missing profile outputs and source choice

AutoDesign remembers only the explicitly chosen Sewer network during the current open drawing/session. A fresh session still requires selection; a lone gravity network is never automatically classified as Sewer. The report distinguishes an unselected source from missing geometry.

Native profile views are discovered from alignment-owned view IDs. Sewer refresh targets must carry CE profile metadata for the selected network. Drawn grids/blocks are not native profile views. An absent linked view is a **NeedsApproval** creation/linking item, not an attempted refresh failure. Existing linked profile refreshes use the same saved-style/apply logic as the manual command, with an explicit view list.

Quantity/schedule refresh requires validated Sewer sources or rebuilt Road sources. Missing profile views or display labels do not prevent independent quantities from refreshing. Final validation still depends on sequence, labels, profiles and output completion and therefore remains blocked until missing deliverables are resolved. No profile placement, datum or engineering choice is invented.
