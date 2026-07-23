# CE_COLOR250 validation checklist

- Preselect several objects, run `CE_COLOR250`, and confirm all supported objects change to AutoCAD colour index 250.
- Run `CE_COLOR250` without preselection and confirm a normal selection prompt appears.
- Confirm objects already on colour 250 are reported separately.
- Confirm objects on locked layers are skipped and reported.
- Confirm one UNDO reverses the complete colour change.
- Confirm the alias command `COLOR250` performs the same operation.
- Confirm the CE Tools ribbon contains **Drawing → Color 250**.
