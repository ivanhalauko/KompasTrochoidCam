using System;
using System.Collections.Generic;
using KompasTrochoidCam.Models;

namespace KompasTrochoidCam.Services
{
    /// <summary>
    /// Демонстрационная деталь для работы без КОМПАС-3D:
    /// плита с прямоугольным карманом и круглым отверстием на дне кармана.
    /// </summary>
    public static class DemoGeometryFactory
    {
        public const double PlateMinX = -45.723;
        public const double PlateMaxX = 44.223;
        public const double PlateMinY = -63.49;
        public const double PlateMaxY = 61.0;
        public const double PlateTopZ = 20.0;
        public const double PocketBottomZ = 10.0;
        public const double PocketHalfX = 30.0;
        public const double PocketHalfY = 40.0;
        public const double HoleRadius = 8.0;

        public static PartGeometry Create()
        {
            var geometry = new PartGeometry
            {
                DocumentName = "Демо-деталь (плита с карманом)",
                Source = "demo",
                LoadedAt = DateTime.Now
            };

            var plateLoop = new PartLoop
            {
                IsOuter = true,
                Points = new List<TrochoidEngine.Point2D>
                {
                    new TrochoidEngine.Point2D(PlateMinX, PlateMinY),
                    new TrochoidEngine.Point2D(PlateMaxX, PlateMinY),
                    new TrochoidEngine.Point2D(PlateMaxX, PlateMaxY),
                    new TrochoidEngine.Point2D(PlateMinX, PlateMaxY)
                }
            };

            var pocketLoop = new PartLoop
            {
                IsOuter = true,
                Points = new List<TrochoidEngine.Point2D>
                {
                    new TrochoidEngine.Point2D(-PocketHalfX, -PocketHalfY),
                    new TrochoidEngine.Point2D(PocketHalfX, -PocketHalfY),
                    new TrochoidEngine.Point2D(PocketHalfX, PocketHalfY),
                    new TrochoidEngine.Point2D(-PocketHalfX, PocketHalfY)
                }
            };

            var holeLoop = new PartLoop
            {
                IsOuter = false,
                Points = CirclePoints(0, 0, HoleRadius, 24)
            };

            var topFace = new PartFace
            {
                Id = 1,
                Name = "Верхняя плоскость плиты",
                Z = PlateTopZ,
                NormalZ = 1,
                Loops = new List<PartLoop> { plateLoop }
            };
            topFace.Area = GeometryMath.FaceArea(topFace);

            var pocketFace = new PartFace
            {
                Id = 2,
                Name = "Дно кармана",
                Z = PocketBottomZ,
                NormalZ = 1,
                Loops = new List<PartLoop> { pocketLoop, holeLoop }
            };
            pocketFace.Area = GeometryMath.FaceArea(pocketFace);

            var bottomFace = new PartFace
            {
                Id = 3,
                Name = "Нижняя плоскость плиты",
                Z = 0,
                NormalZ = -1,
                Loops = new List<PartLoop> { plateLoop }
            };
            bottomFace.Area = GeometryMath.FaceArea(bottomFace);

            geometry.Faces.Add(topFace);
            geometry.Faces.Add(pocketFace);
            geometry.Faces.Add(bottomFace);

            // Рёбра плиты (верхний контур).
            int edgeId = 0;
            AddPolygonEdges(geometry, plateLoop.Points, PlateTopZ, ref edgeId);
            // Рёбра кармана (по дну).
            AddPolygonEdges(geometry, pocketLoop.Points, PocketBottomZ, ref edgeId);
            // Отверстие: четыре дуги по 90°.
            AddCircleEdges(geometry, 0, 0, HoleRadius, PocketBottomZ, ref edgeId);

            geometry.RecalculateBounds();
            return geometry;
        }

        public static List<TrochoidEngine.Point2D> CirclePoints(double cx, double cy, double radius, int count)
        {
            var points = new List<TrochoidEngine.Point2D>(count);
            for (int i = 0; i < count; i++)
            {
                double angle = Math.PI * 2 * i / count;
                points.Add(new TrochoidEngine.Point2D(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
            }
            return points;
        }

        private static void AddPolygonEdges(PartGeometry geometry, IReadOnlyList<TrochoidEngine.Point2D> points, double z, ref int edgeId)
        {
            for (int i = 0; i < points.Count; i++)
            {
                geometry.Edges.Add(new PartEdge
                {
                    Id = ++edgeId,
                    Name = $"Ребро #{edgeId}",
                    Kind = PartCurveKind.Line,
                    Start = points[i],
                    End = points[(i + 1) % points.Count],
                    Z = z
                });
            }
        }

        private static void AddCircleEdges(PartGeometry geometry, double cx, double cy, double radius, double z, ref int edgeId)
        {
            for (int quarter = 0; quarter < 4; quarter++)
            {
                double a0 = Math.PI / 2 * quarter;
                double a1 = a0 + Math.PI / 2;

                geometry.Edges.Add(new PartEdge
                {
                    Id = ++edgeId,
                    Name = $"Дуга отверстия #{edgeId}",
                    Kind = PartCurveKind.Arc,
                    Center = new TrochoidEngine.Point2D(cx, cy),
                    Radius = radius,
                    Sweep = Math.PI / 2,
                    Clockwise = false,
                    Start = new TrochoidEngine.Point2D(cx + radius * Math.Cos(a0), cy + radius * Math.Sin(a0)),
                    End = new TrochoidEngine.Point2D(cx + radius * Math.Cos(a1), cy + radius * Math.Sin(a1)),
                    Z = z
                });
            }
        }
    }
}
