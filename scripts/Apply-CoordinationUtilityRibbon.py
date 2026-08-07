#!/usr/bin/env python3
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]

p=ROOT/'src'/'CE.Tools.Civil3D'/'PluginEntry.cs'
t=p.read_text(encoding='utf-8')
def rep(old,new):
 global t
 if old not in t: raise SystemExit('PluginEntry marker missing: '+old[:180])
 t=t.replace(old,new,1)
rep('''                        Cmd("Phase 1 Utilities", "CE_PHASE1 ", "Open every original CE Tools Phase 1 utility family in one visual hub."),\n                        Cmd("Project Setup", "CE_PROJECTSETUP ", "Create or update project metadata and review it in a pop-up."),''',
'''                        Cmd("Phase 1 Utilities", "CE_PHASE1 ", "Open every original CE Tools Phase 1 utility family in one visual hub."),\n                        Cmd("Project Coordination", "CE_PROJECTCOORDINATION ", "Coordinate discipline XREFs, paper-space page setups and survey/map location tools."),\n                        Cmd("Cadastral Utility Planner", "CE_UTILITYPLANNER ", "Prepare linked cadastral utility routes, constraints, planning manhole points and downstream Sewer/SW/Water workflows."),\n                        Cmd("Project Setup", "CE_PROJECTSETUP ", "Create or update project metadata and review it in a pop-up."),''')
rep('''                        Cmd("Coordinate System Tools", "CE_COORDSYS ", "Open the coordinate-system menu."),\n                        Cmd("Information", "CE_COORDSYSINFO ", "Report the current coordinate system."),''',
'''                        Cmd("Coordinate System Tools", "CE_COORDSYS ", "Open the coordinate-system menu."),\n                        Cmd("Survey Location and Coordinate System", "CE_SURVEYLOCATION ", "Choose a Namibian project town and assign the matching installed Autodesk LO system when available."),\n                        Cmd("Latitude / Longitude Map Tools", "CE_MAPLOCATION ", "Open entered WGS84 latitude/longitude in Google Maps or Google Earth without changing drawing geometry."),\n                        Cmd("Information", "CE_COORDSYSINFO ", "Report the current coordinate system."),''')
rep('''                        Cmd("Open Workflow Centre", "CE_TOOLSPALETTE ", "Open every ribbon workflow and searchable command in the floating window."),\n                        Cmd("Phase 1 Utilities", "CE_PHASE1 ", "Open the completed Phase 1 utility hub."),''',
'''                        Cmd("Open Workflow Centre", "CE_TOOLSPALETTE ", "Open every ribbon workflow and searchable command in the floating window."),\n                        Cmd("Create Coordinated Master XREF Drawing", "CE_MASTERXREF ", "Create a new non-destructive master DWG referencing Roads, Stormwater, Sewer and Water source drawings at the same origin."),\n                        Cmd("Multi-Layout Page Setup Manager", "CE_PAGESETUPMANAGER ", "Copy one paper-space layout page setup to multiple layouts in a popup workflow."),\n                        Cmd("Phase 1 Utilities", "CE_PHASE1 ", "Open the completed Phase 1 utility hub."),''')
rep('''                        Cmd("Bellmouth Densifier", "CE_BMVERT ", "Add equal-chainage vertices to bellmouth polylines.")),''',
'''                        Cmd("Bellmouth Densifier", "CE_BMVERT ", "Add equal-chainage vertices to bellmouth polylines."),\n                        Cmd("Road Settings", "CE_ROADSETTINGS ", "Choose road-only alignment, profile, profile-view band-set, corridor, code-set and assembly styles.")),''')
rep('''                        Cmd("Feature Line Annotation", "CE_FLLABELX ", "Create a feature-line MLeader, MText or COGO point using shared settings."),\n                        Cmd("Raise / Lower",''',
'''                        Cmd("Feature Line Annotation", "CE_FLLABELX ", "Create a feature-line MLeader, MText or COGO point using shared settings."),\n                        Cmd("Dynamic Vertex Points and Table", "CE_FLVERTEXLABELS ", "Create linked feature-line vertex COGO/MText/MLeader points and an optional dynamic XYZ table."),\n                        Cmd("Raise / Lower",''')
rep('''                Row(\n                    Menu(\n                        "CE_TOOLS_STORMWATER_MENU",''',
'''                Row(\n                    Menu(\n                        "CE_TOOLS_UTILITY_PLANNER_MENU",\n                        "Utility\\nPlanning",\n                        "Cadastral route preparation, constraints and linked discipline-production handoff.",\n                        Cmd("Cadastral Utility Planner", "CE_UTILITYPLANNER ", "Open cadastral route preparation and downstream network workflows."),\n                        Cmd("Create Linked Cadastral Routes", "CE_UTILITYROUTES ", "Create inward-offset utility planning routes, manhole planning points and a constraint report."),\n                        Cmd("Refresh Linked Cadastral Routes", "CE_UTILITYROUTESREFRESH ", "Refresh utility routes from edited source erf/cadastral polylines."),\n                        Cmd("Prepare Crossings and Junctions", "CE_PLBREAKJUNCTIONS ", "Break prepared utility polylines at true crossings and T-junctions.")),\n                    Menu(\n                        "CE_TOOLS_STORMWATER_MENU",''')
p.write_text(t,encoding='utf-8')

