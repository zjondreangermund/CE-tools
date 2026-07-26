from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PLUGIN = ROOT / "src" / "CE.Tools.Civil3D" / "PluginEntry.cs"
SOURCE = PLUGIN.read_text(encoding="utf-8")

REQUIRED = [
    'Menu("CE_TOOLS_PROJECT_STYLES_MENU"',
    '"CE_PROJECTSTYLES "',
    '"CE_PROJECTSTYLEINFO "',
    '"CE_PROJECTSTYLECLEAR "',
    'Menu("CE_TOOLS_UNDO_MENU"',
    '"CE_UNDOSETTINGS "',
    '"CE_UNDO "',
    '"CE_REDO "',
    '"CE_CLEANUPUI "',
    '"CE_HATCHUI "',
    '"CE_PRESENTATIONTOOLS "',
    '"CE_MAKEANNOTATIVE "',
    '"CE_TABLESCALE "',
    '"CE_OVERLAPFIX "',
    '"CE_REFRESHALL "',
    '"CE_AUTOREFRESH "',
    '"CE_REFRESHSTATUS "',
    'Point Name, X, Y, Z table',
    'sequential dynamic COGO points',
    'Title = PrefixRibbonText(title).ToUpperInvariant()',
    'Text = PrefixRibbonText(text)',
    'Text = PrefixRibbonText(definition.Text)',
    'return value.StartsWith("CE \\u2013 ", StringComparison.Ordinal)',
]

missing = [value for value in REQUIRED if value not in SOURCE]
if missing:
    raise SystemExit(
        "Remaining-comment ribbon validation failed:\n- "
        + "\n- ".join(missing)
    )

print(
    "Remaining-comment ribbon validation passed: CE naming, project styles, "
    "undo/redo, cleanup/hatch popups, coordinate wording, annotation scaling, "
    "overlap correction and dynamic refresh."
)
