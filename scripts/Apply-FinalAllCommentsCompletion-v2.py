from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = root / "scripts" / "Apply-FinalAllCommentsCompletion.py"
text = source.read_text(encoding="utf-8")
text = text.replace(
    "IEnumerable<VertexSettingSource> sources,\\n            ObjectId ngSurfaceId\\)\\n        \\\{.*?\\n        \\\}\\n\\n        private static",
    "IList<VertexSettingSource> sources,\\n            ObjectId ngSurfaceId\\)\\n        \\\{.*?\\n        \\\}\\n\\n        private static")
code = compile(text, str(source), "exec")
exec(code, {"__name__": "__main__", "__file__": str(source)})