# Feature-line COGO labels must immediately inherit the saved project point styles.
p=ROOT/'src'/'CE.Tools.Civil3D'/'FeatureProfileSurfaceCommentCommands.cs'
f=p.read_text(encoding='utf-8')
old='''            CommentAutoRefreshManager.MarkPending();\n\n            document.Editor.WriteMessage(\n                "\\nCE_FLVERTEXLABELS complete.'''
new='''            CommentAutoRefreshManager.MarkPending();\n            try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, true); } catch { }\n\n            document.Editor.WriteMessage(\n                "\\nCE_FLVERTEXLABELS complete.'''
if old not in f: raise SystemExit('Feature-line COGO style-sync marker missing')
f=f.replace(old,new,1)
p.write_text(f,encoding='utf-8')

# Use the established report presenter API.
p=ROOT/'src'/'CE.Tools.Civil3D'/'UtilityPlanningCommands.cs'
u=p.read_text(encoding='utf-8')
old='''            GridReportPresenter.Show(\n                document,\n                "CE Tools - Utility Route Planning Report",\n                "Linked planning geometry only. Convert/sequence it through the Sewer, Stormwater or Water workflow using the active Civil 3D part catalogue and authority standards.",\n                new[] { "Item", "Value" },\n                rows,\n                false);'''
new='''            GridReportPresenter.ShowReportAndOfferTable(\n                document,\n                "CE Tools - Utility Route Planning Report",\n                "Linked planning geometry only. Convert/sequence it through the Sewer, Stormwater or Water workflow using the active Civil 3D part catalogue and authority standards.",\n                new List<string> { "Item", "Value" },\n                rows,\n                "CE TOOLS UTILITY ROUTE PLANNING");'''
if old not in u: raise SystemExit('Utility report presenter marker missing')
u=u.replace(old,new,1)
p.write_text(u,encoding='utf-8')

# Replace the unshared review helper with established WPF MessageBox confirmation.
p=ROOT/'src'/'CE.Tools.Civil3D'/'ProjectCoordinationCommands.cs'
c=p.read_text(encoding='utf-8')
if 'using System.Windows;' not in c:
    c=c.replace('using System.Linq;\n', 'using System.Linq;\nusing System.Windows;\n', 1)
start='''            var review = files.Select(item =>\n                new KeyValuePair<string, string>(item.Discipline, item.Path)).ToList();\n            review.Insert(0, new KeyValuePair<string, string>("Master drawing", output));\n            review.Insert(1, new KeyValuePair<string, string>("Source drawings changed", "No"));\n            review.Insert(2, new KeyValuePair<string, string>("Insertion", "0,0,0 / Overlay XREF / one layer per discipline"));\n            if (!PopupTablePresenter.ShowReview(\n                    "CE Tools - Coordinated Master Drawing",\n                    "The four discipline drawings remain separate. CE Tools creates a new master DWG and references each source at the same origin so Civil 3D design objects remain in their source drawings.",\n                    review,\n                    "Create Master Drawing")) return;'''
replacement='''            string reviewText =\n                "Create a new coordinated master drawing?\\n\\n" +\n                "Master: " + output + "\\n" +\n                string.Join("\\n", files.Select(item => item.Discipline + ": " + item.Path)) +\n                "\\n\\nInsertion: 0,0,0 as XREFs. Source discipline DWGs are not modified.";\n            if (MessageBox.Show(\n                    reviewText,\n                    "CE Tools - Coordinated Master Drawing",\n                    MessageBoxButton.OKCancel,\n                    MessageBoxImage.Question) != MessageBoxResult.OK) return;'''
if start not in c: raise SystemExit('Master XREF review marker missing')
c=c.replace(start,replacement,1)
p.write_text(c,encoding='utf-8')
print('Coordination/utility, road and feature-line integration applied.')