using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using System;

namespace KompasTrochoidCam.Services
{
    /// <summary>Заготовка как карта высот: съём материала фрезой и отрисовка сетки.</summary>
    public class StockRenderer
    {
        private const int GridSize = 100;
        private readonly double[,] _heightGrid = new double[GridSize, GridSize];
        private bool _isInitialized;

        public double MinX { get; private set; } = -55.0;
        public double MaxX { get; private set; } = 55.0;
        public double MinY { get; private set; } = -70.0;
        public double MaxY { get; private set; } = 70.0;
        public double TopZ { get; private set; } = 0.0;
        public double BottomZ { get; private set; } = -20.0;

        /// <summary>Настройка размеров заготовки под конкретную программу.</summary>
        public void Configure(double minX, double maxX, double minY, double maxY, double topZ, double bottomZ)
        {
            if (maxX - minX > 0.01) { MinX = minX; MaxX = maxX; }
            if (maxY - minY > 0.01) { MinY = minY; MaxY = maxY; }
            TopZ = topZ;
            BottomZ = Math.Min(bottomZ, topZ - 0.5);
            Initialize();
        }

        public void Initialize()
        {
            for (int x = 0; x < GridSize; x++)
            {
                for (int y = 0; y < GridSize; y++)
                {
                    _heightGrid[x, y] = TopZ;
                }
            }
            _isInitialized = true;
        }

        /// <summary>Вычитание объёма фрезы из тела заготовки (карта высот).</summary>
        public void CutMaterial(double toolX, double toolY, double toolZ, double toolRadius)
        {
            if (!_isInitialized) Initialize();

            for (int x = 0; x < GridSize; x++)
            {
                double physicalX = MinX + ((double)x / GridSize) * (MaxX - MinX);
                for (int y = 0; y < GridSize; y++)
                {
                    double physicalY = MinY + ((double)y / GridSize) * (MaxY - MinY);
                    double distance = Math.Sqrt(Math.Pow(physicalX - toolX, 2) + Math.Pow(physicalY - toolY, 2));

                    if (distance > toolRadius) continue;

                    double cutZ = Math.Max(toolZ, BottomZ);
                    if (cutZ < _heightGrid[x, y]) _heightGrid[x, y] = cutZ;
                }
            }
        }

        /// <summary>Отрисовка светлой заготовки и тёмных прорезанных участков.</summary>
        public void Render(Canvas canvas, CamSceneContext ctx)
        {
            if (!_isInitialized) Initialize();

            var rawGeometry = new StreamGeometry();
            var cutGeometry = new StreamGeometry();

            using (var rawCtx = rawGeometry.Open())
            using (var cutCtx = cutGeometry.Open())
            {
                for (int x = 0; x < GridSize; x += 4)
                {
                    bool isRawFirst = true;
                    bool isCutFirst = true;

                    for (int y = 0; y < GridSize; y += 2)
                    {
                        double physicalX = MinX + ((double)x / GridSize) * (MaxX - MinX);
                        double physicalY = MinY + ((double)y / GridSize) * (MaxY - MinY);
                        double physicalZ = _heightGrid[x, y];

                        var pScreen = ctx.Project3D(physicalX, physicalY, physicalZ, canvas.Bounds.Width, canvas.Bounds.Height);

                        if (physicalZ < TopZ - 0.01)
                        {
                            if (isCutFirst) { cutCtx.BeginFigure(pScreen, false); isCutFirst = false; }
                            cutCtx.LineTo(pScreen);
                            isRawFirst = true;
                        }
                        else
                        {
                            if (isRawFirst) { rawCtx.BeginFigure(pScreen, false); isRawFirst = false; }
                            rawCtx.LineTo(pScreen);
                            isCutFirst = true;
                        }
                    }
                }
            }

            canvas.Children.Add(new Path { Data = rawGeometry, Stroke = new SolidColorBrush(Colors.LightGray, 0.45), StrokeThickness = 0.9 });
            canvas.Children.Add(new Path { Data = cutGeometry, Stroke = new SolidColorBrush(Colors.Black, 0.75), StrokeThickness = 1.3 });
        }
    }
}
