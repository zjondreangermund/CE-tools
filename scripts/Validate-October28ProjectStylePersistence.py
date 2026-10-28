#!/usr/bin/env python3
"""Regression checks for Oct-28 Civil 3D Project Style Centre persistence.

These assert the UI state-storage paths by source, not Civil 3D runtime behavior.
"""
from pathlib import Path

src = Path(__file__).resolve().parents[1] / "src" / "CE.Tools.Civil3D"
centre = (src / "ProjectStyleCenterCommands.cs").read_text(encoding="utf-8")
global_preset = (src / "ProjectStylePresetManager.cs").read_text(encoding="utf-8")
discipline = (src / "August11DisciplineStylePresetCommands.cs").read_text(encoding="utf-8")
dedicated = (src / "August12DisciplineStyleCommands.cs").read_text(encoding="utf-8")

checks = {
    "editor discipline is stored separately from active production style":
        'StyleEditorRecordName = "PROJECT_STYLE_LAST_EDITED_DISCIPLINE"' in centre
        and 'SaveLastEditedDiscipline(document.Database, selection.Discipline)' in centre,
    "editor opens saved discipline, not last active production tab":
        "ReadStyleCentreSelection(document.Database)" in centre
        and "ReadLastEditedDiscipline(database)" in centre,
    "main editor loads each discipline's saved choices":
        "discipline => ReadDisciplineChoices(document.Database, discipline)" in centre
        and "August11DisciplineStylePresetManager.ReadPreset(database, discipline)" in centre,
    "global saved preset fallbacks are available per discipline":
        "CeGlobalDisciplineStyleDefaults.Read(discipline)" in centre,
    "discipline switching actually changes ComboBox style fields":
        "_discipline.SelectionChanged +=" in centre
        and "selector.Value.Text =" in centre
        and "_staged.TryGetValue(next, out chosen)" in centre,
    "user edits staged on tab switches before saving":
        "_staged[_visibleDiscipline] = Snapshot(_visibleDiscipline)" in centre,
    "no saved Road values explicitly distinguished from Water":
        "your saved choices in other disciplines have not been deleted." in centre,
    "Use Saved prioritises a populated saved preset":
        "HasConfiguredStyles(selected)" in global_preset
        and "OrderByDescending(item => item.Values.Count" in global_preset,
    "Use Saved retains editor discipline selection":
        "ProjectStyleCenterCommands.SaveLastEditedDiscipline(" in global_preset,
    "production tab activates its own preset if configured":
        "WriteRecord(database, ActiveRecordName, preset)" in discipline,
    "unconfigured discipline remains isolated from other disciplines":
        "var clean = new ProjectStyleSelection" in discipline,
    "dedicated discipline style window retains saved choices":
        "August11DisciplineStylePresetManager.ReadPreset(document.Database, discipline)" in dedicated
        and "CeGlobalDisciplineStyleDefaults.Read(discipline)" in dedicated,
}
for label, passed in checks.items():
    assert passed, label
    print("PASS:", label)
