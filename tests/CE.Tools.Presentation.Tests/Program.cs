using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using CETools.Core;

namespace CETools.Presentation.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            string folder = Path.Combine(Path.GetTempPath(), "ce-tools-pptx-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "project-review.pptx");
            try
            {
                Directory.CreateDirectory(folder);
                PresentationDeck deck = BuildDeck();
                SimplePresentationPackage.Write(path, deck);
                Exists(path, "presentation output");
                ValidatePackage(path, deck.Slides.Count);
                ExistingOutputIsProtected(path, deck);
                InvalidDeckIsRejected(folder);
                Console.WriteLine("CE Tools presentation package tests passed: 4");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("CE Tools presentation package test failure:");
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                catch { }
            }
        }

        private static PresentationDeck BuildDeck()
        {
            return new PresentationDeck(
                "Test Project Review",
                "Civil engineering design review",
                "CE Tools Tests",
                "CE Tools",
                new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc),
                new[]
                {
                    new PresentationSlide(
                        "Project Overview",
                        "Automated drawing snapshot",
                        new[] { "Coordinate system verified", "Three design disciplines detected" },
                        new[]
                        {
                            new PresentationMetric("Alignments", "4"),
                            new PresentationMetric("Surfaces", "2")
                        }),
                    new PresentationSlide(
                        "Model Health",
                        "Automated checks require professional review",
                        new[] { "No unresolved XREFs", "One layout requires a viewport" },
                        new PresentationMetric[0])
                });
        }

        private static void ValidatePackage(string path, int slideCount)
        {
            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                string[] required =
                {
                    "[Content_Types].xml",
                    "_rels/.rels",
                    "docProps/core.xml",
                    "docProps/app.xml",
                    "ppt/presentation.xml",
                    "ppt/_rels/presentation.xml.rels",
                    "ppt/slideMasters/slideMaster1.xml",
                    "ppt/slideLayouts/slideLayout1.xml",
                    "ppt/theme/theme1.xml"
                };
                foreach (string name in required)
                    True(archive.GetEntry(name) != null, "Missing package part: " + name);

                for (int index = 1; index <= slideCount; index++)
                {
                    string slide = "ppt/slides/slide" + index + ".xml";
                    string rels = "ppt/slides/_rels/slide" + index + ".xml.rels";
                    True(archive.GetEntry(slide) != null, "Missing slide: " + slide);
                    True(archive.GetEntry(rels) != null, "Missing slide relationships: " + rels);
                    XDocument slideXml = ReadXml(archive, slide);
                    True(slideXml.Descendants().Any(element => element.Name.LocalName == "t"),
                        "Slide contains no text runs: " + slide);
                }

                XDocument presentation = ReadXml(archive, "ppt/presentation.xml");
                Equal(slideCount,
                    presentation.Descendants().Count(element => element.Name.LocalName == "sldId"),
                    "presentation slide count");
                string allSlideText = string.Join(" ", Enumerable.Range(1, slideCount)
                    .Select(index => ReadText(archive, "ppt/slides/slide" + index + ".xml")));
                True(allSlideText.Contains("Project Overview"), "Project Overview title missing");
                True(allSlideText.Contains("Model Health"), "Model Health title missing");
                True(allSlideText.Contains("Alignments"), "Metric label missing");
            }
        }

        private static void ExistingOutputIsProtected(string path, PresentationDeck deck)
        {
            bool thrown = false;
            try { SimplePresentationPackage.Write(path, deck); }
            catch (IOException) { thrown = true; }
            True(thrown, "Existing presentation was not protected from overwrite.");
        }

        private static void InvalidDeckIsRejected(string folder)
        {
            bool thrown = false;
            try
            {
                SimplePresentationPackage.Write(
                    Path.Combine(folder, "invalid.pptx"),
                    new PresentationDeck("", "", "", "", DateTime.UtcNow, new PresentationSlide[0]));
            }
            catch (ArgumentException) { thrown = true; }
            True(thrown, "Invalid presentation deck was not rejected.");
        }

        private static XDocument ReadXml(ZipArchive archive, string name)
        {
            ZipArchiveEntry entry = archive.GetEntry(name);
            if (entry == null) throw new InvalidOperationException("Missing XML part: " + name);
            using (Stream stream = entry.Open()) return XDocument.Load(stream);
        }

        private static string ReadText(ZipArchive archive, string name)
        {
            return string.Join(" ", ReadXml(archive, name)
                .Descendants()
                .Where(element => element.Name.LocalName == "t")
                .Select(element => element.Value));
        }

        private static void Exists(string path, string label)
        {
            True(File.Exists(path), label + " does not exist.");
        }

        private static void Equal(int expected, int actual, string label)
        {
            if (expected != actual)
                throw new InvalidOperationException(label + ": expected " + expected + ", received " + actual + ".");
        }

        private static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
