using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KompasTrochoidCam.Models;

namespace KompasTrochoidCam.Services
{
    /// <summary>Результат генерации одной операции.</summary>
    public sealed class OperationProgramResult
    {
        public string GCode { get; set; } = string.Empty;
        public double CuttingLengthMm { get; set; }
        public double RapidLengthMm { get; set; }
        public bool IsTruncated { get; set; }
        public List<string> Warnings { get; } = new List<string>();

        public bool IsEmpty => string.IsNullOrEmpty(GCode);
    }

    /// <summary>
    /// Стратегия «Трохоидальная обработка по Z-уровням»:
    /// на каждом уровне глубины контур смещается внутрь на радиус фрезы + припуск,
    /// между проходами очистки — радиальный шаг, вдоль контура — петли G2 с шагом Ae.
    /// </summary>
    public static class ZLevelTrochoidStrategy
    {
        private const int MaxLevels = 2000;
        private const int MaxPasses = 400;

        public static OperationProgramResult Build(
            MachiningOperation op,
            ToolDefinition tool,
            MachiningPlan plan,
            MachiningRegion region,
            int operationIndex,
            int circleBudget = int.MaxValue,
            int maxLevels = int.MaxValue)
        {
            var result = new OperationProgramResult();

            if (!region.HasArea)
            {
                result.Warnings.Add("область обработки не задана");
                return result;
            }

            if (op.Depth <= 0.001)
            {
                result.Warnings.Add("Z начала не выше Z дна");
                return result;
            }

            if (tool.Diameter <= 0.01)
            {
                result.Warnings.Add("диаметр инструмента должен быть больше нуля");
                return result;
            }

            var sb = new StringBuilder();
            double stockTop = plan.Stock.TopZ;
            double toolRadius = tool.Diameter / 2.0;
            double stepAe = op.StepAe > 0.05 ? op.StepAe : 0.1;
            double radialStep = op.RadialStep > 0.1 ? op.RadialStep : tool.Diameter * 0.4;
            double safeZ = 50.0;

            sb.AppendLine();
            sb.AppendLine($"( ===== Операция {operationIndex}: {op.Name} ===== )");
            sb.AppendLine($"( Инструмент T{op.ToolNumber} · {tool.TypeName} · Ø{tool.Diameter:F2} мм )");
            sb.AppendLine($"( S={op.SpindleSpeed:F0} об/мин · F={op.Feed:F0} мм/мин · коррекция r={op.Correction:F2} )");
            sb.AppendLine($"( Z: {op.ZTop:F3} → {op.ZBottom:F3} · шаг {op.StepZ:F2} · Ae={stepAe:F2} · припуск {op.Allowance:F2} )");

            sb.AppendLine($"T{op.ToolNumber} M6");
            sb.AppendLine($"S{Num(op.SpindleSpeed)} M3");
            sb.AppendLine($"G0 Z{Num(safeZ)}");

            int levelCount = (int)Math.Ceiling(op.Depth / Math.Max(op.StepZ, 0.01));
            if (levelCount > MaxLevels)
            {
                result.Warnings.Add($"слишком много уровней ({levelCount}), ограничено {MaxLevels}");
                levelCount = MaxLevels;
            }

            int circles = 0;

            int renderedLevels = Math.Min(levelCount, maxLevels);

            for (int level = 1; level <= renderedLevels && !result.IsTruncated; level++)
            {
                double zPart = op.ZTop - level * op.StepZ;
                if (level == renderedLevels || zPart < op.ZBottom) zPart = op.ZBottom;
                double zProgram = zPart - stockTop;

                sb.AppendLine($"( --- уровень {level}/{levelCount}: Z={zPart:F3} --- )");

                for (int pass = 0; pass < MaxPasses && !result.IsTruncated; pass++)
                {
                    double offset = toolRadius + op.Allowance + pass * radialStep;
                    var passLoops = new List<List<TrochoidEngine.Point2D>>();

                    foreach (var loop in region.Loops)
                    {
                        double limit = PolygonOffset.EstimateInscribedRadius(loop);
                        if (offset >= limit - 0.05) continue;

                        var shifted = PolygonOffset.OffsetInward(loop, offset);
                        if (shifted.Count >= 3) passLoops.Add(shifted);
                    }

                    if (passLoops.Count == 0) break;

                    foreach (var loop in passLoops)
                    {
                        if (result.IsTruncated) break;

                        AppendTrochoidLoop(
                            sb, loop, toolRadius, stepAe, safeZ, zProgram,
                            op.Feed, op.PlungeFeed > 0 ? op.PlungeFeed : 50,
                            result, ref circles, circleBudget);
                    }
                }
            }

            sb.AppendLine($"G0 Z{Num(safeZ)}");
            sb.AppendLine("M5");

            result.GCode = sb.ToString();
            return result;
        }

        private static void AppendTrochoidLoop(
            StringBuilder sb,
            IReadOnlyList<TrochoidEngine.Point2D> loop,
            double toolRadius,
            double stepAe,
            double safeZ,
            double zProgram,
            double feed,
            double plungeFeed,
            OperationProgramResult stats,
            ref int circles,
            int circleBudget)
        {
            if (loop.Count < 3) return;

            bool firstCircle = true;

            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];

                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 1e-4) continue;

                double ux = dx / length;
                double uy = dy / length;
                double nx = -uy;
                double ny = ux;

                int steps = Math.Max(1, (int)Math.Ceiling(length / stepAe));
                double actualStep = length / steps;

                for (int s = 0; s <= steps; s++)
                {
                    if (circles >= circleBudget)
                    {
                        stats.IsTruncated = true;
                        return;
                    }
                    circles++;

                    double t = Math.Min(s * actualStep, length);
                    double cx = a.X + ux * t;
                    double cy = a.Y + uy * t;
                    double entryX = cx + nx * toolRadius;
                    double entryY = cy + ny * toolRadius;

                    if (firstCircle)
                    {
                        sb.AppendLine($"G0 Z{Num(safeZ)}");
                        sb.AppendLine($"G0 X{Num(entryX)} Y{Num(entryY)}");
                        sb.AppendLine($"G1 Z{Num(zProgram)} F{Num(plungeFeed)}");
                        stats.CuttingLengthMm += Math.Abs(safeZ - zProgram);
                        firstCircle = false;
                    }
                    else
                    {
                        sb.AppendLine($"G1 X{Num(entryX)} Y{Num(entryY)} F{Num(feed)}");
                    }

                    // Полная окружность G2 вокруг центра траектории: I/J — смещение центра от точки входа.
                    sb.AppendLine($"G2 X{Num(entryX)} Y{Num(entryY)} I{Num(-nx * toolRadius)} J{Num(-ny * toolRadius)} F{Num(feed)}");
                    stats.CuttingLengthMm += Math.PI * 2.0 * toolRadius + actualStep;
                }
            }

            if (!firstCircle)
            {
                sb.AppendLine($"G0 Z{Num(safeZ)}");
                stats.RapidLengthMm += Math.Abs(safeZ - zProgram);
            }
        }

        private static string Num(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
    }
}
