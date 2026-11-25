namespace CETools.Civil3D {
internal static class CeThemeStore { internal static string Read()=>"Dark"; }
internal class CeWelcomeWindow : System.Windows.Window {}
internal class FloatingToolsWindow : System.Windows.Window {}
internal class DisciplineWorkflowWindow : System.Windows.Window {}
}namespace CETools.Civil3D {
internal class ProductionSettingsDialogModel {
internal ProductionSettingsDialogModel(string a,string b) {}
internal void AddChoice(string a,string b,string c,string d,string e,System.Collections.Generic.IEnumerable<string> f) {}
internal void AddPositiveDouble(string a,string b,string c,double d,string e) {}
internal void AddPositiveInteger(string a,string b,string c,int d,string e) {}
internal void AddDouble(string a,string b,string c,double d,string e) {}
internal void AddText(string a,string b,string c,string d,string e) {}
internal string Text(string s) => "";
internal double Double(string s,double d) => d;
internal int Integer(string s,int d) => d;
}
internal static class DisciplineWorkflowDialogs {
internal static bool EditSettings(ProductionSettingsDialogModel s)=>false;
}
}
