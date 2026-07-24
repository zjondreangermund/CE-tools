using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates lightweight vector-style ribbon icons at runtime. Keeping the artwork
    /// inside the managed assembly avoids missing PNG resources after bundle updates
    /// and gives CE Tools a consistent blue/green engineering identity in both dark
    /// and light AutoCAD themes.
    /// </summary>
    internal static class RibbonVisuals
    {
        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource Small(string id)
        {
            return Create(id, 16);
        }

        public static ImageSource Large(string id)
        {
            return Create(id, 32);
        }

        private static ImageSource Create(string id, int pixels)
        {
            string key = (id ?? string.Empty) + "|" + pixels;
            ImageSource cached;
            if (Cache.TryGetValue(key, out cached)) return cached;

            IconStyle style = ResolveStyle(id);
            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                double inset = Math.Max(1.0, pixels * 0.055);
                double radius = Math.Max(2.0, pixels * 0.18);
                var background = new SolidColorBrush(style.Background);
                var accent = new SolidColorBrush(style.Accent);
                var border = new Pen(new SolidColorBrush(style.Border), Math.Max(1.0, pixels * 0.055));
                background.Freeze();
                accent.Freeze();
                border.Freeze();

                context.DrawRoundedRectangle(
                    background,
                    border,
                    new Rect(inset, inset, pixels - inset * 2.0, pixels - inset * 2.0),
                    radius,
                    radius);

                context.DrawRoundedRectangle(
                    accent,
                    null,
                    new Rect(
                        pixels * 0.17,
                        pixels * 0.73,
                        pixels * 0.66,
                        Math.Max(1.5, pixels * 0.09)),
                    pixels * 0.04,
                    pixels * 0.04);

                double fontSize = style.Glyph.Length > 1
                    ? pixels * 0.34
                    : pixels * 0.48;
                var text = new FormattedText(
                    style.Glyph,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    fontSize,
                    Brushes.White);
                Point origin = new Point(
                    (pixels - text.Width) / 2.0,
                    pixels * 0.10 + (pixels * 0.52 - text.Height) / 2.0);
                context.DrawText(text, origin);

                var nodePen = new Pen(accent, Math.Max(1.0, pixels * 0.05));
                nodePen.Freeze();
                double y = pixels * 0.66;
                context.DrawLine(nodePen, new Point(pixels * 0.24, y), new Point(pixels * 0.76, y));
                context.DrawEllipse(accent, null, new Point(pixels * 0.24, y), pixels * 0.045, pixels * 0.045);
                context.DrawEllipse(accent, null, new Point(pixels * 0.50, y), pixels * 0.045, pixels * 0.045);
                context.DrawEllipse(accent, null, new Point(pixels * 0.76, y), pixels * 0.045, pixels * 0.045);
            }

            var bitmap = new RenderTargetBitmap(
                pixels,
                pixels,
                96.0,
                96.0,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            Cache[key] = bitmap;
            return bitmap;
        }

        private static IconStyle ResolveStyle(string id)
        {
            string value = (id ?? string.Empty).ToUpperInvariant();
            if (value.Contains("PROJECT")) return Style("P", 18, 69, 122, 113, 190, 68);
            if (value.Contains("COORDSYS")) return Style("CS", 20, 91, 146, 68, 192, 208);
            if (value.Contains("SURVEY")) return Style("XY", 21, 96, 143, 72, 198, 219);
            if (value.Contains("DRAWING_MENU")) return Style("DR", 50, 77, 99, 98, 172, 199);
            if (value.Contains("CLEANUP")) return Style("CL", 75, 85, 99, 244, 180, 0);
            if (value.Contains("HATCH")) return Style("H", 67, 79, 95, 111, 190, 108);
            if (value.Contains("ROADS")) return Style("RD", 79, 92, 107, 255, 176, 32);
            if (value.Contains("FEATURE")) return Style("FL", 38, 97, 87, 123, 201, 111);
            if (value.Contains("ALIGNMENT")) return Style("AL", 45, 89, 122, 81, 182, 220);
            if (value.Contains("PROFILE")) return Style("PR", 72, 84, 114, 255, 193, 7);
            if (value.Contains("SURFACE")) return Style("SF", 62, 91, 76, 124, 179, 66);
            if (value.Contains("CORRIDOR")) return Style("CO", 45, 87, 102, 47, 181, 170);
            if (value.Contains("PARKING")) return Style("PK", 57, 91, 76, 139, 195, 74);
            if (value.Contains("PIPE")) return Style("UT", 38, 85, 112, 75, 179, 208);
            if (value.Contains("DESIGN_STANDARDS")) return Style("DS", 91, 82, 63, 223, 184, 63);
            if (value.Contains("QUANTITY")) return Style("Q", 43, 87, 105, 114, 190, 82);
            if (value.Contains("REPORT")) return Style("R", 62, 81, 111, 235, 159, 52);
            if (value.Contains("DYNAMIC_SECTION")) return Style("XS", 36, 87, 109, 46, 190, 190);
            if (value.Contains("CLIENT_BOOK")) return Style("BK", 18, 69, 122, 113, 190, 68);
            if (value.Contains("PRODUCTION")) return Style("S", 43, 77, 112, 96, 189, 75);
            if (value.Contains("STANDARDS")) return Style("ST", 69, 82, 103, 127, 183, 70);
            return Style("CE", 25, 69, 118, 111, 190, 68);
        }

        private static IconStyle Style(
            string glyph,
            byte red,
            byte green,
            byte blue,
            byte accentRed,
            byte accentGreen,
            byte accentBlue)
        {
            Color background = Color.FromRgb(red, green, blue);
            return new IconStyle(
                glyph,
                background,
                Color.FromRgb(accentRed, accentGreen, accentBlue),
                Color.FromRgb(
                    (byte)Math.Min(255, red + 45),
                    (byte)Math.Min(255, green + 45),
                    (byte)Math.Min(255, blue + 45)));
        }

        private sealed class IconStyle
        {
            public IconStyle(string glyph, Color background, Color accent, Color border)
            {
                Glyph = glyph;
                Background = background;
                Accent = accent;
                Border = border;
            }

            public string Glyph { get; }
            public Color Background { get; }
            public Color Accent { get; }
            public Color Border { get; }
        }
    }
}
