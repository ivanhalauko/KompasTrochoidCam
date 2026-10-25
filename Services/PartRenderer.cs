using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using KompasTrochoidCam.Models;

namespace KompasTrochoidCam.Services
{
    /// <summary>Данные для отрисовки сцены в главном окне.</summary>
    public sealed class PartRenderOptions
    {
        public PartGeometry? Geometry { get; set; }
        public StockDefinition? Stock { get; set; }
        public MachiningRegion? Region { get; set; }
        public IReadOnlyList<ToolpathPoint> Trajectory { get; set; } = Array.Empty<ToolpathPoint>();
        public IReadOnlyCollection<int> SelectedFaceIds { get; set; } = Array.Empty<int>();
        public IReadOnlyCollection<int> SelectedEdgeIds { get; set; } = Array.Empty<int>();
        /// <summary>Смещение Z траектории (программные Z → координаты детали).</summary>
        public double TrajectoryZOffset { get; set; }
    }

    /// <summary>Отрисовка заготовки, геометрии детали, области обработки и траектории.</summary>
    public sealed class PartRenderer
    {
        private static readonly IBrush StockBrush = new SolidColorBrush(Color.Parse("#6E7681"), 0.9);
        private static readonly IBrush FaceBrush = new SolidColorBrush(Color.Parse("#8B949E"), 0.75);
        private static readonly IBrush SelectedFaceFill = new SolidColorBrush(Color.Parse("#1F6FEB"), 0.22);
        private static readonly IBrush SelectedFaceStroke = new SolidColorBrush(Color.Parse("#58A6FF"), 1.0);
        private static readonly IBrush EdgeBrush = new SolidColorBrush(Color.Parse("#586069"), 0.9);
        private static readonly IBrush SelectedEdgeBrush = new SolidColorBrush(Color.Parse("#F0883E"), 1.0);
        private static readonly IBrush RegionBrush = new SolidColorBrush(Color.Parse("#F2CC60"), 1.0);
        private static readonly IBrush TrajectoryBrush = new SolidColorBrush(Color.Parse("#3FB950"), 1.0);
        private static readonly IBrush RapidBrush = new SolidColorBrush(Color.Parse("#DA3633"), 0.8);

        public void Render(Canvas canvas, CamSceneContext context, PartRenderOptions options)
        {
            canvas.Children.Clear();

            double width = canvas.Bounds.Width;
            double height = canvas.Bounds.Height;
            if (width <= 1 || height <= 1) return;

            if (options.Stock != null) DrawStock(canvas, context, options.Stock, width, height);
            if (options.Geometry != null)
            {
                DrawFaces(canvas, context, options.Geometry, options.SelectedFaceIds, width, height);
                DrawEdges(canvas, context, options.Geometry, options.SelectedEdgeIds, width, height);
            }

            if (options.Region != null) DrawRegion(canvas, context, options.Region, width, height);
            DrawTrajectory(canvas, context, options, width, height);
        }

        private static void DrawStock(Canvas canvas, CamSceneContext context, StockDefinition stock, double width, double height)
        {
            var corners = new List<Point>(8);
            for (int i = 0; i < 8; i++)
            {
                double x = (i & 1) == 0 ? stock.MinX : stock.MaxX;
                double y = (i & 2) == 0 ? stock.MinY : stock.MaxY;
                double z = (i & 4) == 0 ? stock.BottomZ : stock.TopZ;
                corners.Add(context.Project3D(x, y, z, width, height));
            }

            int[,] edges =
            {
                {0,1},{1,3},{3,2},{2,0},
                {4,5},{5,7},{7,6},{6,4},
                {0,4},{1,5},{2,6},{3,7}
            };

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (int i = 0; i < edges.GetLength(0); i++)
                {
                    ctx.BeginFigure(corners[edges[i, 0]], false);
                    ctx.LineTo(corners[edges[i, 1]]);
                }
            }

            canvas.Children.Add(new Path { Data = geometry, Stroke = StockBrush, StrokeThickness = 1.0 });
        }

