# Implementation status

- `CE_BMVERT`: alpha source complete; live Civil 3D validation in progress.
- `CE_TLENGTH`: alpha source complete; Civil 3D validation required.
- `CE_TAREA`: alpha source complete; Civil 3D validation required.
- `CE_COORDINATE`: installed alpha works; redesign pending the user's reference coordinate LSP.
- `CE_SEWSEQ`: alpha source complete with start/end-only workflow; Civil 3D network validation required.
- `CE_COLOR250` / `COLOR250`: alpha source complete; Civil 3D validation required.
- `CE_FLTOOLS`: first alpha source complete with `Report`, `RaiseLower` and `SetElevation`; Civil 3D 2023 compile and feature-line validation required.
- `CE_FLEDIT`: second feature-line alpha batch complete with `Create`, `Surface`, `Insert` and `Delete`; Civil 3D 2023 compile and drawing validation required.
- `CE_FLWEED`: conservative elevation-point weeding with preview, vertical tolerance and spacing tolerance is source-complete; Civil 3D 2023 compile and drawing validation required.
- `CE_ALTOOLS`: first alignment alpha batch complete with `Report`, `StationOffset` and `Label`; Civil 3D 2023 compile, station-equation and left/right validation required.
- `CE_PRTOOLS`: first profile alpha batch complete with `Report`, `Elevation` and plan `Label`; Civil 3D 2023 compile, profile-selection, station-equation and label validation required.
- `CE_SFTOOLS`: first surface alpha batch complete with `Report`, `Elevation`, `Label` and point `Compare`; Civil 3D 2023 compile, boundary, UCS and cut/fill-sign validation required.
- `CE_CORTOOLS`: first corridor alpha batch complete with `Report`, detailed `Baselines`/regions and controlled `Rebuild`; Civil 3D 2023 compile, source-resolution, reference and rebuild-transaction validation required.
- `CE_PKTOOLS`: first parking alpha batch complete with straight `Row`, aisle-centred `DoubleRow`, bay `Count` and sequential `Number`; Civil 3D 2023 compile, geometry-direction, layer, count and numbering validation required.
- `CE_PROJECT`: first project metadata alpha batch complete with `Setup`, `Info` and confirmed `Clear`; Civil 3D 2023 compile, Named Objects Dictionary, save/reopen and Save As validation required.
- `CE_COORDSYS`: first coordinate-system alpha batch complete with `Info`, validated `Assign`, library `Search` and confirmed `Clear`; Civil 3D 2023 compile, Autodesk code-library, persistence and geometry-nontransformation validation required.
- Ribbon architecture: full Project/Survey/Drawings/Geometry/Site Design/Utilities/Standards/Analysis/Production/BIM/Management/Help category and flyout conversion is specified and remains a release-gate task before the next combined public test build.
