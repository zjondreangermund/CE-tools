"""Check the uploaded compact ribbon retains reachable commands and shared tab identity."""
from pathlib import Path
import re
root = Path(__file__).resolve().parents[1]
src = root / 'src/CE.Tools.Civil3D'
read = lambda n: (src / n).read_text()
plugin = read('PluginEntry.cs')
production = read('August11ProductionCentreCommands.cs')
floating = read('FloatingToolsWindow.cs')
help_text = read('CeHelpCentreCommands.cs')
registry = '\n'.join(p.read_text() for p in src.glob('*.cs'))
owners = set(re.findall(r'CommandMethod\("CE_TOOLS",\s*"([^"]+)"', registry))
definitions = re.findall(r'new ProductionTabDefinition \{[^\n]*Command = "([^\"]+)"', production)
assert len(definitions) == 12
assert all(command.strip() in owners for command in definitions)
assert 'tab.Id == ProductionWorkflowRibbonBuilder.HorizontalProductionTabId' in plugin
assert 'item.Id == ProductionWorkflowRibbonBuilder.HorizontalProductionTabId' in floating
assert 'tabs.Add(RibbonBuilder.CreateCommandCatalogueTab())' in floating
# Every previous specialist panel remains in the detached, searchable catalogue.
catalogue = plugin.split('internal static RibbonTab CreateCommandCatalogueTab()', 1)[1].split('private static void AddAssemblyPanel', 1)[0]
for panel in ['Project', 'Survey', 'Drawings', 'Geometry', 'Assembly', 'RoadProduction', 'Corridor', 'SiteDesign', 'Utilities', 'Standards', 'Analysis', 'Advanced', 'Production']:
    assert f'Add{panel}Panel(tab);' in catalogue
assert 'direct.CommandParameter' in floating
assert 'value = value.Substring(3).Trim();' in help_text
assert 'CE_HELP' in owners and 'CE_TOOLSHELP' in owners
for token in ['CE_REFRESHALL', 'CE_REFRESHDISMISS', 'CE_REFRESHSTATUS', 'CE_AUTOREFRESH']:
    assert token in read('DynamicRefreshContextMenu.cs')
for event in ['SizeChanged', 'PreviewMouseLeftButtonUp', 'PreviewKeyUp']:
    assert f'.{event} -=' in production
assert 'ProductionWorkflowRibbonBuilder.Detach();' in plugin
repair = (root / 'scripts/Repair-August12SurveyGridAndDisplayNames-Civil3D2023.ps1').read_text()
assert repair.count("$text.Contains('WrapProductionRibbonLabel(definition.ButtonText)')") == 1
assert "$productionText.Contains('WrapProductionRibbonLabel(definition.ButtonText)')" in repair
print('PASS: twelve production targets, shared tab identity, complete specialist catalogue, Help, refresh controls and event cleanup.')
