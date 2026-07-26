using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CETools.Core
{
    public enum EngineeringAssetApprovalStatus
    {
        Draft,
        ForReview,
        Reviewed,
        Approved,
        Superseded
    }

    public enum EngineeringAssetAuditSeverity
    {
        Information,
        Review,
        Warning,
        Error
    }

    public sealed class EngineeringAssetRecord
    {
        public string AssetId { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public string Discipline { get; set; }
        public string AssetType { get; set; }
        public string RelativePath { get; set; }
        public string Revision { get; set; }
        public EngineeringAssetApprovalStatus ApprovalStatus { get; set; }
        public string ApprovedBy { get; set; }
        public string ApprovalDateUtc { get; set; }
        public double UnitsPerMetre { get; set; }
        public string Tags { get; set; }
        public string Description { get; set; }
        public string Sha256 { get; set; }
        public bool IsActive { get; set; }

        public string ResolvePath(string catalogPath)
        {
            if (string.IsNullOrWhiteSpace(RelativePath)) return string.Empty;
            if (Path.IsPathRooted(RelativePath)) return Path.GetFullPath(RelativePath);
            string root = Path.GetDirectoryName(Path.GetFullPath(catalogPath));
            return Path.GetFullPath(Path.Combine(root ?? string.Empty, RelativePath));
        }

        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            string[] terms = query
                .Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            string haystack = string.Join(" ", new[]
            {
                AssetId, Title, Category, Discipline, AssetType, Revision, Tags, Description
            }.Where(item => !string.IsNullOrWhiteSpace(item))).ToUpperInvariant();
            return terms.All(term => haystack.Contains(term.ToUpperInvariant()));
        }
    }

    public sealed class EngineeringAssetAuditFinding
    {
        public EngineeringAssetAuditFinding(
            EngineeringAssetAuditSeverity severity,
            string assetId,
            string area,
            string finding,
            string action)
        {
            Severity = severity;
            AssetId = assetId ?? string.Empty;
            Area = area ?? string.Empty;
            Finding = finding ?? string.Empty;
            Action = action ?? string.Empty;
        }

        public EngineeringAssetAuditSeverity Severity { get; private set; }
        public string AssetId { get; private set; }
        public string Area { get; private set; }
        public string Finding { get; private set; }
        public string Action { get; private set; }
    }

    public sealed class EngineeringAssetAuditResult
    {
        public EngineeringAssetAuditResult(
            IList<EngineeringAssetRecord> records,
            IList<EngineeringAssetAuditFinding> findings)
        {
            Records = records == null
                ? new List<EngineeringAssetRecord>()
                : new List<EngineeringAssetRecord>(records);
            Findings = findings == null
                ? new List<EngineeringAssetAuditFinding>()
                : new List<EngineeringAssetAuditFinding>(findings);
        }

        public IList<EngineeringAssetRecord> Records { get; private set; }
        public IList<EngineeringAssetAuditFinding> Findings { get; private set; }
        public int ErrorCount { get { return Findings.Count(item => item.Severity == EngineeringAssetAuditSeverity.Error); } }
        public int WarningCount { get { return Findings.Count(item => item.Severity == EngineeringAssetAuditSeverity.Warning); } }
        public int ReviewCount { get { return Findings.Count(item => item.Severity == EngineeringAssetAuditSeverity.Review); } }
    }

    public static class EngineeringAssetCatalog
    {
        public const int MaximumAssets = 10000;
        public const string Header =
            "AssetId,Title,Category,Discipline,AssetType,RelativePath,Revision,ApprovalStatus,ApprovedBy,ApprovalDateUtc,UnitsPerMetre,Tags,Description,Sha256,IsActive";

        private static readonly HashSet<string> SupportedTypes =
            new HashSet<string>(new[]
            {
                "DWG", "DXF", "PDF", "PNG", "JPG", "JPEG", "SVG", "WEBP", "XLSX", "DOCX"
            }, StringComparer.OrdinalIgnoreCase);

        public static void CreateTemplate(string catalogPath)
        {
            if (string.IsNullOrWhiteSpace(catalogPath))
                throw new ArgumentException("Catalog path is required.", "catalogPath");
            string fullPath = Path.GetFullPath(catalogPath);
            if (File.Exists(fullPath))
                throw new IOException("The catalog already exists and will not be overwritten: " + fullPath);
            string root = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(root)) Directory.CreateDirectory(root);
            foreach (string folder in new[]
            {
                "Typical Details", "Standards", "Symbols", "Furniture 2D", "Furniture 3D", "Specifications"
            })
            {
                Directory.CreateDirectory(Path.Combine(root ?? string.Empty, folder));
            }

            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "DETAIL-KERB-INLET-001", "Kerb Inlet", "Typical Detail", "Stormwater", "DWG",
                    "Typical Details/Kerb Inlet.dwg", "A", "Draft", string.Empty, string.Empty,
                    "1", "kerb,inlet,stormwater", "Replace with an office-reviewed source asset.",
                    string.Empty, "true"
                },
                new List<string>
                {
                    "STD-TITLEBLOCK-A1-001", "A1 Title Block Standard", "Standard", "Drawing Production", "DWG",
                    "Standards/A1 Title Block.dwg", "A", "Draft", string.Empty, string.Empty,
                    "1", "title block,a1,revision", "Replace with an approved office title-block asset.",
                    string.Empty, "true"
                }
            };
            WriteCatalog(fullPath, rows);
        }

        public static IList<EngineeringAssetRecord> Load(string catalogPath)
        {
            if (string.IsNullOrWhiteSpace(catalogPath))
                throw new ArgumentException("Catalog path is required.", "catalogPath");
            string fullPath = Path.GetFullPath(catalogPath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Asset catalog was not found.", fullPath);
            var records = new List<EngineeringAssetRecord>();
            using (var reader = new StreamReader(fullPath, Encoding.UTF8, true))
            {
                string header = reader.ReadLine();
                if (header == null) throw new InvalidDataException("Asset catalog is empty.");
                Dictionary<string, int> columns = BuildColumnMap(ParseCsvLine(header));
                RequireColumn(columns, "AssetId");
                RequireColumn(columns, "Title");
                RequireColumn(columns, "AssetType");
                RequireColumn(columns, "RelativePath");
                RequireColumn(columns, "Revision");
                RequireColumn(columns, "ApprovalStatus");
                RequireColumn(columns, "UnitsPerMetre");
                RequireColumn(columns, "Sha256");
                RequireColumn(columns, "IsActive");

                string line;
                int lineNumber = 1;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (records.Count >= MaximumAssets)
                        throw new InvalidDataException("The asset catalog exceeds the 10,000-record safety limit.");
                    IList<string> values = ParseCsvLine(line);
                    records.Add(ParseRecord(values, columns, lineNumber));
                }
            }
            return records;
        }

        public static EngineeringAssetAuditResult Audit(string catalogPath)
        {
            IList<EngineeringAssetRecord> records = Load(catalogPath);
            var findings = new List<EngineeringAssetAuditFinding>();

            foreach (IGrouping<string, EngineeringAssetRecord> duplicate in records
                .Where(item => !string.IsNullOrWhiteSpace(item.AssetId))
                .GroupBy(item => item.AssetId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                findings.Add(new EngineeringAssetAuditFinding(
                    EngineeringAssetAuditSeverity.Error,
                    duplicate.Key,
                    "Identity",
                    "Duplicate AssetId occurs " + duplicate.Count().ToString(CultureInfo.InvariantCulture) + " times.",
                    "Assign one stable unique AssetId to each asset revision family."));
            }

            foreach (IGrouping<string, EngineeringAssetRecord> duplicate in records
                .Where(item => !string.IsNullOrWhiteSpace(item.RelativePath))
                .GroupBy(item => NormalisePath(item.ResolvePath(catalogPath)), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                findings.Add(new EngineeringAssetAuditFinding(
                    EngineeringAssetAuditSeverity.Review,
                    string.Join(", ", duplicate.Select(item => item.AssetId)),
                    "Source path",
                    "Multiple catalog records reference the same source file.",
                    "Confirm whether this is intentional revision/category reuse."));
            }

            foreach (EngineeringAssetRecord record in records)
            {
                AuditRecord(catalogPath, record, findings);
            }

            if (findings.Count == 0)
            {
                findings.Add(new EngineeringAssetAuditFinding(
                    EngineeringAssetAuditSeverity.Information,
                    string.Empty,
                    "Catalog",
                    "No catalog integrity problems were detected.",
                    "Continue office and engineering review of the asset content itself."));
            }
            return new EngineeringAssetAuditResult(records, findings);
        }

        public static IList<EngineeringAssetRecord> Search(
            string catalogPath,
            string query,
            string category,
            string discipline,
            IEnumerable<EngineeringAssetApprovalStatus> visibleStatuses,
            bool activeOnly)
        {
            HashSet<EngineeringAssetApprovalStatus> statuses = visibleStatuses == null
                ? new HashSet<EngineeringAssetApprovalStatus>(new[] { EngineeringAssetApprovalStatus.Approved })
                : new HashSet<EngineeringAssetApprovalStatus>(visibleStatuses);
            return Load(catalogPath)
                .Where(item => !activeOnly || item.IsActive)
                .Where(item => statuses.Contains(item.ApprovalStatus))
                .Where(item => string.IsNullOrWhiteSpace(category) ||
                    string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
                .Where(item => string.IsNullOrWhiteSpace(discipline) ||
                    string.Equals(item.Discipline, discipline, StringComparison.OrdinalIgnoreCase))
                .Where(item => item.Matches(query))
                .OrderBy(item => item.Category, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Discipline, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Revision, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static string CalculateSha256(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            using (FileStream stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        public static string SanitizeBlockName(string value)
        {
            string input = string.IsNullOrWhiteSpace(value) ? "CE_ASSET" : value.Trim();
            var builder = new StringBuilder(input.Length);
            foreach (char character in input)
            {
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_')
                    builder.Append(character);
                else
                    builder.Append('_');
            }
            string result = builder.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(result) ? "CE_ASSET" : result;
        }

        private static void AuditRecord(
            string catalogPath,
            EngineeringAssetRecord record,
            ICollection<EngineeringAssetAuditFinding> findings)
        {
            if (string.IsNullOrWhiteSpace(record.AssetId))
                findings.Add(Finding(EngineeringAssetAuditSeverity.Error, record, "Identity", "AssetId is blank.", "Assign a stable unique AssetId."));
            if (string.IsNullOrWhiteSpace(record.Title))
                findings.Add(Finding(EngineeringAssetAuditSeverity.Error, record, "Metadata", "Title is blank.", "Add a user-facing asset title."));
            if (string.IsNullOrWhiteSpace(record.Revision))
                findings.Add(Finding(EngineeringAssetAuditSeverity.Warning, record, "Revision", "Revision is blank.", "Assign the controlled office revision."));
            if (record.UnitsPerMetre <= 0.0 || double.IsNaN(record.UnitsPerMetre) || double.IsInfinity(record.UnitsPerMetre))
                findings.Add(Finding(EngineeringAssetAuditSeverity.Error, record, "Units", "UnitsPerMetre is invalid.", "Enter a positive source-units-per-metre value."));
            if (!SupportedTypes.Contains(record.AssetType ?? string.Empty))
                findings.Add(Finding(EngineeringAssetAuditSeverity.Warning, record, "Format", "Asset type is unsupported: " + record.AssetType, "Use a supported catalog type or add a reviewed importer."));
            if (record.ApprovalStatus == EngineeringAssetApprovalStatus.Superseded && record.IsActive)
                findings.Add(Finding(EngineeringAssetAuditSeverity.Warning, record, "Approval", "Superseded asset is marked active.", "Mark it inactive or replace the active record with the current revision."));
            if (record.ApprovalStatus == EngineeringAssetApprovalStatus.Approved && string.IsNullOrWhiteSpace(record.ApprovedBy))
                findings.Add(Finding(EngineeringAssetAuditSeverity.Warning, record, "Approval", "Approved status has no ApprovedBy value.", "Record the office reviewer/approver reference."));

            string sourcePath = record.ResolvePath(catalogPath);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                findings.Add(Finding(EngineeringAssetAuditSeverity.Error, record, "Source", "Source file is missing: " + sourcePath, "Restore the source file or correct RelativePath."));
                return;
            }

            string actualHash;
            try
            {
                actualHash = CalculateSha256(sourcePath);
            }
            catch (Exception exception)
            {
                findings.Add(Finding(EngineeringAssetAuditSeverity.Error, record, "Checksum", "Could not calculate SHA-256: " + exception.Message, "Resolve file access and rerun the audit."));
                return;
            }

            if (string.IsNullOrWhiteSpace(record.Sha256))
            {
                findings.Add(Finding(
                    record.ApprovalStatus == EngineeringAssetApprovalStatus.Approved
                        ? EngineeringAssetAuditSeverity.Error
                        : EngineeringAssetAuditSeverity.Review,
                    record,
                    "Checksum",
                    "Catalog SHA-256 is blank. Current source SHA-256 is " + actualHash + ".",
                    "Review the source and record its controlled SHA-256 before approval/insertion."));
            }
            else if (!string.Equals(record.Sha256.Trim(), actualHash, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    EngineeringAssetAuditSeverity.Error,
                    record,
                    "Checksum",
                    "Source SHA-256 differs from the catalog value.",
                    "Treat this as a new revision; review it and update the catalog deliberately."));
            }
        }

        private static EngineeringAssetAuditFinding Finding(
            EngineeringAssetAuditSeverity severity,
            EngineeringAssetRecord record,
            string area,
            string finding,
            string action)
        {
            return new EngineeringAssetAuditFinding(severity, record == null ? string.Empty : record.AssetId, area, finding, action);
        }

        private static EngineeringAssetRecord ParseRecord(
            IList<string> values,
            IDictionary<string, int> columns,
            int lineNumber)
        {
            EngineeringAssetApprovalStatus status;
            string statusText = Value(values, columns, "ApprovalStatus");
            if (!Enum.TryParse(statusText.Replace(" ", string.Empty), true, out status))
                throw new InvalidDataException("Invalid ApprovalStatus at line " + lineNumber + ": " + statusText);
            double unitsPerMetre;
            if (!double.TryParse(Value(values, columns, "UnitsPerMetre"), NumberStyles.Float, CultureInfo.InvariantCulture, out unitsPerMetre))
                throw new InvalidDataException("Invalid UnitsPerMetre at line " + lineNumber + ".");
            bool active;
            if (!bool.TryParse(Value(values, columns, "IsActive"), out active))
                throw new InvalidDataException("Invalid IsActive at line " + lineNumber + ".");

            return new EngineeringAssetRecord
            {
                AssetId = Value(values, columns, "AssetId").Trim(),
                Title = Value(values, columns, "Title").Trim(),
                Category = Value(values, columns, "Category").Trim(),
                Discipline = Value(values, columns, "Discipline").Trim(),
                AssetType = Value(values, columns, "AssetType").Trim().ToUpperInvariant(),
                RelativePath = Value(values, columns, "RelativePath").Trim(),
                Revision = Value(values, columns, "Revision").Trim(),
                ApprovalStatus = status,
                ApprovedBy = Value(values, columns, "ApprovedBy").Trim(),
                ApprovalDateUtc = Value(values, columns, "ApprovalDateUtc").Trim(),
                UnitsPerMetre = unitsPerMetre,
                Tags = Value(values, columns, "Tags").Trim(),
                Description = Value(values, columns, "Description").Trim(),
                Sha256 = Value(values, columns, "Sha256").Trim().ToLowerInvariant(),
                IsActive = active
            };
        }

        private static Dictionary<string, int> BuildColumnMap(IList<string> headers)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < headers.Count; index++)
            {
                string header = headers[index].Trim();
                if (!result.ContainsKey(header)) result.Add(header, index);
            }
            return result;
        }

        private static void RequireColumn(IDictionary<string, int> columns, string name)
        {
            if (!columns.ContainsKey(name)) throw new InvalidDataException("Required catalog column is missing: " + name);
        }

        private static string Value(IList<string> values, IDictionary<string, int> columns, string name)
        {
            int index;
            return columns.TryGetValue(name, out index) && index >= 0 && index < values.Count
                ? values[index]
                : string.Empty;
        }

        private static IList<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            for (int index = 0; index < line.Length; index++)
            {
                char character = line[index];
                if (character == '"')
                {
                    if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        current.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else if (character == ',' && !quoted)
                {
                    result.Add(current.ToString());
                    current.Length = 0;
                }
                else
                {
                    current.Append(character);
                }
            }
            if (quoted) throw new InvalidDataException("Unclosed quoted CSV field.");
            result.Add(current.ToString());
            return result;
        }

        private static void WriteCatalog(string path, IEnumerable<IList<string>> rows)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine(Header);
                foreach (IList<string> row in rows)
                    writer.WriteLine(string.Join(",", row.Select(EscapeCsv)));
            }
        }

        private static string EscapeCsv(string value)
        {
            string text = value ?? string.Empty;
            return text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + text.Replace("\"", "\"\"") + "\""
                : text;
        }

        private static string NormalisePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
