#!/usr/bin/env python3
"""Validate the uploaded Phase 8 engineering asset catalog register."""

import csv
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "assets" / "engineering-library" / "engineering-assets.csv"
EXPECTED_COUNT = 20
HASH = re.compile(r"^[0-9a-f]{64}$")

if not CATALOG.exists():
    raise SystemExit(f"Missing uploaded asset catalog: {CATALOG.relative_to(ROOT)}")

with CATALOG.open("r", encoding="utf-8", newline="") as stream:
    rows = list(csv.DictReader(stream))

if len(rows) != EXPECTED_COUNT:
    raise SystemExit(f"Expected {EXPECTED_COUNT} uploaded asset records, found {len(rows)}")

required = {
    "AssetId", "Title", "Category", "Discipline", "AssetType", "RelativePath",
    "Revision", "ApprovalStatus", "UnitsPerMetre", "Tags", "Description", "Sha256", "IsActive",
}
missing = required.difference(rows[0].keys() if rows else ())
if missing:
    raise SystemExit("Uploaded catalog is missing columns: " + ", ".join(sorted(missing)))

ids = [row["AssetId"].strip().upper() for row in rows]
if len(ids) != len(set(ids)):
    raise SystemExit("Uploaded catalog contains duplicate AssetId values")

paths = [row["RelativePath"].strip().replace("\\", "/").lower() for row in rows]
if len(paths) != len(set(paths)):
    raise SystemExit("Uploaded catalog contains duplicate RelativePath values")

for index, row in enumerate(rows, start=2):
    asset_id = row["AssetId"].strip()
    if not asset_id:
        raise SystemExit(f"Row {index}: AssetId is blank")
    if row["ApprovalStatus"].strip() != "ForReview":
        raise SystemExit(f"Row {index} {asset_id}: uploaded assets must start as ForReview")
    if row["Revision"].strip() != "UPLOAD-01":
        raise SystemExit(f"Row {index} {asset_id}: expected revision UPLOAD-01")
    if row["IsActive"].strip().lower() != "true":
        raise SystemExit(f"Row {index} {asset_id}: expected IsActive=true")
    if row["AssetType"].strip().upper() not in {"PDF", "XLSX"}:
        raise SystemExit(f"Row {index} {asset_id}: only uploaded PDF/XLSX records are expected")
    if not HASH.fullmatch(row["Sha256"].strip().lower()):
        raise SystemExit(f"Row {index} {asset_id}: invalid SHA-256 value")
    if not row["RelativePath"].strip().startswith("Source/"):
        raise SystemExit(f"Row {index} {asset_id}: RelativePath must be under Source/")

print(f"Uploaded Phase 8 asset catalog validation passed: {len(rows)} records.")
