using System;
using System.Collections.Generic;
using System.Linq;
using KompasTrochoidCam.Models;

namespace KompasTrochoidCam.Services
{
    /// <summary>Область обработки: контуры + оценка объёма снимаемого металла.</summary>
    public sealed class MachiningRegion
    {
        /// <summary>Внешние контуры области (в координатах детали).</summary>
        public List<List<TrochoidEngine.Point2D>> Loops { get; } = new List<List<TrochoidEngine.Point2D>>();

        /// <summary>Площадь проекции области, мм².</summary>
        public double Area { get; set; }

        /// <summary>Глубина обработки, мм.</summary>
        public double Depth { get; set; }

        /// <summary>Z, на которой удобно рисовать контур области (верх обработки).</summary>
        public double DisplayZ { get; set; }

        public double VolumeMm3 => Area * Depth;

        public double VolumeCm3 => VolumeMm3 / 1000.0;

        public bool HasArea => Area > 0.01 && Loops.Count > 0;

        public string Summary
        {
            get
            {
                if (!HasArea) return "Область обработки не задана";
                return $"Площадь {Area:N0} мм² · глубина {Depth:F1} мм · объём {VolumeCm3:F2} см³";
            }
        }
    }

    /// <summary>Построение области обработки по выбранным граням и рёбрам.</summary>
    public static class RegionCalculator
    {
        public static MachiningRegion Build(
            PartGeometry? geometry,
            IEnumerable<int> faceIds,
            IEnumerable<int> edgeIds,
            double zTop,
            double zBottom)
        {
            var region = new MachiningRegion();
            double depth = zTop - zBottom;
            region.Depth = depth > 0 ? depth : 0;
            region.DisplayZ = zTop;

            if (geometry == null) return region;

            var faceSet = new HashSet<int>(faceIds);
            var edgeSet = new HashSet<int>(edgeIds);

            // 1. Грани: используем внешние контуры выбранных граней.
            foreach (var face in geometry.Faces.Where(f => faceSet.Contains(f.Id)))
            {
                foreach (var loop in face.Loops.Where(l => l.IsOuter))
                {
                    var points = GeometryMath.NormalizeLoop(loop.Points);
                    if (points.Count < 3) continue;
                    region.Loops.Add(points);
                    region.Area += GeometryMath.Area(points);
                }
            }

            // 2. Рёбра: сшиваем в замкнутые контуры.
            var edges = geometry.Edges.Where(e => edgeSet.Contains(e.Id)).ToList();
            foreach (var loop in ChainEdges(edges))
            {
                if (loop.Count < 3) continue;
                region.Loops.Add(loop);
                region.Area += GeometryMath.Area(loop);
            }

            // 3. Если ничего не выбрано — берём все горизонтальные грани (кроме верхней).
            if (region.Loops.Count == 0 && geometry.Faces.Count > 0)
            {
                var fallback = geometry.Faces
                    .Where(f => f.IsHorizontal && f.NormalZ > 0.9 && f.Loops.Count > 0)
                    .OrderBy(f => f.Z)
                    .FirstOrDefault();

                if (fallback == null)
                {
                    fallback = geometry.Faces
                        .Where(f => f.IsHorizontal && f.Loops.Count > 0)
                        .OrderBy(f => f.Z)
                        .FirstOrDefault();
                }
                if (fallback != null)
                {
                    foreach (var loop in fallback.Loops.Where(l => l.IsOuter))
                    {
                        var points = GeometryMath.NormalizeLoop(loop.Points);
                        if (points.Count < 3) continue;
                        region.Loops.Add(points);
                        region.Area += GeometryMath.Area(points);
                    }
                }
            }

            return region;
        }

        /// <summary>Сшивка набора рёбер в замкнутые контуры по совпадению концов.</summary>
        public static List<List<TrochoidEngine.Point2D>> ChainEdges(IReadOnlyList<PartEdge> edges)
        {
            var loops = new List<List<TrochoidEngine.Point2D>>();
            var used = new bool[edges.Count];

            for (int i = 0; i < edges.Count; i++)
            {
                if (used[i]) continue;

                var chain = new List<TrochoidEngine.Point2D>();
                var first = edges[i];
                used[i] = true;
                chain.AddRange(GeometryMath.SampleEdge(first));

                for (int guard = 0; guard < edges.Count + 1; guard++)
                {
                    var lastPoint = chain[chain.Count - 1];
                    if (GeometryMath.SamePoint(lastPoint, chain[0], 0.01) && chain.Count > 2)
                    {
                        break;
                    }

                    int nextIndex = -1;
                    bool reverse = false;
                    for (int j = 0; j < edges.Count; j++)
                    {
                        if (used[j]) continue;
                        if (GeometryMath.SamePoint(edges[j].Start, lastPoint, 0.01))
                        {
                            nextIndex = j;
                            reverse = false;
                            break;
                        }
                        if (GeometryMath.SamePoint(edges[j].End, lastPoint, 0.01))
                        {
                            nextIndex = j;
                            reverse = true;
                            break;
                        }
                    }

                    if (nextIndex < 0) break;

                    used[nextIndex] = true;
                    var sampled = GeometryMath.SampleEdge(edges[nextIndex]);
                    if (reverse) sampled.Reverse();
                    foreach (var p in sampled)
                    {
                        if (!GeometryMath.SamePoint(chain[chain.Count - 1], p)) chain.Add(p);
                    }
                }

                var normalized = GeometryMath.NormalizeLoop(chain);
                if (normalized.Count >= 3) loops.Add(normalized);
            }

            return loops;
        }
    }
}
