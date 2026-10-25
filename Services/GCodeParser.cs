using System;
using System.Collections.Generic;
using System.Globalization;

namespace KompasTrochoidCam.Services
{
    /// <summary>Точка траектории инструмента, полученная из УП.</summary>
    public struct ToolpathPoint
    {
        public double X;
        public double Y;
        public double Z;
        public int LineIndex;
        public bool Rapid;

        public ToolpathPoint(double x, double y, double z, int lineIndex, bool rapid)
        {
            X = x; Y = y; Z = z; LineIndex = lineIndex; Rapid = rapid;
        }
    }

    /// <summary>Результат разбора управляющей программы.</summary>
    public sealed class ParsedProgram
    {
        public List<ToolpathPoint> Points { get; } = new List<ToolpathPoint>();
        public double TotalTimeSeconds { get; set; }
        public int LineCount { get; set; }

        public bool HasBounds { get; set; }
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }

        public void Extend(double x, double y, double z)
        {
            if (!HasBounds)
            {
                MinX = MaxX = x;
                MinY = MaxY = y;
                MinZ = MaxZ = z;
                HasBounds = true;
                return;
            }
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
            if (z < MinZ) MinZ = z;
            if (z > MaxZ) MaxZ = z;
        }
    }

    /// <summary>
    /// Разбор G-кода: G0/G1/G2/G3, G90/G91, G20/G21, I/J/R.
    /// Дуги аппроксимируются отрезками (по умолчанию 15° на сегмент).
    /// </summary>
    public static class GCodeParser
    {
        private const double RapidFeed = 5000.0;
        private const double DegreesPerSegment = Math.PI / 12;

        public static ParsedProgram Parse(string gcode)
        {
            var result = new ParsedProgram();
            if (string.IsNullOrWhiteSpace(gcode)) return result;

            string[] lines = gcode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            result.LineCount = lines.Length;

            double x = 0, y = 0, z = 0;
            double feed = 400;
            double scale = 1.0;
            bool absolute = true;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = StripComments(lines[i]);
                if (line.Length == 0) continue;

                // Режимы, влияющие на интерпретацию координат.
                var gCodes = ExtractCodes(line, 'G');
                foreach (int g in gCodes)
                {
                    if (g == 20) scale = 25.4;
                    else if (g == 21) scale = 1.0;
                    else if (g == 90) absolute = true;
                    else if (g == 91) absolute = false;
                }

                if (TryGetWord(line, 'F', out double f) && f > 0) feed = f;

                bool hasX = TryGetWord(line, 'X', out double nx);
                bool hasY = TryGetWord(line, 'Y', out double ny);
                bool hasZ = TryGetWord(line, 'Z', out double nz);

                double targetX = hasX ? (absolute ? nx * scale : x + nx * scale) : x;
                double targetY = hasY ? (absolute ? ny * scale : y + ny * scale) : y;
                double targetZ = hasZ ? (absolute ? nz * scale : z + nz * scale) : z;

                bool isRapid = gCodes.Contains(0);
                bool isLine = gCodes.Contains(1);
                bool isArc = gCodes.Contains(2) || gCodes.Contains(3);

                if (isArc && (hasX || hasY || TryGetWord(line, 'I', out _) || TryGetWord(line, 'J', out _) || TryGetWord(line, 'R', out _)))
                {
                    AppendArc(result, line, x, y, targetX, targetY, targetZ, i, feed, gCodes.Contains(2));
                }
                else if (isRapid || isLine)
                {
                    bool rapid = isRapid && !isLine;
                    result.Points.Add(new ToolpathPoint(targetX, targetY, targetZ, i, rapid));
                    result.Extend(targetX, targetY, targetZ);

                    double distance = Math.Sqrt(
                        (targetX - x) * (targetX - x) +
                        (targetY - y) * (targetY - y) +
                        (targetZ - z) * (targetZ - z));
                    result.TotalTimeSeconds += distance / (rapid ? RapidFeed : Math.Max(feed, 1)) * 60.0;
                }

                x = targetX; y = targetY; z = targetZ;
            }

            return result;
        }

        private static void AppendArc(
            ParsedProgram result, string line,
            double startX, double startY, double endX, double endY, double endZ,
            int lineIndex, double feed, bool clockwise)
        {
            double centerX, centerY;

            bool hasI = TryGetWord(line, 'I', out double iVal);
            bool hasJ = TryGetWord(line, 'J', out double jVal);

            if (hasI || hasJ)
            {
                centerX = startX + iVal;
                centerY = startY + jVal;
            }
            else if (TryGetWord(line, 'R', out double rWord))
            {
                if (!TryComputeArcCenter(startX, startY, endX, endY, rWord, clockwise, out centerX, out centerY))
                {
                    result.Points.Add(new ToolpathPoint(endX, endY, endZ, lineIndex, false));
                    result.Extend(endX, endY, endZ);
                    return;
                }
            }
            else
            {
                return;
            }

            double radius = Math.Sqrt((startX - centerX) * (startX - centerX) + (startY - centerY) * (startY - centerY));
            if (radius < 1e-6) return;

            double a0 = Math.Atan2(startY - centerY, startX - centerX);
            double a1 = Math.Atan2(endY - centerY, endX - centerX);
            double sweep = Sweep(a0, a1, clockwise);

            bool samePoint = Math.Abs(endX - startX) < 1e-9 && Math.Abs(endY - startY) < 1e-9;
            if (samePoint) sweep = Math.PI * 2;

            result.TotalTimeSeconds += (sweep * radius) / Math.Max(feed, 1) * 60.0;

            int segments = Math.Max(2, (int)Math.Ceiling(sweep / DegreesPerSegment));
            double sign = clockwise ? -1.0 : 1.0;

            for (int s = 0; s <= segments; s++)
            {
                double a = a0 + sign * sweep * s / segments;
                double px = centerX + radius * Math.Cos(a);
                double py = centerY + radius * Math.Sin(a);
                result.Points.Add(new ToolpathPoint(px, py, endZ, lineIndex, false));
                result.Extend(px, py, endZ);
            }
        }

        private static double Sweep(double from, double to, bool clockwise)
        {
            double twoPi = Math.PI * 2;
            double sweep = clockwise ? from - to : to - from;
            sweep %= twoPi;
            if (sweep <= 0) sweep += twoPi;
            return sweep;
        }

        private static bool TryComputeArcCenter(
            double sx, double sy, double ex, double ey, double r,
            bool clockwise, out double cx, out double cy)
        {
            cx = cy = 0;
            double dx = ex - sx, dy = ey - sy;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9 || d > Math.Abs(r) * 2 + 1e-6) return false;

            double radius = Math.Abs(r);
            double h = Math.Sqrt(Math.Max(0, radius * radius - (d / 2) * (d / 2)));
            double mx = (sx + ex) / 2, my = (sy + ey) / 2;
            double ux = -dy / d, uy = dx / d;

            double c1x = mx + ux * h, c1y = my + uy * h;
            double c2x = mx - ux * h, c2y = my - uy * h;

            double a0 = Math.Atan2(sy - c1y, sx - c1x);
            double a1 = Math.Atan2(ey - c1y, ex - c1x);
            double sweep1 = Sweep(a0, a1, clockwise);
            double sweep2 = Math.PI * 2 - sweep1;

            bool minor = r > 0;
            bool c1isMinor = sweep1 <= Math.PI;
            if (minor == c1isMinor) { cx = c1x; cy = c1y; }
            else { cx = c2x; cy = c2y; }
            return true;
        }

        private static string StripComments(string raw)
        {
            string line = raw;

            int paren = line.IndexOf('(');
            if (paren >= 0) line = line.Substring(0, paren);

            int semi = line.IndexOf(';');
            if (semi >= 0) line = line.Substring(0, semi);

            return line.Trim();
        }

        private static List<int> ExtractCodes(string line, char letter)
        {
            var codes = new List<int>();
            for (int i = 0; i < line.Length; i++)
            {
                if (char.ToUpperInvariant(line[i]) != letter) continue;

                int start = i + 1;
                int end = start;
                while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '-' || line[end] == '+')) end++;
                if (end > start && int.TryParse(line.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    codes.Add(value);

                i = Math.Max(i, end - 1);
            }
            return codes;
        }

        private static bool TryGetWord(string line, char letter, out double value)
        {
            value = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (char.ToUpperInvariant(line[i]) != letter) continue;

                int start = i + 1;
                int end = start;
                while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '-' || line[end] == '+' || line[end] == '.')) end++;

                if (end > start &&
                    double.TryParse(line.Substring(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    value = parsed;
                    return true;
                }

                i = Math.Max(i, end - 1);
            }
            return false;
        }
    }
}
