#!/usr/bin/env python3
"""Protect CE Tools versioning, release packaging and verified installation."""

from __future__ import annotations

from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
errors: list[str] = []


def require(path: str, markers: tuple[str, ...] = ()) -> str:
    target = ROOT / path
    if not target.is_file():
        errors.append(f"Missing release-pipeline file: {path}")
        return ""
    text = target.read_text(encoding="utf-8-sig")
    for marker in markers:
        if marker not in text:
            errors.append(f"{path} is missing marker: {marker}")
    return text


props = require(
    "Directory.Build.props",
    ("<VersionPrefix>", "<AssemblyVersion>", "<FileVersion>", "<InformationalVersion>"),
)
package_path = ROOT / "bundle" / "CE Tools.bundle" / "PackageContents.xml"
try:
    package_version = ET.parse(package_path).getroot().attrib["AppVersion"]
except Exception as exc:  # pragma: no cover - validator diagnostic
    errors.append(f"Could not read PackageContents.xml AppVersion: {exc}")
    package_version = ""

assembly_match = re.search(r"<AssemblyVersion>([^<]+)</AssemblyVersion>", props)
assembly_version = assembly_match.group(1) if assembly_match else ""
if package_version != assembly_version:
    errors.append(
        f"Package AppVersion {package_version!r} does not match AssemblyVersion {assembly_version!r}"
    )

require(
    "scripts/New-CE-ToolsReleasePackage.ps1",
    (
        "release-manifest.json",
        "SHA256SUMS.txt",
        "CE-Tools-$releaseLabel-Civil3D-2023-$commitLabel",
        "INSTALL-CE-TOOLS.cmd",
    ),
)
require(
    "scripts/Install-CE-Tools-Release.ps1",
    ("WindowsBuiltInRole]::Administrator", "Get-Process -Name acad", "Install-VerifiedCivil3D2023Bundle.ps1"),
)
require(
    "scripts/Install-VerifiedCivil3D2023Bundle.ps1",
    ("SHA256", "rollback", "SourceCommit"),
)
require("INSTALL-CE-TOOLS.cmd", ("Install-CE-Tools-Release.ps1",))
release_source = require(
    "src/CE.Tools.Civil3D/ReleaseInfoCommands.cs",
    ("CE_VERSION", "CE_INSTALLVERIFY", "CE_UPDATECHECK", "release-manifest.json"),
)
settings_source = require(
    "src/CE.Tools.Civil3D/SettingsCenterCommands.cs",
    ("CE_SETTINGS", "CE_SETTINGSAUDIT", "CE_SWSETTINGS", "CE_SEWSETTINGS", "CE_WATERSETTINGS"),
)
workflow = require(
    ".github/workflows/civil3d-2023-package.yml",
    ("self-hosted", "Civil3D2023", "upload-artifact@v4", "New-CE-ToolsReleasePackage.ps1"),
)

if release_source.count("[CommandMethod") < 5:
    errors.append("ReleaseInfoCommands.cs must retain the five release-management commands")
if settings_source.count("[CommandMethod") < 3:
    errors.append("SettingsCenterCommands.cs must retain the settings centre, alias and audit")
if "workflow_dispatch:" not in workflow:
    errors.append("Civil 3D package workflow must remain manually dispatchable")
if "branches:" not in workflow or "- main" not in workflow:
    errors.append("Civil 3D package workflow must run automatically for main")

if errors:
    print("CE Tools release-pipeline validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "CE Tools release pipeline passed: version parity, settings centre, "
    "manifest packaging, verified installer and Windows build workflow are present."
)
