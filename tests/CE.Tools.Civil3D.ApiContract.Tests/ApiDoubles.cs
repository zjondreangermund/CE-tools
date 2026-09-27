using System;
using System.Collections.Generic;
using System.Linq;

// Minimal doubles of the documented API contracts used by the linked helpers.
// No Autodesk assemblies are bundled or emulated beyond these managed contracts.
namespace Autodesk.AutoCAD.Colors
{
    public enum ColorMethod { ByAci, ByLayer }
    public sealed class Color
    {
        public ColorMethod ColorMethod { get; private set; }
        public short ColorIndex { get; private set; }
        public static Color FromColorIndex(ColorMethod method, short index)
        {
            return new Color { ColorMethod = method, ColorIndex = index };
        }
    }
}

namespace Autodesk.AutoCAD.DatabaseServices
{
    public class DBObject { public bool IsWriteEnabled { get; set; } }
    public struct ObjectId
    {
        internal DBObject Value;
        public bool IsNull { get { return Value == null; } }
        public static ObjectId Null { get { return new ObjectId(); } }
    }
    public enum OpenMode { ForRead, ForWrite }
    public sealed class Database
    {
        internal Autodesk.Civil.ApplicationServices.CivilDocument CivilDocument;
    }
    public sealed class Transaction
    {
        public DBObject GetObject(ObjectId id, OpenMode mode, bool erased)
        {
            id.Value.IsWriteEnabled = mode == OpenMode.ForWrite;
            return id.Value;
        }
    }
}

namespace Autodesk.Civil.DatabaseServices
{
    public class DBObject : Autodesk.AutoCAD.DatabaseServices.DBObject
    {
        public virtual string Name { get; set; }
    }
    public sealed class FeatureLine
    {
        public string StyleName { get; set; }
        public Autodesk.AutoCAD.DatabaseServices.ObjectId StyleId
        {
            set { StyleName = ((DBObject)value.Value).Name; }
        }
    }
    public sealed class ProfileView
    {
        public bool IsWriteEnabled { get; set; }
        public ProfileViewBandSet Bands { get; } = new ProfileViewBandSet();
    }
    public sealed class ProfileViewBandItem
    {
        public bool ShowLabels { get; set; }
        public int Profile1Id { get; set; }
        public int DataSourceId { get; set; }
        public string BandStyleName { get; set; }
        public double Gap { get; set; }
        public double MajorInterval { get; set; }
        internal ProfileViewBandItem Copy() { return (ProfileViewBandItem)MemberwiseClone(); }
    }
    public sealed class ProfileViewBandItemCollection : IDisposable
    {
        internal readonly List<ProfileViewBandItem> Items;
        internal readonly bool Top;
        private bool disposed;
        internal ProfileViewBandItemCollection(List<ProfileViewBandItem> items, bool top)
        {
            Items = items.Select(item => item.Copy()).ToList();
            Top = top;
        }
        public int Count { get { Check(); return Items.Count; } }
        public ProfileViewBandItem this[int index] { get { Check(); return Items[index]; } }
        private void Check() { if (disposed) throw new ObjectDisposedException("bands"); }
        public void Dispose() { disposed = true; }
    }
    public sealed class ProfileViewBandSet
    {
        internal List<ProfileViewBandItem> Top = new List<ProfileViewBandItem>();
        internal List<ProfileViewBandItem> Bottom = new List<ProfileViewBandItem>();
        internal bool IgnoreWrites, FailWrites;
        internal int Writes;
        public ProfileViewBandItemCollection GetTopBandItems() { return new ProfileViewBandItemCollection(Top, true); }
        public ProfileViewBandItemCollection GetBottomBandItems() { return new ProfileViewBandItemCollection(Bottom, false); }
        public void SetTopBandItems(ProfileViewBandItemCollection items)
        {
            Check(items, true);
            if (!IgnoreWrites) Top = items.Items.Select(item => item.Copy()).ToList();
        }
        public void SetBottomBandItems(ProfileViewBandItemCollection items)
        {
            Check(items, false);
            if (!IgnoreWrites) Bottom = items.Items.Select(item => item.Copy()).ToList();
        }
        private void Check(ProfileViewBandItemCollection items, bool top)
        {
            if (FailWrites) throw new InvalidOperationException("Native band write failed.");
            if (items.Top != top) throw new InvalidOperationException("Wrong band location.");
            Writes++;
        }
    }
}

namespace Autodesk.Civil.DatabaseServices.Styles
{
    using Autodesk.AutoCAD.Colors;
    using Autodesk.AutoCAD.DatabaseServices;

    public class StyleBase : Autodesk.Civil.DatabaseServices.DBObject
    {
        // AeccDbMgd exposes this setter-only override. GetProperty("Name")
        // returns it, rather than the inherited readable property.
        public override string Name { set { base.Name = value; } }
    }
    public sealed class DisplayStyle
    {
        public Color Color { get; set; } = Color.FromColorIndex(ColorMethod.ByAci, 5);
        public bool Visible { get; set; } = true;
    }
    public enum FeatureLineDisplayStyleProfileType { FeatureLine = 0 }
    public sealed class FeatureLineStyle : StyleBase
    {
        internal FeatureLineStyleCollection Owner;
        internal DisplayStyle Plan = new DisplayStyle();
        internal DisplayStyle Model = new DisplayStyle();
        internal DisplayStyle Profile = new DisplayStyle();
        public DisplayStyle GetFeatureLineDisplayStylePlan() { return Plan; }
        public DisplayStyle GetFeatureLineDisplayStyleModel() { return Model; }
        public DisplayStyle GetDisplayStyleProfile(FeatureLineDisplayStyleProfileType type) { return Profile; }
        public ObjectId CopyAsSibling(string name)
        {
            if (!IsWriteEnabled) throw new InvalidOperationException("Source style must be open for write.");
            var id = Owner.Add(name);
            var copy = (FeatureLineStyle)id.Value;
            copy.Plan.Color = Plan.Color;
            copy.Model.Color = Model.Color;
            copy.Profile.Color = Profile.Color;
            return id;
        }
    }
    public sealed class FeatureLineStyleCollection
    {
        private readonly Dictionary<string, ObjectId> values = new Dictionary<string, ObjectId>();
        public bool Contains(string name) { return values.ContainsKey(name); }
        public ObjectId this[string name] { get { return values[name]; } }
        public ObjectId Add(string name)
        {
            var style = new FeatureLineStyle { Name = name, Owner = this };
            var id = new ObjectId { Value = style };
            values.Add(name, id);
            return id;
        }
    }
    public sealed class StylesRoot
    {
        public FeatureLineStyleCollection FeatureLineStyles { get; } = new FeatureLineStyleCollection();
    }
}

namespace Autodesk.Civil.ApplicationServices
{
    public sealed class CivilDocument
    {
        public Autodesk.Civil.DatabaseServices.Styles.StylesRoot Styles { get; } =
            new Autodesk.Civil.DatabaseServices.Styles.StylesRoot();
        public static CivilDocument GetCivilDocument(Autodesk.AutoCAD.DatabaseServices.Database database)
        {
            return database.CivilDocument ?? (database.CivilDocument = new CivilDocument());
        }
    }
}
