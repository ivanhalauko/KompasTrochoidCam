using System;

namespace KompasTrochoidCam.Services
{
    /// <summary>Камера сцены: повороты, масштаб и смещение для ортогональной проекции.</summary>
    public class CamSceneContext
    {
        public double RotX { get; set; } = 30 * Math.PI / 180.0;
        public double RotZ { get; set; } = -45 * Math.PI / 180.0;
        public double Scale { get; set; } = 3.5;
        public double OffsetX { get; set; } = 0.0;
        public double OffsetY { get; set; } = 0.0;

        /// <summary>Проекция без масштаба и смещения (для расчёта вписывания).</summary>
        public Avalonia.Point ProjectRaw(double x, double y, double z)
        {
            double x1 = x * Math.Cos(RotZ) - y * Math.Sin(RotZ);
            double y1 = x * Math.Sin(RotZ) + y * Math.Cos(RotZ);

            double y2 = y1 * Math.Cos(RotX) - z * Math.Sin(RotX);
            return new Avalonia.Point(x1, y2);
        }

        /// <summary>Математическая матрица проекции 3D-точки на 2D-экран.</summary>
        public Avalonia.Point Project3D(double x, double y, double z, double canvasWidth, double canvasHeight)
        {
            double cx = (canvasWidth / 2) + OffsetX;
            double cy = (canvasHeight / 2) + OffsetY;
            if (canvasWidth == 0) cx = 300 + OffsetX;
            if (canvasHeight == 0) cy = 300 + OffsetY;

            var raw = ProjectRaw(x, y, z);
            return new Avalonia.Point(cx + raw.X * Scale, cy - raw.Y * Scale);
        }

        /// <summary>Вписывает параллелепипед в область холста с отступом.</summary>
        public void FitTo(
            double minX, double maxX, double minY, double maxY, double minZ, double maxZ,
            double canvasWidth, double canvasHeight, double padding = 60)
        {
            if (canvasWidth <= 10 || canvasHeight <= 10) return;
            if (maxX - minX < 0.001 && maxY - minY < 0.001) return;

            double rawMinX = double.MaxValue, rawMaxX = double.MinValue;
            double rawMinY = double.MaxValue, rawMaxY = double.MinValue;

            for (int i = 0; i < 8; i++)
            {
                double x = (i & 1) == 0 ? minX : maxX;
                double y = (i & 2) == 0 ? minY : maxY;
                double z = (i & 4) == 0 ? minZ : maxZ;

                var p = ProjectRaw(x, y, z);
                if (p.X < rawMinX) rawMinX = p.X;
                if (p.X > rawMaxX) rawMaxX = p.X;
                if (p.Y < rawMinY) rawMinY = p.Y;
                if (p.Y > rawMaxY) rawMaxY = p.Y;
            }

            double width = Math.Max(rawMaxX - rawMinX, 0.001);
            double height = Math.Max(rawMaxY - rawMinY, 0.001);
            double availableWidth = Math.Max(canvasWidth - padding * 2, 50);
            double availableHeight = Math.Max(canvasHeight - padding * 2, 50);

            Scale = Math.Min(availableWidth / width, availableHeight / height);
            if (Scale < 0.05) Scale = 0.05;
            if (Scale > 500) Scale = 500;

            double centerX = (rawMinX + rawMaxX) / 2.0;
            double centerY = (rawMinY + rawMaxY) / 2.0;

            OffsetX = -centerX * Scale;
            OffsetY = centerY * Scale;
        }
    }
}
