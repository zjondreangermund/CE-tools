from pathlib import Path

source = Path(__file__).resolve().parent / "Apply-FinalAllCommentsCompletion.py"
text = source.read_text(encoding="utf-8")
needle = "IEnumerable<VertexSettingSource> sources"
if needle not in text:
    raise RuntimeError("Expected old ApplyLevelReferences source-shape marker was not found in patch script.")
text = text.replace(needle, "IList<VertexSettingSource> sources", 1)
exec(compile(text, str(source), "exec"), {"__name__": "__main__", "__file__": str(source)})
