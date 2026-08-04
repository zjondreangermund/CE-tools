using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ReleaseInfoCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Reports the exact loaded release and verifies it against the manifest
    /// written by the versioned packaging pipeline.
    /// </summary>
    public sealed class ReleaseInfoCommands
    {
        private const string ReleasesUrl = "https://github.com/zjondreangermund/CE-tools/releases";
        private const string LatestReleaseApi = "https://api.github.com/repos/zjondreangermund/CE-tools/releases/latest";

        [CommandMethod("CE_TOOLS", "CE_ABOUT", CommandFlags.Modal)]
        public void ShowAbout()
        {
            ShowReleaseInformation("CE Tools - About");
        }

        [CommandMethod("CE_TOOLS", "CE_VERSION", CommandFlags.Modal)]
        public void ShowVersion()
        {
            ShowReleaseInformation("CE Tools - Installed Version");
        }

        [CommandMethod("CE_TOOLS", "CE_RELEASEINFO", CommandFlags.Modal)]
        public void ShowReleaseInfo()
        {
            ShowReleaseInformation("CE Tools - Release Information");
        }

        [CommandMethod("CE_TOOLS", "CE_INSTALLVERIFY", CommandFlags.Modal)]
        public void VerifyInstallation()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ReleaseSnapshot snapshot = ReleaseSnapshot.Read();
            IList<KeyValuePair<string, string>> rows = snapshot.BuildVerificationRows();
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Installation Verification",
                snapshot.VerificationPassed
                    ? "PASS: the loaded Civil 3D assembly and packaged files match the installed release manifest."
                    : "REVIEW: one or more packaged files could not be verified. Rebuild and reinstall the versioned bundle.",
                rows,
                "CE TOOLS INSTALLATION VERIFICATION");
        }

        [CommandMethod("CE_TOOLS", "CE_UPDATECHECK", CommandFlags.Modal)]
        public void CheckForUpdates()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            string latest = "Unavailable";
            string page = ReleasesUrl;
            string status;
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                var request = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
                request.UserAgent = "CE-Tools-Civil3D-2023";
                request.Accept = "application/vnd.github+json";
                request.Timeout = 6000;
                request.ReadWriteTimeout = 6000;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string json = reader.ReadToEnd();
                    latest = JsonValue(json, "tag_name", "Unknown");
                    page = JsonValue(json, "html_url", ReleasesUrl);
                }

                Version installedVersion = Assembly.GetExecutingAssembly().GetName().Version;
                Version releaseVersion;
                status = TryParseReleaseVersion(latest, out releaseVersion) && installedVersion != null
                    ? (releaseVersion.CompareTo(installedVersion) > 0
                        ? "A newer packaged release is available."
                        : "The installed assembly is current relative to the latest packaged release.")
                    : "Latest release information was retrieved; compare the release tag with the installed version.";
            }
            catch (System.Exception exception)
            {
                status = "The update service could not be reached: " + exception.Message;
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Installed version", ReleaseSnapshot.AssemblyVersion()),
                Pair("Latest release", latest),
                Pair("Status", status),
                Pair("Release page", page),
                Pair("Installation policy", "Download only from the CE Tools GitHub release and use the verified installer.")
            };
            bool open = PopupTablePresenter.ShowReview(
                "CE Tools - Update Check",
                "CE Tools will not silently replace a DLL loaded by Civil 3D. Review the release and run its verified installer after closing Civil 3D.",
                rows,
                "Open Releases");
            if (!open) return;

            try
            {
                Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCould not open the CE Tools release page. {0}", exception.Message);
            }
        }

        private static void ShowReleaseInformation(string title)
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ReleaseSnapshot snapshot = ReleaseSnapshot.Read();
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                title,
                "Exact loaded assembly, source commit and bundle identity. Use CE_INSTALLVERIFY for SHA-256 verification.",
                snapshot.BuildInformationRows(),
                "CE TOOLS RELEASE INFORMATION");
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value ?? string.Empty);
        }

        private static string JsonValue(string json, string name, string fallback)
        {
            Match match = Regex.Match(
                json ?? string.Empty,
                "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"",
                RegexOptions.IgnoreCase);
            return match.Success ? Regex.Unescape(match.Groups["value"].Value) : fallback;
        }

        private static bool TryParseReleaseVersion(string value, out Version version)
        {
            string normalized = Regex.Replace(value ?? string.Empty, "^[vV]", string.Empty);
            if (normalized.Count(character => character == '.') == 1) normalized += ".0";
            return Version.TryParse(normalized, out version);
        }

        private sealed class ReleaseSnapshot
        {
            private readonly List<ManifestFile> _files = new List<ManifestFile>();

            private ReleaseSnapshot()
            {
            }

            public string BundleRoot { get; private set; }
            public string ManifestPath { get; private set; }
            public string Version { get; private set; }
            public string SourceCommit { get; private set; }
            public string CreatedUtc { get; private set; }
            public bool ManifestFound { get; private set; }
            public bool VerificationPassed { get; private set; }

            public static ReleaseSnapshot Read()
            {
                var snapshot = new ReleaseSnapshot
                {
                    BundleRoot = FindBundleRoot(),
                    Version = AssemblyVersion(),
                    SourceCommit = "UNKNOWN",
                    CreatedUtc = "UNKNOWN"
                };
                snapshot.ManifestPath = Path.Combine(
                    snapshot.BundleRoot,
                    "Contents",
                    "Resources",
                    "release-manifest.json");
                if (!File.Exists(snapshot.ManifestPath))
                {
                    snapshot.VerificationPassed = false;
                    return snapshot;
                }

                try
                {
                    string json = File.ReadAllText(snapshot.ManifestPath);
                    snapshot.ManifestFound = true;
                    snapshot.Version = JsonValue(json, "Version", snapshot.Version);
                    snapshot.SourceCommit = JsonValue(json, "SourceCommit", "UNKNOWN");
                    snapshot.CreatedUtc = JsonValue(json, "CreatedUtc", "UNKNOWN");

                    MatchCollection matches = Regex.Matches(
                        json,
                        "\\{\\s*\\\"Path\\\"\\s*:\\s*\\\"(?<path>(?:\\\\.|[^\\\"])*)\\\"\\s*,\\s*\\\"SHA256\\\"\\s*:\\s*\\\"(?<hash>[A-Fa-f0-9]{64})\\\"\\s*\\}",
                        RegexOptions.IgnoreCase);
                    foreach (Match match in matches)
                        snapshot._files.Add(new ManifestFile(
                            Regex.Unescape(match.Groups["path"].Value),
                            match.Groups["hash"].Value.ToUpperInvariant()));

                    snapshot.VerificationPassed = snapshot._files.Count > 0;
                    foreach (ManifestFile file in snapshot._files)
                    {
                        string relative = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                        string fullPath = Path.GetFullPath(Path.Combine(snapshot.BundleRoot, relative));
                        string rootPrefix = snapshot.BundleRoot.TrimEnd(Path.DirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
                        if (Path.IsPathRooted(relative) ||
                            relative.Split(Path.DirectorySeparatorChar).Contains("..") ||
                            !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            file.ActualHash = "UNSAFE PATH";
                            file.Passed = false;
                            snapshot.VerificationPassed = false;
                            continue;
                        }
                        file.Exists = File.Exists(fullPath);
                        file.ActualHash = file.Exists ? Sha256(fullPath) : "MISSING";
                        file.Passed = file.Exists && string.Equals(
                            file.ExpectedHash,
                            file.ActualHash,
                            StringComparison.OrdinalIgnoreCase);
                        snapshot.VerificationPassed &= file.Passed;
                    }
                }
                catch
                {
                    snapshot.ManifestFound = false;
                    snapshot.VerificationPassed = false;
                }
                return snapshot;
            }

            public IList<KeyValuePair<string, string>> BuildInformationRows()
            {
                return new List<KeyValuePair<string, string>>
                {
                    Pair("Product", "CE Tools for Civil 3D 2023"),
                    Pair("Assembly version", AssemblyVersion()),
                    Pair("Package version", Version),
                    Pair("Source commit", SourceCommit),
                    Pair("Release created", CreatedUtc),
                    Pair("Loaded assembly", Assembly.GetExecutingAssembly().Location),
                    Pair("Bundle root", BundleRoot),
                    Pair("Release manifest", ManifestFound ? ManifestPath : "Not installed"),
                    Pair("Command surface", FloatingToolsCommands.ReadCurrentRibbonTools().Count + " unique loaded commands"),
                    Pair("Host", "Autodesk Civil 3D 2023 / .NET Framework 4.8")
                };
            }

            public IList<KeyValuePair<string, string>> BuildVerificationRows()
            {
                var rows = new List<KeyValuePair<string, string>>
                {
                    Pair("Overall", VerificationPassed ? "PASS" : "REVIEW"),
                    Pair("Manifest", ManifestFound ? ManifestPath : "Missing release-manifest.json"),
                    Pair("Source commit", SourceCommit)
                };
                foreach (ManifestFile file in _files)
                    rows.Add(Pair(
                        file.RelativePath,
                        file.Passed ? "PASS - " + file.ActualHash :
                        "FAIL - expected " + file.ExpectedHash + "; actual " + file.ActualHash));
                return rows;
            }

            public static string AssemblyVersion()
            {
                System.Version version = Assembly.GetExecutingAssembly().GetName().Version;
                return version == null ? "UNKNOWN" : version.ToString();
            }

            private static string FindBundleRoot()
            {
                var current = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
                while (current != null)
                {
                    if (current.Name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                        return current.FullName;
                    current = current.Parent;
                }
                return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }

            private static string Sha256(string path)
            {
                using (var algorithm = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                    return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private sealed class ManifestFile
        {
            public ManifestFile(string relativePath, string expectedHash)
            {
                RelativePath = relativePath;
                ExpectedHash = expectedHash;
                ActualHash = "NOT CHECKED";
            }

            public string RelativePath { get; private set; }
            public string ExpectedHash { get; private set; }
            public string ActualHash { get; set; }
            public bool Exists { get; set; }
            public bool Passed { get; set; }
        }
    }
}
