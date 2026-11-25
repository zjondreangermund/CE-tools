# Sheet imports and units — 25 November 2026

Integrated the three supplied revisions, preserving plan-only sheets, delayed viewport activation and title-block layer restoration. The paper fitting upload now owns CE_PAPERFITVIEWPORTS; CE_MOVEPAPERSPACES continues moving selected layout contents.

CE_SEWERSHEETS calls CreateSheets(true); road sheets call CreateSheets(false). Common top-level diagnostics now identify the actual command. Missing profile views are not a road-command dispatch: create sewer profile views with CE_SEWPROFILE or select existing profile views in the dialog.

MULTIDIM offers measurement units (m/mm) separately from placement units. A fresh copied dimension style per run prevents unit changes from modifying existing dimensions. Drawing units and source text heights remain unchanged.

Road viewport fitting rejects invalid dimensions/view heights and derives scale through ViewHeight, avoiding a second CustomScale camera update. This is a mitigation, not a verified diagnosis of the native crash; Civil 3D 2024 runtime testing with the affected DWG is still required.

Validation: Autodesk API-reference compilation of sheet/import/media/move commands, repository C# syntax/alias validation, command registry and road-sheet regression. Runtime checks: imported locked/frozen title blocks and raster paths; source/destination viewport scales; sewer existing/split/missing profiles; road oversized/repeated sheet creation; MULTIDIM m/mm on metre and millimetre drawings. Confirm saved drawings retain text and dimension heights after reopening.

The repository-wide popup migration remains a separate unfinished task.
