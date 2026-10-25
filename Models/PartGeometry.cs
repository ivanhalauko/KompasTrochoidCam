using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using KompasTrochoidCam.Services;

namespace KompasTrochoidCam.Models
{
    /// <summary>Тип кривой, из которой построено ребро детали.</summary>
    public enum PartCurveKind
    {
        Line = 0,
        Arc = 1
    }

    /// <summary>Замкнутый контур (петля) плоской грани в координатах детали.</summary>
    public sealed class PartLoop
    {
        /// <summary>Точки контура в мм. Последняя точка может совпадать с первой.</summary>
        public List<TrochoidEngine.Point2D> Points { get; set; } = new List<TrochoidEngine.Point2D>();

        /// <summary>Признак внешнего контура (остальные контуры — отверстия/острова).</summary>
        public bool IsOuter { get; set; } = true;

        [JsonIgnore]
        public int PointCount => Points.Count;
    }

    /// <summary>Ребро детали: отрезок или дуга окружности.</summary>
    public sealed class PartEdge
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public PartCurveKind Kind { get; set; } = PartCurveKind.Line;
        public TrochoidEngine.Point2D Start { get; set; }
        public TrochoidEngine.Point2D End { get; set; }
        public TrochoidEngine.Point2D Center { get; set; }
        public double Radius { get; set; }
        /// <summary>Угол дуги в радианах (положительный). Для отрезка не используется.</summary>
        public double Sweep { get; set; }
        public bool Clockwise { get; set; } = true;
        /// <summary>Аппроксимация Z ребра (у плоских контуров совпадает с Z грани).</summary>
        public double Z { get; set; }
        public int FaceId { get; set; } = -1;

        [JsonIgnore]
        public bool IsArc => Kind == PartCurveKind.Arc && Radius > 1e-6;
    }

    /// <summary>Плоская грань детали с контурами.</summary>
    public sealed class PartFace
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Z { get; set; }
        public double NormalX { get; set; }
        public double NormalY { get; set; }
        public double NormalZ { get; set; } = 1.0;
        public List<PartLoop> Loops { get; set; } = new List<PartLoop>();

        /// <summary>Площадь грани с учётом отверстий, мм². Заполняется при чтении геометрии.</summary>
        public double Area { get; set; }

        [JsonIgnore]
        public bool IsHorizontal => Math.Abs(NormalZ) > 0.9;

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                string kind = IsHorizontal ? "плоскость XY" : "наклонная";
                return $"Грань #{Id} · Z={Z:F2} мм · {kind}";
            }
        }
    }

    /// <summary>Геометрия детали, полученная из КОМПАС-3D или из демо-набора.</summary>
    public sealed class PartGeometry
    {
        public string DocumentName { get; set; } = string.Empty;
        public DateTime LoadedAt { get; set; } = DateTime.Now;
        /// <summary>Источник: "kompas" или "demo".</summary>
        public string Source { get; set; } = "demo";
        public List<PartFace> Faces { get; set; } = new List<PartFace>();
        public List<PartEdge> Edges { get; set; } = new List<PartEdge>();

        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }

        [JsonIgnore]
        public bool HasFaces => Faces.Count > 0;

        [JsonIgnore]
        public bool IsEmpty => Faces.Count == 0 && Edges.Count == 0;

        [JsonIgnore]
        public string SizeText => $"{MaxX - MinX:F1} × {MaxY - MinY:F1} × {MaxZ - MinZ:F1} мм";

        /// <summary>Пересчёт габаритов по всем точкам граней и рёбер.</summary>
        public void RecalculateBounds()
        {
            MinX = MinY = MinZ = double.MaxValue;
            MaxX = MaxY = MaxZ = double.MinValue;

            foreach (var face in Faces)
            {
                foreach (var loop in face.Loops)
                {
                    foreach (var p in loop.Points) Extend(p.X, p.Y, face.Z);
                }
                if (face.Loops.Count == 0) Extend(0, 0, face.Z);
            }

            foreach (var edge in Edges)
            {
                Extend(edge.Start.X, edge.Start.Y, edge.Z);
                Extend(edge.End.X, edge.End.Y, edge.Z);
                if (edge.IsArc)
                {
                    Extend(edge.Center.X + edge.Radius, edge.Center.Y, edge.Z);
                    Extend(edge.Center.X - edge.Radius, edge.Center.Y, edge.Z);
                    Extend(edge.Center.X, edge.Center.Y + edge.Radius, edge.Z);
                    Extend(edge.Center.X, edge.Center.Y - edge.Radius, edge.Z);
                }
            }

            if (MinX == double.MaxValue)
            {
                MinX = MinY = MinZ = 0;
                MaxX = MaxY = MaxZ = 0;
            }
        }

        private void Extend(double x, double y, double z)
        {
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
            if (z < MinZ) MinZ = z;
            if (z > MaxZ) MaxZ = z;
        }
    }
}
