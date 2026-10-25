using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KompasTrochoidCam.Models;

namespace KompasTrochoidCam.Services
{
    /// <summary>Итог сборки управляющей программы.</summary>
    public sealed class MachiningProgramResult
    {
        public string GCode { get; set; } = string.Empty;
        public double CuttingLengthMm { get; set; }
        public double RapidLengthMm { get; set; }
        public double EstimatedSeconds { get; set; }
        public List<string> Warnings { get; } = new List<string>();
        public int OperationCount { get; set; }

        public string TimeText
        {
            get
            {
                var time = TimeSpan.FromSeconds(EstimatedSeconds);
                return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
            }
        }
    }

    /// <summary>Сборка многооперационной УП: каждая операция — блок со своим инструментом, S и F.</summary>
    public static class MachiningProgramBuilder
    {
        public static MachiningProgramResult Build(MachiningPlan plan, MachiningRegion region)
        {
            var result = new MachiningProgramResult();
            var sb = new StringBuilder();

            sb.AppendLine("%");
            sb.AppendLine("( TrochoidCam — 2.5D трохоидальная обработка по Z-уровням )");
            sb.AppendLine($"( План: {Sanitize(plan.Name)} )");
            sb.AppendLine($"( Станок: {Sanitize(plan.CoordinateSystem.MachineName)} · ЛСК: {Sanitize(plan.CoordinateSystem.LocalCsName)} )");
            sb.AppendLine($"( Заготовка: {plan.Stock.SizeX:F1} x {plan.Stock.SizeY:F1} x {plan.Stock.SizeZ:F1} мм )");
            sb.AppendLine("( Z отсчитывается от верха заготовки, Z0 = верх )");
            sb.AppendLine("G21 G90 G40 G17");
            sb.AppendLine(plan.CoordinateSystem.WorkOffset);
            sb.AppendLine("G0 Z50.000");

            int index = 0;
            foreach (var op in plan.Operations.Where(o => o.IsEnabled))
            {
                index++;
                var tool = plan.FindTool(op.ToolNumber);
                if (tool == null)
                {
                    result.Warnings.Add($"операция «{op.Name}»: не найден инструмент T{op.ToolNumber}");
                    continue;
                }

                var opResult = ZLevelTrochoidStrategy.Build(op, tool, plan, region, index);
                foreach (var warning in opResult.Warnings)
                    result.Warnings.Add($"операция «{op.Name}»: {warning}");

                if (opResult.IsEmpty) continue;

                sb.Append(opResult.GCode);
                result.CuttingLengthMm += opResult.CuttingLengthMm;
                result.RapidLengthMm += opResult.RapidLengthMm;
                result.OperationCount++;
            }

            sb.AppendLine("G0 Z50.000");
            sb.AppendLine("M9");
            sb.AppendLine("M5");
            sb.AppendLine("M30");
            sb.AppendLine("%");

            result.GCode = sb.ToString();
            result.EstimatedSeconds = result.CuttingLengthMm / AverageFeed(plan) * 60.0
                                     + result.RapidLengthMm / 5000.0 * 60.0;

            return result;
        }

        /// <summary>УП только для одной операции (для предпросмотра выбранной операции).</summary>
        public static MachiningProgramResult BuildForOperation(MachiningPlan plan, MachiningOperation op, MachiningRegion region)
        {
            var result = new MachiningProgramResult();
            var tool = plan.FindTool(op.ToolNumber);

            if (tool == null)
            {
                result.Warnings.Add($"не найден инструмент T{op.ToolNumber}");
                return result;
            }

            // Предпросмотр: один уровень (дно обработки) с ограничением по числу петель.
            var opResult = ZLevelTrochoidStrategy.Build(op, tool, plan, region, 1, circleBudget: 4500, maxLevels: 1);
            result.Warnings.AddRange(opResult.Warnings);
            if (opResult.IsTruncated) result.Warnings.Add("предпросмотр сокращён");

            if (opResult.IsEmpty) return result;

            result.GCode = opResult.GCode;
            result.CuttingLengthMm = opResult.CuttingLengthMm;
            result.RapidLengthMm = opResult.RapidLengthMm;
            result.OperationCount = 1;
            result.EstimatedSeconds = opResult.CuttingLengthMm / Math.Max(op.Feed, 1) * 60.0
                                     + opResult.RapidLengthMm / 5000.0 * 60.0;
            return result;
        }

        private static double AverageFeed(MachiningPlan plan)
        {
            double feedSum = 0;
            int count = 0;
            foreach (var op in plan.Operations.Where(o => o.IsEnabled))
            {
                if (plan.FindTool(op.ToolNumber) == null) continue;
                feedSum += Math.Max(op.Feed, 1);
                count++;
            }
            return count > 0 ? feedSum / count : 400;
        }

        private static string Sanitize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "-";
            return text.Replace("(", "[").Replace(")", "]");
        }
    }
}
