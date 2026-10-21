using System;

namespace CETools.Core
{
    /// <summary>Fixed plan dimensions measured from a real frontage corner, never parcel size.</summary>
    public sealed class CornerConnectionPlan
    {
        public double StartX, StartY, EndX, EndY;

        public static bool TryCreate(double x, double y, double tx, double ty,
            double nx, double ny, int downstreamSign, double frontageLength,
            double offset, double insideLength, double totalLength, double angleDegrees,
            out CornerConnectionPlan plan)
        {
            plan = null;
            foreach (double value in new[] { x, y, tx, ty, nx, ny, frontageLength,
                offset, insideLength, totalLength, angleDegrees })
                if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            if (frontageLength <= 0 || offset < 0 || offset >= frontageLength ||
                insideLength < 0 || totalLength <= insideLength || Math.Abs(angleDegrees) >= 89) return false;
            double tl = Math.Sqrt(tx * tx + ty * ty), nl = Math.Sqrt(nx * nx + ny * ny);
            if (tl < 1e-9 || nl < 1e-9) return false;
            tx /= tl; ty /= tl; nx /= nl; ny /= nl;
            if (Math.Abs(tx * nx + ty * ny) > 1e-6) return false;
            double radians = angleDegrees * Math.PI / 180;
            double dx = -nx * Math.Cos(radians) + tx * Math.Sign(downstreamSign) * Math.Sin(radians);
            double dy = -ny * Math.Cos(radians) + ty * Math.Sign(downstreamSign) * Math.Sin(radians);
            if (downstreamSign != 1 && downstreamSign != -1) return false;
            double ox = x + tx * offset, oy = y + ty * offset;
            plan = new CornerConnectionPlan {
                StartX = ox - dx * insideLength, StartY = oy - dy * insideLength,
                EndX = ox + dx * (totalLength - insideLength),
                EndY = oy + dy * (totalLength - insideLength)
            };
            return true;
        }
    }
}
