using System.Collections.Generic;
namespace CETools.Civil3D {
internal static class SewerNetworkDynamicSequenceManager { internal static void DiscardPendingForIntervalUpdate() {} }
internal class ProductionSettingsDialogModel {
internal ProductionSettingsDialogModel(string a,string b) {}
internal void AddChoice(string a,string b,string c,string d,string e,IEnumerable<string> f) {}
internal void AddPositiveDouble(string a,string b,string c,double d,string e) {}
internal string Text(string s) => "";
internal double Double(string s,double d) => d;
}
internal static class DisciplineWorkflowDialogs {
internal static bool EditSettings(ProductionSettingsDialogModel s)=>false;
internal static bool Confirm(string a,string b)=>false;
}
}
