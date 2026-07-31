from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_required(path: Path, old: str, new: str) -> bool:
    text = path.read_text(encoding="utf-8")
    if old not in text:
        return False
    path.write_text(text.replace(old, new), encoding="utf-8")
    return True


def main() -> None:
    changed = []

    batch = ROOT / "src" / "CE.Tools.Civil3D" / "CivilObjectBatchStyleCommands.cs"
    text = batch.read_text(encoding="utf-8")
    patched = text.replace("? Visibility.Visible", "? System.Windows.Visibility.Visible")
    patched = patched.replace(": Visibility.Collapsed", ": System.Windows.Visibility.Collapsed")
    if patched != text:
        batch.write_text(patched, encoding="utf-8")
        changed.append(batch)

    # Guard against the exact CS0176 regression that occurred in the recovered source.
    remaining = batch.read_text(encoding="utf-8")
    forbidden = ["? Visibility.Visible", ": Visibility.Collapsed"]
    hits = [token for token in forbidden if token in remaining]
    if hits:
        raise SystemExit("Unqualified WPF Visibility references remain: " + ", ".join(hits))

    print("Recovered compile fixes applied to:")
    if changed:
        for path in changed:
            print(f"- {path.relative_to(ROOT)}")
    else:
        print("- no files required changes")


if __name__ == "__main__":
    main()
