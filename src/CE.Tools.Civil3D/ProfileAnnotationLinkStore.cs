using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;

namespace CETools.Civil3D
{
    /// <summary>
    /// Keeps a profile annotation attached to a draggable DBPoint. Moving the
    /// point in plan changes station; elevation, grade and text are recalculated
    /// from the linked Civil profile during automatic or manual refresh.
    /// </summary>
    internal static class ProfileAnnotationLinkStore
    {
        private const string RecordName = "CE_PROFILE_ANNOTATION_LINK";
        private const double Tolerance = 0.0000001;

        public static void Link(
            Database database,
            ObjectId profileId,
            ObjectId sourcePointId,
            Point3d lastPoint,
            IEnumerable<ObjectId> outputIds)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Write(transaction.GetObject(sourcePointId, OpenMode.ForWrite, false),
                    transaction, profileId, sourcePointId, lastPoint);
                if (outputIds != null)
                {
                    foreach (ObjectId outputId in outputIds)
                    {
                        if (outputId.IsNull || outputId == sourcePointId) continue;
                        Write(transaction.GetObject(outputId, OpenMode.ForWrite, false),
                            transaction, profileId, sourcePointId, lastPoint);
                    }
                }
                transaction.Commit();
            }
        }

        public static int RefreshAll(Document document)
        {
            int changed = 0;
            Database database = document.Database;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in EntityIds(database, transaction))
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForWrite, false); }
                    catch { continue; }
                    LinkData link;
                    if (!TryRead(database, value, transaction, out link)) continue;
                    ProfileResult result;
                    try { result = Calculate(transaction, link); }
                    catch { continue; }

                    if (id == link.SourcePointId)
                    {
                        DBPoint source = value as DBPoint;
                        if (source != null &&
                            Math.Abs(source.Position.Z - result.Point.Z) > Tolerance)
                        {
                            source.Position = result.Point;
                            changed++;
                        }
                    }
                    else
                    {
                        Entity entity = value as Entity;
                        if (entity != null)
                        {
                            Vector3d displacement = result.Point - link.LastPoint;
                            if (displacement.Length > Tolerance)
                            {
                                try
                                {
                                    entity.TransformBy(Matrix3d.Displacement(displacement));
                                    changed++;
                                }
                                catch { }
                            }
                        }
                        if (Update(value, result)) changed++;
                    }
                    Write(value, transaction, link.ProfileId, link.SourcePointId, result.Point);
                }
                transaction.Commit();
            }
            return changed;
        }

        private static ProfileResult Calculate(Transaction transaction, LinkData link)
        {
            CivilProfile profile = transaction.GetObject(
                link.ProfileId, OpenMode.ForRead, false) as CivilProfile;
            if (profile == null) throw new InvalidOperationException();
            CivilAlignment alignment = transaction.GetObject(
                profile.AlignmentId, OpenMode.ForRead, false) as CivilAlignment;
            DBPoint source = transaction.GetObject(
                link.SourcePointId, OpenMode.ForRead, false) as DBPoint;
            if (alignment == null || source == null) throw new InvalidOperationException();
            double station = 0.0;
            double offset = 0.0;
            alignment.StationOffset(
                source.Position.X,
                source.Position.Y,
                ref station,
                ref offset);
            double elevation = profile.ElevationAt(station);
            double grade = profile.GradeAt(station) * 100.0;
            string stationText;
            try { stationText = alignment.GetStationStringWithEquations(station); }
            catch { stationText = station.ToString("N3", CultureInfo.CurrentCulture); }
            return new ProfileResult(
                profile.Name,
                stationText,
                station,
                elevation,
                grade,
                new Point3d(source.Position.X, source.Position.Y, elevation));
        }

        private static bool Update(DBObject value, ProfileResult result)
        {
            string contents = string.Join(
                "\\P",
                result.ProfileName,
                "STA: " + result.StationText,
                "ELEV: " + result.Elevation.ToString("N3", CultureInfo.CurrentCulture),
                "GRADE: " + result.Grade.ToString("N3", CultureInfo.CurrentCulture) + "%");
            MText text = value as MText;
            if (text != null)
            {
                if (text.Contents == contents) return false;
                text.Contents = contents;
                return true;
            }
            MLeader leader = value as MLeader;
            if (leader != null && leader.ContentType == ContentType.MTextContent)
            {
                MText leaderText = leader.MText;
                if (leaderText == null || leaderText.Contents == contents) return false;
                leaderText.Contents = contents;
                leader.MText = leaderText;
                return true;
            }
            return false;
        }

        private static void Write(
            DBObject target,
            Transaction transaction,
            ObjectId profileId,
            ObjectId sourceId,
            Point3d point)
        {
            if (target.ExtensionDictionary.IsNull) target.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                target.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(RecordName))
                record = transaction.GetObject(
                    dictionary.GetAt(RecordName), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(RecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Profile=" + profileId.Handle),
                new TypedValue((int)DxfCode.Text, "Source=" + sourceId.Handle),
                new TypedValue((int)DxfCode.Text, "X=" + point.X.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Y=" + point.Y.ToString("R", CultureInfo.InvariantCulture)),
                new TypedValue((int)DxfCode.Text, "Z=" + point.Z.ToString("R", CultureInfo.InvariantCulture)));
        }

        private static bool TryRead(
            Database database,
            DBObject target,
            Transaction transaction,
            out LinkData link)
        {
            link = null;
            if (target == null || target.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(
                target.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(RecordName)) return false;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(RecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) return false;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue item in record.Data)
            {
                string text = item.Value as string;
                int split = string.IsNullOrEmpty(text) ? -1 : text.IndexOf('=');
                if (split > 0) values[text.Substring(0, split)] = text.Substring(split + 1);
            }
            ObjectId profileId;
            ObjectId sourceId;
            double x;
            double y;
            double z;
            if (!Resolve(database, Value(values, "Profile"), out profileId) ||
                !Resolve(database, Value(values, "Source"), out sourceId) ||
                !double.TryParse(Value(values, "X"), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !double.TryParse(Value(values, "Y"), NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                !double.TryParse(Value(values, "Z"), NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                return false;
            link = new LinkData(profileId, sourceId, new Point3d(x, y, z));
            return true;
        }

        private static string Value(IDictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static bool Resolve(Database database, string handleText, out ObjectId id)
        {
            id = ObjectId.Null;
            long value;
            if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;
            try
            {
                id = database.GetObjectId(false, new Handle(value), 0);
                return !id.IsNull && !id.IsErased;
            }
            catch { return false; }
        }

        private static IEnumerable<ObjectId> EntityIds(
            Database database,
            Transaction transaction)
        {
            BlockTable table = transaction.GetObject(
                database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            foreach (ObjectId recordId in table)
            {
                BlockTableRecord record = transaction.GetObject(
                    recordId, OpenMode.ForRead, false) as BlockTableRecord;
                if (record == null || record.IsFromExternalReference) continue;
                foreach (ObjectId id in record) yield return id;
            }
        }

        private sealed class LinkData
        {
            public LinkData(ObjectId profileId, ObjectId sourcePointId, Point3d lastPoint)
            {
                ProfileId = profileId;
                SourcePointId = sourcePointId;
                LastPoint = lastPoint;
            }
            public ObjectId ProfileId { get; private set; }
            public ObjectId SourcePointId { get; private set; }
            public Point3d LastPoint { get; private set; }
        }

        private sealed class ProfileResult
        {
            public ProfileResult(
                string profileName,
                string stationText,
                double station,
                double elevation,
                double grade,
                Point3d point)
            {
                ProfileName = profileName;
                StationText = stationText;
                Station = station;
                Elevation = elevation;
                Grade = grade;
                Point = point;
            }
            public string ProfileName { get; private set; }
            public string StationText { get; private set; }
            public double Station { get; private set; }
            public double Elevation { get; private set; }
            public double Grade { get; private set; }
            public Point3d Point { get; private set; }
        }
    }
}
