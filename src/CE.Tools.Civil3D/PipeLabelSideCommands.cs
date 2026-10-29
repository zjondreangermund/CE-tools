using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PipeLabelSideCommands))]
namespace CETools.Civil3D
{
    public sealed class PipeLabelSideCommands
    {
        private const string Suffix = " [CE Length Above]";
        [CommandMethod("CE_TOOLS", "CE_PIPELABELSIDES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Arrange()
        {
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            using (doc.LockDocument()) Apply(doc, null);
            doc.Editor.Regen();
        }
        internal static void Apply(Document doc, ISet<ObjectId> selectedParts)
        {
            int changed = 0, skipped = 0;
            var styles = new Dictionary<ObjectId, ObjectId>();
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                foreach (ObjectId id in model)
                {
                    var label = tr.GetObject(id, OpenMode.ForRead);
                    if (label == null || label.GetType().Name != "PipeLabel") continue;
                    var parent = Read(label, "FeatureId") ?? Read(label, "PipeId");
                    if (selectedParts != null && (!(parent is ObjectId) || !selectedParts.Contains((ObjectId)parent))) continue;
                    object styleValue = Read(label, "StyleId");
                    if (!(styleValue is ObjectId) || ((ObjectId)styleValue).IsNull) { skipped++; continue; }
                    ObjectId styleId = (ObjectId)styleValue, replacement;
                    if (!styles.TryGetValue(styleId, out replacement))
                    {
                        try { replacement = Prepare(tr, styleId); }
                        catch (System.Exception ex)
                        {
                            doc.Editor.WriteMessage("\nPipe style {0} could not be arranged: {1}", styleId.Handle, ex.Message);
                            replacement = ObjectId.Null;
                        }
                        styles[styleId] = replacement;
                    }
                    if (replacement.IsNull) { skipped++; continue; }
                    label.UpgradeOpen();
                    var property = label.GetType().GetProperty("StyleId");
                    if (property == null || !property.CanWrite) { skipped++; continue; }
                    property.SetValue(label, replacement, null); changed++;
                }
                tr.Commit();
            }
            doc.Editor.WriteMessage("\nPipe label layout: updated={0}; unsupported/combined styles skipped={1}. Length above; diameter/class below the readable pipe direction.", changed, skipped);
        }
        private static ObjectId Prepare(Transaction tr, ObjectId id)
        {
            var original = tr.GetObject(id, OpenMode.ForRead);
            var originalComponents = TextComponents(original, tr).ToList();
            if (!originalComponents.Any(c => Category(c) == 1) || !originalComponents.Any(c => Category(c) == -1)) return ObjectId.Null;
            string name = Convert.ToString(Read(original, "Name"), CultureInfo.InvariantCulture) ?? "";
            ObjectId result = id;
            object style;
            if (!name.EndsWith(Suffix, StringComparison.Ordinal))
            {
                var copy = original.GetType().GetMethod("CopyAsSibling", new[] { typeof(string) });
                if (copy == null) return ObjectId.Null;
                string derivedName = name + " " + id.Handle + Suffix;
                var collection = SewerNetworkLabelCommands.ReadLabelStyleCollection(Autodesk.Civil.ApplicationServices.CivilApplication.ActiveDocument, true);
                var existing = CivilStyleCatalogV2.ReadObjectIds(collection, tr).FirstOrDefault(candidate =>
                    string.Equals(Convert.ToString(Read(tr.GetObject(candidate, OpenMode.ForRead), "Name")), derivedName, StringComparison.Ordinal));
                if (!existing.IsNull) result = existing;
                else
                {
                    original.UpgradeOpen();
                    object value = copy.Invoke(original, new object[] { derivedName });
                    if (!(value is ObjectId)) return ObjectId.Null;
                    result = (ObjectId)value;
                }
            }
            style = tr.GetObject(result, OpenMode.ForWrite);
            SetPlanReadability(Read(style, "Properties"), 0);
            foreach (var component in TextComponents(style, tr))
            {
                int category = Category(component);
                if (category == 0) continue;
                var text = Read(component, "Text");
                var offset = Read(text, "YOffset");
                double current = Number(Read(offset, "Value"));
                double height = Number(Read(Read(text, "Height"), "Value"));
                double distance = Math.Max(Math.Abs(current), Math.Max(height * 0.75, 0.001));
                if (!WriteValue(offset, category * distance)) return ObjectId.Null;
                // Anchor offsets remain relative to the native pipe label direction;
                // flipping anchors with readable text keeps their visual sides consistent.
                SetEnumValue(Read(text, "Attachment"), category > 0 ? "BottomCenter" : "TopCenter");
            }
            return result;
        }
        private static int Category(object component)
        {
            string text = Convert.ToString(Read(Read(Read(component, "Text"), "Contents"), "Value")) ?? "";
            string value = (Convert.ToString(Read(component, "Name")) + " " + text).ToUpperInvariant();
            bool length = value.Contains("LENGTH");
            bool diameter = value.Contains("DIAMETER") || value.Contains("CLASS") || value.Contains("MATERIAL") || value.Contains("WIDTH") || value.Contains("Ø");
            return length == diameter ? 0 : length ? 1 : -1;
        }
        private static IEnumerable<object> TextComponents(object style, Transaction tr)
        {
            var method = style.GetType().GetMethods().FirstOrDefault(m => m.Name == "GetComponents" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
            if (method == null) yield break;
            var type = method.GetParameters()[0].ParameterType;
            string key = Enum.GetNames(type).FirstOrDefault(n => n == "Text");
            if (key == null) yield break;
            var values = method.Invoke(style, new[] { Enum.Parse(type, key) }) as IEnumerable;
            if (values == null) yield break;
            foreach (object value in values)
                yield return value is ObjectId ? tr.GetObject((ObjectId)value, OpenMode.ForWrite) : value;
        }
        private static void SetPlanReadability(object value, int depth)
        {
            if (value == null || depth > 4) return;
            WriteValue(Read(value, "PlanReadable"), true);
            WriteValue(Read(value, "FlipAnchorsWithText"), true);
            foreach (string property in new[] { "Label", "General", "PlanReadability" }) SetPlanReadability(Read(value, property), depth + 1);
        }
        private static object Read(object value, string name)
        { return value == null ? null : value.GetType().GetProperty(name)?.GetValue(value, null); }
        private static double Number(object value)
        { return value == null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        private static bool WriteValue(object wrapper, object value)
        {
            if (wrapper == null) return false;
            var property = wrapper.GetType().GetProperty("Value");
            if (property == null || !property.CanWrite) return false;
            property.SetValue(wrapper, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture), null); return true;
        }
        private static void SetEnumValue(object wrapper, string name)
        {
            if (wrapper == null) return;
            var property = wrapper.GetType().GetProperty("Value");
            if (property == null || !property.CanWrite || !property.PropertyType.IsEnum) return;
            string found = Enum.GetNames(property.PropertyType).FirstOrDefault(n => n.Replace("_", "").Equals(name, StringComparison.OrdinalIgnoreCase));
            if (found != null) property.SetValue(wrapper, Enum.Parse(property.PropertyType, found), null);
        }
    }
}
