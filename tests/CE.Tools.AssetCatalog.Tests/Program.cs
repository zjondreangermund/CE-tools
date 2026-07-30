using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CETools.Core;

namespace CETools.AssetCatalog.Tests
{
    internal static class Program
    {
        private static int _tests;

        private static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "ce-tools-phase8-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                TemplateIsNonOverwriting(root);
                RelativePathAndChecksumResolve(root);
                AuditDetectsDuplicateAndChangedAssets(root);
                SearchHonoursApprovalAndTerms(root);
                Console.WriteLine("CE Tools Phase 8 asset catalog tests passed: " + _tests);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("CE Tools Phase 8 asset catalog test failure:");
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                try { Directory.Delete(root, true); }
                catch { }
            }
        }

        private static void TemplateIsNonOverwriting(string root)
        {
            string path = Path.Combine(root, "template", "asset-catalog.csv");
            EngineeringAssetCatalog.CreateTemplate(path);
            True(File.Exists(path));
            True(Directory.Exists(Path.Combine(Path.GetDirectoryName(path), "Typical Details")));
            True(Directory.Exists(Path.Combine(Path.GetDirectoryName(path), "Furniture 3D")));
            Throws<IOException>(() => EngineeringAssetCatalog.CreateTemplate(path));
            Pass();
        }

        private static void RelativePathAndChecksumResolve(string root)
        {
            string folder = Path.Combine(root, "relative");
            Directory.CreateDirectory(Path.Combine(folder, "Typical Details"));
            string source = Path.Combine(folder, "Typical Details", "Headwall.dwg");
            File.WriteAllText(source, "headwall revision A", new UTF8Encoding(false));
            string hash = EngineeringAssetCatalog.CalculateSha256(source);
            string catalog = Path.Combine(folder, "asset-catalog.csv");
            WriteCatalog(catalog, new[]
            {
                Row("DETAIL-HEADWALL-001", "Headwall", "Typical Detail", "Stormwater", "DWG",
                    "Typical Details/Headwall.dwg", "A", "Approved", "Engineer A", "2026-07-25T00:00:00Z",
                    "1", "headwall,outlet", "Approved headwall source", hash, "true")
            });

            IList<EngineeringAssetRecord> records = EngineeringAssetCatalog.Load(catalog);
            Equal(1, records.Count);
            Equal(Path.GetFullPath(source), records[0].ResolvePath(catalog));
            Equal(hash, records[0].Sha256);
            EngineeringAssetAuditResult audit = EngineeringAssetCatalog.Audit(catalog);
            Equal(0, audit.ErrorCount);
            Pass();
        }

        private static void AuditDetectsDuplicateAndChangedAssets(string root)
        {
            string folder = Path.Combine(root, "audit");
            Directory.CreateDirectory(folder);
            string source = Path.Combine(folder, "asset.dwg");
            File.WriteAllText(source, "current bytes", new UTF8Encoding(false));
            string catalog = Path.Combine(folder, "asset-catalog.csv");
            WriteCatalog(catalog, new[]
            {
                Row("DUP-001", "Asset One", "Typical Detail", "Road", "DWG", "asset.dwg", "A",
                    "Approved", "Engineer", "2026-07-25T00:00:00Z", "1", "road", "first", new string('0', 64), "true"),
                Row("DUP-001", "Asset Two", "Typical Detail", "Road", "DWG", "missing.dwg", "B",
                    "Superseded", "Engineer", "2026-07-25T00:00:00Z", "1", "road", "second", string.Empty, "true")
            });

            EngineeringAssetAuditResult audit = EngineeringAssetCatalog.Audit(catalog);
            True(audit.ErrorCount >= 3);
            True(audit.Findings.Any(item => item.Area == "Identity"));
            True(audit.Findings.Any(item => item.Area == "Checksum"));
            True(audit.Findings.Any(item => item.Area == "Source"));
            True(audit.Findings.Any(item => item.Area == "Approval"));
            Pass();
        }

        private static void SearchHonoursApprovalAndTerms(string root)
        {
            string folder = Path.Combine(root, "search");
            Directory.CreateDirectory(folder);
            string approved = Path.Combine(folder, "approved.dwg");
            string reviewed = Path.Combine(folder, "reviewed.dwg");
            File.WriteAllText(approved, "approved", new UTF8Encoding(false));
            File.WriteAllText(reviewed, "reviewed", new UTF8Encoding(false));
            string catalog = Path.Combine(folder, "asset-catalog.csv");
            WriteCatalog(catalog, new[]
            {
                Row("KERB-001", "Mountable Kerb", "Typical Detail", "Road", "DWG", "approved.dwg", "A",
                    "Approved", "Engineer", "2026-07-25T00:00:00Z", "1", "kerb,road", "approved kerb", EngineeringAssetCatalog.CalculateSha256(approved), "true"),
                Row("VALVE-001", "Valve Chamber", "Typical Detail", "Water", "DWG", "reviewed.dwg", "A",
                    "Reviewed", "Reviewer", "2026-07-25T00:00:00Z", "1", "valve,chamber", "reviewed chamber", EngineeringAssetCatalog.CalculateSha256(reviewed), "true")
            });

            IList<EngineeringAssetRecord> approvedOnly = EngineeringAssetCatalog.Search(
                catalog, string.Empty, string.Empty, string.Empty,
                new[] { EngineeringAssetApprovalStatus.Approved }, true);
            Equal(1, approvedOnly.Count);
            Equal("KERB-001", approvedOnly[0].AssetId);

            IList<EngineeringAssetRecord> water = EngineeringAssetCatalog.Search(
                catalog, "valve chamber", "Typical Detail", "Water",
                new[] { EngineeringAssetApprovalStatus.Approved, EngineeringAssetApprovalStatus.Reviewed }, true);
            Equal(1, water.Count);
            Equal("VALVE-001", water[0].AssetId);
            Pass();
        }

        private static IList<string> Row(params string[] values)
        {
            return new List<string>(values);
        }

        private static void WriteCatalog(string path, IEnumerable<IList<string>> rows)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine(EngineeringAssetCatalog.Header);
                foreach (IList<string> row in rows)
                    writer.WriteLine(string.Join(",", row.Select(Escape)));
            }
        }

        private static string Escape(string value)
        {
            string text = value ?? string.Empty;
            return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + text.Replace("\"", "\"\"") + "\""
                : text;
        }

        private static void Pass() { _tests++; }

        private static void True(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Expected condition to be true.");
        }

        private static void Equal(int expected, int actual)
        {
            if (expected != actual)
                throw new InvalidOperationException("Expected " + expected + ", received " + actual + ".");
        }

        private static void Equal(string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Expected '" + expected + "', received '" + actual + "'.");
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected exception " + typeof(T).Name + ".");
        }
    }
}
