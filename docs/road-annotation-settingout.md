# Road annotation and junction setting-out

The new actions are available in **Field Completion** and **Road Supplementary**.

| Command | Options |
| --- | --- |
| `CE_ROADNAMES` | All or selected CE road centrelines; output layer; above, below or centered; one road midpoint or every section between T/cross junctions; overlap avoidance. |
| `CE_ROADDIMENSIONS` | All or selected CE road centrelines; dimension layer; lane/full widths; road midpoint or each junction-section midpoint; overlap avoidance. Requires linked road edges. |
| `CE_JUNCTIONSETTINGOUT4` / `CE_ROADJUNCTIONSETTINGOUT` | Actual owning road number; clockwise per-junction order; point and leader layers; closed-filled/current-style arrows; paper arrow size; keep or replace selected-source output. |

Road annotation detects crossings against the complete CE centreline network even
when only some roads are selected. End sections are included. Two meeting endpoints
alone are a continuation, not a junction. Crowded locations that cannot be placed are
reported instead of forcing overlapping text. Centered names stay on the centreline.
Dimensions intersect the linked edge pieces at the measured cross-section; lane and
full widths have separate dimension lines. `CE_ROADLAYOUTREFRESH` preserves the chosen
layer, side and placement mode for the new labels/dimensions.

For the reported **RD-02-CORRIDOR** example, use **Road grouped sequence** and
**Clockwise per junction from top left**. The command resolves the ROAD/RD number
from linked road metadata or a nearby named road. At an ambiguous shared junction,
select the owning road alignment/corridor when prompted, or use **Pick owning road**.
**Use specified road number** provides an explicit override for unnamed geometry.
All returns assigned to Road 2 use `J2.1`, `J2.2`, etc.; the sequence continues across
its selected returns and junctions. Return order is top-left → top-right →
bottom-right → bottom-left. Arc centres follow their associated on-curve points.

Choose **Replace selected sources** with **Create new linked table** to remove old
CE points, leaders, radius dimensions and table rows belonging to the selected
source geometry. A table shared with unselected sources is retained for those
sources. Replacement and new output commit together; cancellation or an error
leaves the previous group intact. Manually created points/tables are not erased.
Kept output is considered when allocating new road point names. Saved road maps,
starting sequences, layers and arrow settings remain linked during refresh.

Validation includes core geometry/numbering regression tests, the C# syntax and
shared-alias check, production wiring checks, and an idempotent staged-repair test.
An installed Civil 3D 2023 build and drawing check are still required for native API
and visual verification; the test environment does not contain Autodesk assemblies.
