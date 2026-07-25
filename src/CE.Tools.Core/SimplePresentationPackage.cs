using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace CETools.Core
{
    /// <summary>
    /// Dependency-free Open XML presentation writer for concise project-review decks.
    /// It intentionally supports a small, deterministic slide model rather than the
    /// complete PowerPoint feature set.
    /// </summary>
    public static class SimplePresentationPackage
    {
        private const long SlideWidth = 12192000;
        private const long SlideHeight = 6858000;

        public static void Write(string path, PresentationDeck deck)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Presentation path is required.", nameof(path));
            if (deck == null) throw new ArgumentNullException(nameof(deck));
            deck.Validate();
            if (File.Exists(path)) throw new IOException("The presentation output file already exists.");

            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, false))
                {
                    Add(archive, "[Content_Types].xml", ContentTypes(deck.Slides.Count));
                    Add(archive, "_rels/.rels", RootRelationships());
                    Add(archive, "docProps/core.xml", CoreProperties(deck));
                    Add(archive, "docProps/app.xml", AppProperties(deck));
                    Add(archive, "ppt/presentation.xml", PresentationXml(deck.Slides.Count));
                    Add(archive, "ppt/_rels/presentation.xml.rels", PresentationRelationships(deck.Slides.Count));
                    Add(archive, "ppt/presProps.xml", PresentationProperties());
                    Add(archive, "ppt/viewProps.xml", ViewProperties());
                    Add(archive, "ppt/tableStyles.xml", TableStyles());
                    Add(archive, "ppt/theme/theme1.xml", Theme());
                    Add(archive, "ppt/slideMasters/slideMaster1.xml", SlideMaster());
                    Add(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels", SlideMasterRelationships());
                    Add(archive, "ppt/slideLayouts/slideLayout1.xml", SlideLayout());
                    Add(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", SlideLayoutRelationships());

                    for (int index = 0; index < deck.Slides.Count; index++)
                    {
                        Add(
                            archive,
                            "ppt/slides/slide" + (index + 1).ToString(CultureInfo.InvariantCulture) + ".xml",
                            SlideXml(deck, deck.Slides[index], index));
                        Add(
                            archive,
                            "ppt/slides/_rels/slide" + (index + 1).ToString(CultureInfo.InvariantCulture) + ".xml.rels",
                            SlideRelationships());
                    }
                }

                File.Move(temporary, path);
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
                throw;
            }
        }

        private static void Add(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(content);
        }

        private static string ContentTypes(int slideCount)
        {
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            xml.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            xml.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            xml.Append("<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>");
            xml.Append("<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>");
            xml.Append("<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>");
            xml.Append("<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
            xml.Append("<Override PartName=\"/ppt/presProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presProps+xml\"/>");
            xml.Append("<Override PartName=\"/ppt/viewProps.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.viewProps+xml\"/>");
            xml.Append("<Override PartName=\"/ppt/tableStyles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml\"/>");
            xml.Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
            xml.Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
            for (int index = 1; index <= slideCount; index++)
            {
                xml.Append("<Override PartName=\"/ppt/slides/slide").Append(index)
                    .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
            }
            xml.Append("</Types>");
            return xml.ToString();
        }

        private static string RootRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"ppt/presentation.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
                "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
                "</Relationships>";
        }

        private static string CoreProperties(PresentationDeck deck)
        {
            string created = deck.CreatedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
                "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
                "xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                "<dc:title>" + Xml(deck.Title) + "</dc:title>" +
                "<dc:subject>" + Xml(deck.Subject) + "</dc:subject>" +
                "<dc:creator>" + Xml(deck.Author) + "</dc:creator>" +
                "<cp:lastModifiedBy>" + Xml(deck.Author) + "</cp:lastModifiedBy>" +
                "<dcterms:created xsi:type=\"dcterms:W3CDTF\">" + created + "</dcterms:created>" +
                "<dcterms:modified xsi:type=\"dcterms:W3CDTF\">" + created + "</dcterms:modified>" +
                "</cp:coreProperties>";
        }

        private static string AppProperties(PresentationDeck deck)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" " +
                "xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
                "<Application>CE Tools</Application><PresentationFormat>On-screen Show (16:9)</PresentationFormat>" +
                "<Slides>" + deck.Slides.Count.ToString(CultureInfo.InvariantCulture) + "</Slides>" +
                "<Notes>0</Notes><HiddenSlides>0</HiddenSlides><MMClips>0</MMClips><ScaleCrop>false</ScaleCrop>" +
                "<Company>" + Xml(deck.Company) + "</Company><LinksUpToDate>false</LinksUpToDate>" +
                "<SharedDoc>false</SharedDoc><HyperlinksChanged>false</HyperlinksChanged><AppVersion>1.0</AppVersion>" +
                "</Properties>";
        }

        private static string PresentationXml(int slideCount)
        {
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<p:presentation xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" ");
            xml.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" ");
            xml.Append("xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">");
            xml.Append("<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>");
            xml.Append("<p:sldIdLst>");
            for (int index = 0; index < slideCount; index++)
            {
                xml.Append("<p:sldId id=\"").Append(256 + index).Append("\" r:id=\"rId")
                    .Append(6 + index).Append("\"/>");
            }
            xml.Append("</p:sldIdLst>");
            xml.Append("<p:sldSz cx=\"").Append(SlideWidth).Append("\" cy=\"").Append(SlideHeight)
                .Append("\" type=\"screen16x9\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>");
            xml.Append("<p:defaultTextStyle><a:defPPr><a:defRPr lang=\"en-ZA\"/></a:defPPr></p:defaultTextStyle>");
            xml.Append("</p:presentation>");
            return xml.ToString();
        }

        private static string PresentationRelationships(int slideCount)
        {
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            xml.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>");
            xml.Append("<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/presProps\" Target=\"presProps.xml\"/>");
            xml.Append("<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/viewProps\" Target=\"viewProps.xml\"/>");
            xml.Append("<Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"theme/theme1.xml\"/>");
            xml.Append("<Relationship Id=\"rId5\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/tableStyles\" Target=\"tableStyles.xml\"/>");
            for (int index = 0; index < slideCount; index++)
            {
                xml.Append("<Relationship Id=\"rId").Append(6 + index)
                    .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide")
                    .Append(index + 1).Append(".xml\"/>");
            }
            xml.Append("</Relationships>");
            return xml.ToString();
        }

        private static string SlideXml(PresentationDeck deck, PresentationSlide slide, int slideIndex)
        {
            var shapes = new StringBuilder();
            int id = 2;
            shapes.Append(Shape(id++, "Accent", 0, 0, 180000, SlideHeight, string.Empty, 1, "1F4E78", "1F4E78", false, false));
            shapes.Append(TextShape(id++, "Slide title", 500000, 300000, 11100000, 700000, slide.Title, 3000, "1F4E78", true, false));
            if (!string.IsNullOrWhiteSpace(slide.Subtitle))
                shapes.Append(TextShape(id++, "Subtitle", 520000, 1030000, 10950000, 420000, slide.Subtitle, 1300, "5B6573", false, false));

            long top = string.IsNullOrWhiteSpace(slide.Subtitle) ? 1200000 : 1550000;
            if (slide.Metrics.Count > 0)
            {
                int count = Math.Min(4, slide.Metrics.Count);
                long gap = 180000;
                long cardWidth = (11100000 - gap * (count - 1)) / count;
                for (int index = 0; index < count; index++)
                {
                    long x = 500000 + index * (cardWidth + gap);
                    PresentationMetric metric = slide.Metrics[index];
                    shapes.Append(Shape(id++, "Metric card", x, top, cardWidth, 950000, string.Empty, 1, "EAF1F7", "B8C9D9", true, false));
                    shapes.Append(TextShape(id++, "Metric value", x + 100000, top + 90000, cardWidth - 200000, 420000, metric.Value, 2200, "1F4E78", true, true));
                    shapes.Append(TextShape(id++, "Metric label", x + 100000, top + 510000, cardWidth - 200000, 300000, metric.Label, 1050, "5B6573", false, true));
                }
                top += 1200000;
            }

            List<string> bullets = slide.Bullets.Take(10).ToList();
            long availableHeight = SlideHeight - top - 700000;
            long bulletHeight = bullets.Count == 0 ? 0 : Math.Max(330000, Math.Min(540000, availableHeight / bullets.Count));
            for (int index = 0; index < bullets.Count; index++)
            {
                shapes.Append(TextShape(
                    id++,
                    "Bullet " + (index + 1).ToString(CultureInfo.InvariantCulture),
                    700000,
                    top + index * bulletHeight,
                    10800000,
                    bulletHeight,
                    "•  " + bullets[index],
                    1450,
                    "263238",
                    false,
                    false));
            }

            shapes.Append(TextShape(id++, "Footer", 500000, 6380000, 11100000, 260000,
                deck.Company + "  |  " + (slideIndex + 1).ToString(CultureInfo.InvariantCulture) + " / " + deck.Slides.Count.ToString(CultureInfo.InvariantCulture),
                850, "7A8793", false, false));

            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                "<p:cSld><p:spTree>" + GroupShapeRoot() + shapes + "</p:spTree></p:cSld>" +
                "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
        }

        private static string GroupShapeRoot()
        {
            return "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/>" +
                "<a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";
        }

        private static string TextShape(
            int id,
            string name,
            long x,
            long y,
            long width,
            long height,
            string text,
            int fontSizeHundredths,
            string colour,
            bool bold,
            bool centred)
        {
            return Shape(id, name, x, y, width, height, text, fontSizeHundredths, null, colour, false, centred, bold);
        }

        private static string Shape(
            int id,
            string name,
            long x,
            long y,
            long width,
            long height,
            string text,
            int fontSizeHundredths,
            string fill,
            string line,
            bool rounded,
            bool centred,
            bool bold = false)
        {
            var xml = new StringBuilder();
            xml.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id).Append("\" name=\"").Append(Xml(name)).Append("\"/>");
            xml.Append("<p:cNvSpPr txBox=\"").Append(string.IsNullOrEmpty(text) ? "0" : "1").Append("\"/><p:nvPr/></p:nvSpPr>");
            xml.Append("<p:spPr><a:xfrm><a:off x=\"").Append(x).Append("\" y=\"").Append(y)
                .Append("\"/><a:ext cx=\"").Append(width).Append("\" cy=\"").Append(height).Append("\"/></a:xfrm>");
            xml.Append("<a:prstGeom prst=\"").Append(rounded ? "roundRect" : "rect").Append("\"><a:avLst/></a:prstGeom>");
            if (string.IsNullOrWhiteSpace(fill)) xml.Append("<a:noFill/>");
            else xml.Append("<a:solidFill><a:srgbClr val=\"").Append(fill).Append("\"/></a:solidFill>");
            if (string.IsNullOrWhiteSpace(line)) xml.Append("<a:ln><a:noFill/></a:ln>");
            else xml.Append("<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"").Append(line).Append("\"/></a:solidFill></a:ln>");
            xml.Append("</p:spPr>");
            if (!string.IsNullOrEmpty(text))
            {
                xml.Append("<p:txBody><a:bodyPr wrap=\"square\" lIns=\"50000\" rIns=\"50000\" tIns=\"30000\" bIns=\"30000\"/>");
                xml.Append("<a:lstStyle/><a:p><a:pPr algn=\"").Append(centred ? "ctr" : "l").Append("\"/>");
                xml.Append("<a:r><a:rPr lang=\"en-ZA\" sz=\"").Append(fontSizeHundredths).Append("\" b=\"")
                    .Append(bold ? "1" : "0").Append("\"><a:solidFill><a:srgbClr val=\"").Append(line ?? "263238")
                    .Append("\"/></a:solidFill><a:latin typeface=\"Aptos\"/></a:rPr><a:t>").Append(Xml(text)).Append("</a:t></a:r>");
                xml.Append("<a:endParaRPr lang=\"en-ZA\" sz=\"").Append(fontSizeHundredths).Append("\"/></a:p></p:txBody>");
            }
            xml.Append("</p:sp>");
            return xml.ToString();
        }

        private static string SlideRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                "</Relationships>";
        }

        private static string SlideMaster()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:sldMaster xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                "<p:cSld><p:spTree>" + GroupShapeRoot() + "</p:spTree></p:cSld>" +
                "<p:clrMap accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" bg1=\"lt1\" bg2=\"lt2\" folHlink=\"folHlink\" hlink=\"hlink\" tx1=\"dk1\" tx2=\"dk2\"/>" +
                "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>" +
                "<p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\"><a:defRPr sz=\"3000\"/></a:lvl1pPr></p:titleStyle>" +
                "<p:bodyStyle><a:lvl1pPr marL=\"342900\" indent=\"-285750\"><a:defRPr sz=\"1800\"/></a:lvl1pPr></p:bodyStyle>" +
                "<p:otherStyle><a:defPPr><a:defRPr lang=\"en-ZA\"/></a:defPPr></p:otherStyle></p:txStyles>" +
                "</p:sldMaster>";
        }

        private static string SlideMasterRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme1.xml\"/>" +
                "</Relationships>";
        }

        private static string SlideLayout()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:sldLayout xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" type=\"blank\" preserve=\"1\">" +
                "<p:cSld name=\"Blank\"><p:spTree>" + GroupShapeRoot() + "</p:spTree></p:cSld>" +
                "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";
        }

        private static string SlideLayoutRelationships()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/>" +
                "</Relationships>";
        }

        private static string PresentationProperties()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:presentationPr xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:showPr useTimings=\"0\"/></p:presentationPr>";
        }

        private static string ViewProperties()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:viewPr xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" lastView=\"sldView\"><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr><p:slideViewPr><p:cSldViewPr><p:cViewPr varScale=\"1\"><p:scale><a:sx n=\"100\" d=\"100\"/><a:sy n=\"100\" d=\"100\"/></p:scale><p:origin x=\"0\" y=\"0\"/></p:cViewPr><p:guideLst/></p:cSldViewPr></p:slideViewPr><p:notesTextViewPr><p:cViewPr><p:scale><a:sx n=\"100\" d=\"100\"/><a:sy n=\"100\" d=\"100\"/></p:scale><p:origin x=\"0\" y=\"0\"/></p:cViewPr></p:notesTextViewPr><p:gridSpacing cx=\"78028800\" cy=\"78028800\"/></p:viewPr>";
        }

        private static string TableStyles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"/>";
        }

        private static string Theme()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"CE Tools\"><a:themeElements>" +
                "<a:clrScheme name=\"CE Tools\"><a:dk1><a:srgbClr val=\"1B1F23\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1>" +
                "<a:dk2><a:srgbClr val=\"263238\"/></a:dk2><a:lt2><a:srgbClr val=\"EAF1F7\"/></a:lt2>" +
                "<a:accent1><a:srgbClr val=\"1F4E78\"/></a:accent1><a:accent2><a:srgbClr val=\"2E75B6\"/></a:accent2>" +
                "<a:accent3><a:srgbClr val=\"70AD47\"/></a:accent3><a:accent4><a:srgbClr val=\"ED7D31\"/></a:accent4>" +
                "<a:accent5><a:srgbClr val=\"A5A5A5\"/></a:accent5><a:accent6><a:srgbClr val=\"FFC000\"/></a:accent6>" +
                "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
                "<a:fontScheme name=\"CE Tools\"><a:majorFont><a:latin typeface=\"Aptos Display\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
                "<a:minorFont><a:latin typeface=\"Aptos\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
                "<a:fmtScheme name=\"CE Tools\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>" +
                "<a:gradFill rotWithShape=\"1\"><a:gsLst><a:gs pos=\"0\"><a:schemeClr val=\"phClr\"><a:tint val=\"50000\"/><a:satMod val=\"300000\"/></a:schemeClr></a:gs><a:gs pos=\"100000\"><a:schemeClr val=\"phClr\"><a:shade val=\"100000\"/><a:satMod val=\"200000\"/></a:schemeClr></a:gs></a:gsLst><a:lin ang=\"16200000\" scaled=\"1\"/></a:gradFill>" +
                "<a:solidFill><a:schemeClr val=\"phClr\"><a:tint val=\"50000\"/><a:satMod val=\"150000\"/></a:schemeClr></a:solidFill></a:fillStyleLst>" +
                "<a:lnStyleLst><a:ln w=\"6350\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>" +
                "<a:ln w=\"12700\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>" +
                "<a:ln w=\"19050\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln></a:lnStyleLst>" +
                "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
                "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"><a:tint val=\"95000\"/><a:satMod val=\"170000\"/></a:schemeClr></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"><a:tint val=\"85000\"/><a:satMod val=\"170000\"/></a:schemeClr></a:solidFill></a:bgFillStyleLst>" +
                "</a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
        }

        private static string Xml(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }

    public sealed class PresentationDeck
    {
        public PresentationDeck(string title, string subject, string author, string company, DateTime createdUtc, IEnumerable<PresentationSlide> slides)
        {
            Title = title ?? string.Empty;
            Subject = subject ?? string.Empty;
            Author = author ?? string.Empty;
            Company = company ?? string.Empty;
            CreatedUtc = createdUtc;
            Slides = (slides ?? Enumerable.Empty<PresentationSlide>()).ToList();
        }
        public string Title { get; private set; }
        public string Subject { get; private set; }
        public string Author { get; private set; }
        public string Company { get; private set; }
        public DateTime CreatedUtc { get; private set; }
        public IReadOnlyList<PresentationSlide> Slides { get; private set; }
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Title)) throw new ArgumentException("Presentation title is required.");
            if (Slides.Count == 0) throw new ArgumentException("At least one presentation slide is required.");
            if (Slides.Count > 100) throw new ArgumentOutOfRangeException(nameof(Slides), "Presentation slide count exceeds the 100-slide limit.");
            for (int index = 0; index < Slides.Count; index++) Slides[index].Validate(index);
        }
    }

    public sealed class PresentationSlide
    {
        public PresentationSlide(string title, string subtitle, IEnumerable<string> bullets, IEnumerable<PresentationMetric> metrics)
        {
            Title = title ?? string.Empty;
            Subtitle = subtitle ?? string.Empty;
            Bullets = (bullets ?? Enumerable.Empty<string>()).Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
            Metrics = (metrics ?? Enumerable.Empty<PresentationMetric>()).Where(item => item != null).ToList();
        }
        public string Title { get; private set; }
        public string Subtitle { get; private set; }
        public IReadOnlyList<string> Bullets { get; private set; }
        public IReadOnlyList<PresentationMetric> Metrics { get; private set; }
        public void Validate(int index)
        {
            if (string.IsNullOrWhiteSpace(Title)) throw new ArgumentException("Slide title is missing at index " + index.ToString(CultureInfo.InvariantCulture));
            if (Bullets.Count > 20) throw new ArgumentOutOfRangeException(nameof(Bullets), "Slide bullet count exceeds 20 at index " + index.ToString(CultureInfo.InvariantCulture));
            if (Metrics.Count > 8) throw new ArgumentOutOfRangeException(nameof(Metrics), "Slide metric count exceeds 8 at index " + index.ToString(CultureInfo.InvariantCulture));
        }
    }

    public sealed class PresentationMetric
    {
        public PresentationMetric(string label, string value)
        {
            Label = label ?? string.Empty;
            Value = value ?? string.Empty;
        }
        public string Label { get; private set; }
        public string Value { get; private set; }
    }
}