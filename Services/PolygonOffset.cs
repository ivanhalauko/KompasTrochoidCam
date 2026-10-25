using System;
using System.Collections.Generic;

namespace KompasTrochoidCam.Services
{
    /// <summary>
    /// Смещение простого замкнутого контура «внутрь» на заданное расстояние.
    /// Реализация через смещение рёбер и пересечение соседних прямых (miter).
    /// Подходит для контуров без самопересечений; при вырождении возвращает пустой список.
    /// </summary>
    public static class PolygonOffset
    {
        public static List<TrochoidEngine.Point2D> OffsetInward(
            IReadOnlyList<TrochoidEngine.Point2D> source, double distance)
        {
            var points = GeometryMath.NormalizeLoop(source);
            if (points.Count < 3 || distance <= GeometryMath.Epsilon)
                return new List<TrochoidEngine.Point2D>(points);

            double signedArea = GeometryMath.SignedArea(points);
            if (Math.Abs(signedArea) < GeometryMath.Epsilon) return new List<TrochoidEngine.Point2D>();

            // Для обхода против часовой (area > 0) внутренность слева от ребра.
            double orientation = signedArea > 0 ? 1.0 : -1.0;

            int n = points.Count;
            var offsetLines = new List<(TrochoidEngine.Point2D A, TrochoidEngine.Point2D B)>(n);
            for (int i = 0; i < n; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < GeometryMath.Epsilon) continue;

                // Левая нормаль к направлению ребра.
                double nx = -dy / len * orientation;
                double ny = dx / len * orientation;

                offsetLines.Add((
                    new TrochoidEngine.Point2D(a.X + nx * distance, a.Y + ny * distance),
                    new TrochoidEngine.Point2D(b.X + nx * distance, b.Y + ny * distance)));
            }

            if (offsetLines.Count < 3) return new List<TrochoidEngine.Point2D>();

            var result = new List<TrochoidEngine.Point2D>(offsetLines.Count);
            for (int i = 0; i < offsetLines.Count; i++)
            {
                var prev = offsetLines[(i - 1 + offsetLines.Count) % offsetLines.Count];
                var cur = offsetLines[i];

                if (GeometryMath.IntersectLines(prev.A, prev.B, cur.A, cur.B, out var intersection))
                {
                    result.Add(intersection);
                }
                else
                {
                    // Параллельные рёбра: берём среднюю точку смещённых начал.
                    result.Add(new TrochoidEngine.Point2D((prev.A.X + cur.A.X) / 2, (prev.A.Y + cur.A.Y) / 2));
                }
            }

            result = GeometryMath.NormalizeLoop(result);
            if (result.Count < 3) return new List<TrochoidEngine.Point2D>();

            double newArea = GeometryMath.Area(result);
            if (newArea < 1.0) return new List<TrochoidEngine.Point2D>();

            // Смещение не должно «выворачивать» контур наружу.
            if (GeometryMath.SignedArea(result) * orientation <= 0) return new List<TrochoidEngine.Point2D>();
            if (newArea >= Math.Abs(signedArea) - GeometryMath.Epsilon) return new List<TrochoidEngine.Point2D>();

            return result;
        }

        /// <summary>Наибольшее расстояние, на которое можно сместить контур внутрь (грубая оценка).</summary>
        public static double EstimateInscribedRadius(IReadOnlyList<TrochoidEngine.Point2D> loop)
        {
            var points = GeometryMath.NormalizeLoop(loop);
            if (points.Count < 3) return 0;

            double area = GeometryMath.Area(points);
            double perimeter = 0;
            for (int i = 0; i < points.Count; i++)
                perimeter += GeometryMath.Distance(points[i], points[(i + 1) % points.Count]);

            if (perimeter < GeometryMath.Epsilon) return 0;
            // Для круга r = 2S/P; используем как оценку сверху для произвольного контура.
            return 2.0 * area / perimeter;
        }
    }
}
