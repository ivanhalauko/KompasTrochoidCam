using System;
using System.Collections.Generic;
using KompasTrochoidCam.Models;

namespace KompasTrochoidCam.Services
{
    /// <summary>Плоская геометрия: площади, нормализация контуров, аппроксимация дуг.</summary>
    public static class GeometryMath
    {
        public const double Epsilon = 1e-6;

        public static double Distance(TrochoidEngine.Point2D a, TrochoidEngine.Point2D b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static bool SamePoint(TrochoidEngine.Point2D a, TrochoidEngine.Point2D b, double tolerance = 1e-3)
            => Distance(a, b) <= tolerance;

        /// <summary>Убирает повторяющуюся замыкающую точку и подряд идущие дубликаты.</summary>
        public static List<TrochoidEngine.Point2D> NormalizeLoop(IReadOnlyList<TrochoidEngine.Point2D> points)
        {
            var result = new List<TrochoidEngine.Point2D>();
            foreach (var p in points)
            {
                if (result.Count == 0 || !SamePoint(result[result.Count - 1], p)) result.Add(p);
            }
            if (result.Count > 1 && SamePoint(result[0], result[result.Count - 1])) result.RemoveAt(result.Count - 1);
            return result;
        }

        /// <summary>Удвоенная площадь со знаком (формула шнурков). Положительная — обход против часовой.</summary>
        public static double SignedArea(IReadOnlyList<TrochoidEngine.Point2D> loop)
        {
            if (loop.Count < 3) return 0;
            double sum = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return sum * 0.5;
        }

        public static double Area(IReadOnlyList<TrochoidEngine.Point2D> loop) => Math.Abs(SignedArea(loop));

        /// <summary>Площадь грани: внешние контуры минус внутренние (отверстия/острова).</summary>
        public static double FaceArea(PartFace face)
        {
            double outer = 0, inner = 0;
            foreach (var loop in face.Loops)
            {
                double a = Area(NormalizeLoop(loop.Points));
                if (loop.IsOuter) outer += a; else inner += a;
            }
            double result = outer - inner;
            return result > 0 ? result : outer;
        }

        /// <summary>Аппроксимация дуги ребра отрезками.</summary>
        public static List<TrochoidEngine.Point2D> SampleEdge(PartEdge edge, double maxSegmentAngle = Math.PI / 12)
        {
            if (!edge.IsArc)
                return new List<TrochoidEngine.Point2D> { edge.Start, edge.End };

            double sweep = edge.Sweep > Epsilon ? edge.Sweep : Math.PI * 2;
            int segments = Math.Max(2, (int)Math.Ceiling(sweep / maxSegmentAngle));
            double a0 = Math.Atan2(edge.Start.Y - edge.Center.Y, edge.Start.X - edge.Center.X);
            double sign = edge.Clockwise ? -1.0 : 1.0;

            var points = new List<TrochoidEngine.Point2D>(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                double angle = a0 + sign * sweep * i / segments;
                points.Add(new TrochoidEngine.Point2D(
                    edge.Center.X + edge.Radius * Math.Cos(angle),
                    edge.Center.Y + edge.Radius * Math.Sin(angle)));
            }
            return points;
        }

        public static double NormalizeAngle(double angle)
        {
            double twoPi = Math.PI * 2;
            angle %= twoPi;
            if (angle < 0) angle += twoPi;
            return angle;
        }

        /// <summary>Общая точка двух прямых (бесконечных). false — прямые параллельны.</summary>
        public static bool IntersectLines(
            TrochoidEngine.Point2D a1, TrochoidEngine.Point2D a2,
            TrochoidEngine.Point2D b1, TrochoidEngine.Point2D b2,
            out TrochoidEngine.Point2D result)
        {
            double dax = a2.X - a1.X, day = a2.Y - a1.Y;
            double dbx = b2.X - b1.X, dby = b2.Y - b1.Y;
            double denom = dax * dby - day * dbx;
            if (Math.Abs(denom) < 1e-9)
            {
                result = new TrochoidEngine.Point2D((a1.X + a2.X) / 2, (a1.Y + a2.Y) / 2);
                return false;
            }
            double t = ((b1.X - a1.X) * dby - (b1.Y - a1.Y) * dbx) / denom;
            result = new TrochoidEngine.Point2D(a1.X + dax * t, a1.Y + day * t);
            return true;
        }

        /// <summary>Точка внутри замкнутого контура (алгоритм трассировки луча).</summary>
        public static bool PointInPolygon(TrochoidEngine.Point2D p, IReadOnlyList<TrochoidEngine.Point2D> loop)
        {
            bool inside = false;
            int n = loop.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pi = loop[i];
                var pj = loop[j];
                if ((pi.Y > p.Y) != (pj.Y > p.Y) &&
                    p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y + 1e-12) + pi.X)
                {
                    inside = !inside;
                }
            }
            return inside;
        }
    }
}