        private static void DrawFaces(
            Canvas canvas, CamSceneContext context, PartGeometry geometry,
            IReadOnlyCollection<int> selectedFaceIds, double width, double height)
        {
            var selected = new HashSet<int>(selectedFaceIds);

            foreach (var face in geometry.Faces)
            {
                foreach (var loop in face.Loops)
                {
                    var points = GeometryMath.NormalizeLoop(loop.Points);
                    if (points.Count < 3) continue;

                    bool isSelected = selected.Contains(face.Id);
                    var projected = new List<Point>(points.Count);
                    foreach (var p in points) projected.Add(context.Project3D(p.X, p.Y, face.Z, width, height));

                    var path = BuildPolygon(projected, closed: true, fill: isSelected ? SelectedFaceFill : null,
                        stroke: isSelected ? SelectedFaceStroke : FaceBrush,
                        thickness: isSelected ? 1.6 : 0.8);

                    canvas.Children.Add(path);
                }
            }
        }

        private static void DrawEdges(
            Canvas canvas, CamSceneContext context, PartGeometry geometry,
            IReadOnlyCollection<int> selectedEdgeIds, double width, double height)
        {
            var selected = new HashSet<int>(selectedEdgeIds);

            foreach (var edge in geometry.Edges)
            {
                var sampled = GeometryMath.SampleEdge(edge, Math.PI / 24);
                if (sampled.Count < 2) continue;

                var geometryPath = new StreamGeometry();
                using (var ctx = geometryPath.Open())
                {
                    ctx.BeginFigure(context.Project3D(sampled[0].X, sampled[0].Y, edge.Z, width, height), false);
                    for (int i = 1; i < sampled.Count; i++)
                        ctx.LineTo(context.Project3D(sampled[i].X, sampled[i].Y, edge.Z, width, height));
                }

                bool isSelected = selected.Contains(edge.Id);
                canvas.Children.Add(new Path
                {
                    Data = geometryPath,
                    Stroke = isSelected ? SelectedEdgeBrush : EdgeBrush,
                    StrokeThickness = isSelected ? 2.0 : 0.7
                });
            }
        }

        private static void DrawRegion(Canvas canvas, CamSceneContext context, MachiningRegion region, double width, double height)
        {
            if (!region.HasArea) return;

            foreach (var loop in region.Loops)
            {
                var points = GeometryMath.NormalizeLoop(loop);
                if (points.Count < 3) continue;

                var projected = new List<Point>(points.Count);
                foreach (var p in points)
                {
                    projected.Add(context.Project3D(p.X, p.Y, region.DisplayZ, width, height));
                }

                canvas.Children.Add(BuildPolygon(projected, closed: true, fill: null, stroke: RegionBrush, thickness: 2.0));
            }
        }

        private static void DrawTrajectory(Canvas canvas, CamSceneContext context, PartRenderOptions options, double width, double height)
        {
            var points = options.Trajectory;
            if (points.Count < 2) return;

            var cutting = new StreamGeometry();
            var rapid = new StreamGeometry();

            using (var cuttingCtx = cutting.Open())
            using (var rapidCtx = rapid.Open())
            {
                bool cuttingStarted = false;
                bool rapidStarted = false;

                for (int i = 0; i < points.Count - 1; i++)
                {
                    var a = points[i];
                    var b = points[i + 1];

                    var pa = context.Project3D(a.X, a.Y, a.Z + options.TrajectoryZOffset, width, height);
                    var pb = context.Project3D(b.X, b.Y, b.Z + options.TrajectoryZOffset, width, height);

                    if (a.Rapid)
                    {
                        if (!rapidStarted) { rapidCtx.BeginFigure(pa, false); rapidStarted = true; }
                        rapidCtx.LineTo(pb);
                        cuttingStarted = false;
                    }
                    else
                    {
                        if (!cuttingStarted) { cuttingCtx.BeginFigure(pa, false); cuttingStarted = true; }
                        cuttingCtx.LineTo(pb);
                        rapidStarted = false;
                    }
                }

            }

            canvas.Children.Add(new Path { Data = rapid, Stroke = RapidBrush, StrokeThickness = 0.7 });
            canvas.Children.Add(new Path { Data = cutting, Stroke = TrajectoryBrush, StrokeThickness = 1.8 });
        }

        private static Path BuildPolygon(IReadOnlyList<Point> points, bool closed, IBrush? fill, IBrush stroke, double thickness)
        {
            var geometry = new StreamGeometry();

            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(points[0], fill != null && closed);
                for (int i = 1; i < points.Count; i++) ctx.LineTo(points[i]);
            }

            return new Path
            {
                Data = geometry,
                Stroke = stroke,
                StrokeThickness = thickness,
                Fill = fill
            };
        }
    }
}
