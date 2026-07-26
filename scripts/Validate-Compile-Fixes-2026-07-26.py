#!/usr/bin/env python3
"""Guard the generated Civil 3D 2023 compatibility source against syntax regressions."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(relative: str) -> str:
    path = ROOT / relative
    if not path.exists():
        raise SystemExit(f"Missing required file: {relative}")
    return path.read_text(encoding="utf-8")


model_path = "src/CE.Tools.Civil3D/ModelDesignAuditCommands.cs"
model = read(model_path)
correct_layout_block = '''                snapshot.Layouts.Add(new LayoutAuditItem(
                    layout.LayoutName,
                    layout.ModelType,
                    viewports,
                    layout.TabOrder,
                    layout.CanonicalMediaName,
                    Convert.ToString(
                        ReadProperty(layout, "ConfigName") ??
                        ReadProperty(layout, "PlotConfigurationName"),
                        CultureInfo.CurrentCulture)));'''
if correct_layout_block not in model:
    raise SystemExit("The Civil 3D 2023 layout plot-configuration block is missing or malformed")

for bad in (
    "layout.ConfigName",
    '\\"ConfigName\\"',
    '\\"PlotConfigurationName\\"',
    "`\"ConfigName",
    "`\"PlotConfigurationName",
):
    if bad in model:
        raise SystemExit(f"Malformed layout compatibility text remains: {bad}")

# These compact checks guard the other compiler-error groups without relying on
# unrelated implementation details in the same files.
grid = read("src/CE.Tools.Civil3D/GridReportPresenter.cs")
if grid.count("public static void ShowReportAndOfferTable(") < 2:
    raise SystemExit("The report presenter compatibility overload is missing")

core = read("src/CE.Tools.Core/SimplePresentationPackage.cs")
if ": this(title, subject, author, company, DateTime.UtcNow, slides)" not in core:
    raise SystemExit("The presentation-deck compatibility constructor is missing")
if ": this(title, subtitle, bullets, metrics)" not in core:
    raise SystemExit("The presentation-slide compatibility constructor is missing")

print("Civil 3D 2023 generated compiler compatibility source validated.")
