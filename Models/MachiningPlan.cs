using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KompasTrochoidCam.Models
{
    /// <summary>Инструмент (позиция в магазине станка).</summary>
    public sealed partial class ToolDefinition : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        [NotifyPropertyChangedFor(nameof(Caption))]
        private int _number = 1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        [NotifyPropertyChangedFor(nameof(Caption))]
        private string _name = "Фреза";

        /// <summary>Тип инструмента, например "Фреза ГОСТ 17025" или "Фреза нестандартная".</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Caption))]
        private string _typeName = "Фреза ГОСТ 17025";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Caption))]
        private double _diameter = 10.0;

        /// <summary>Длина рабочей части, мм (справочно).</summary>
        [ObservableProperty]
        private double _length = 40.0;

        [JsonIgnore]
        public string DisplayName => $"T{Number} — {Name}";

        [JsonIgnore]
        public string Caption => $"T{Number} · Ø{Diameter:F1} · {TypeName}";
    }

    /// <summary>Операция фрезерования, привязанная к инструменту.</summary>
    public sealed partial class MachiningOperation : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        private string _name = "Фрезерование";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        private int _toolNumber = 1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        private double _spindleSpeed = 10000;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        private double _feed = 1000;

        /// <summary>Коррекция радиуса инструмента r, мм.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        private double _correction = 0;

        [ObservableProperty]
        private bool _isEnabled = true;

        /// <summary>Шаг трохоиды Ae, мм.</summary>
        [ObservableProperty]
        private double _stepAe = 1.0;

        /// <summary>Шаг по глубине ΔZ, мм.</summary>
        [ObservableProperty]
        private double _stepZ = 2.0;

        /// <summary>Верх обработки (абсолютная Z детали), мм.</summary>
        [ObservableProperty]
        private double _zTop = 20.0;

        /// <summary>Дно обработки (абсолютная Z детали), мм.</summary>
        [ObservableProperty]
        private double _zBottom = 10.0;

        /// <summary>Припуск на стенку, мм.</summary>
        [ObservableProperty]
        private double _allowance = 0.2;

        /// <summary>Безопасная высота (программная Z от верха заготовки), мм.</summary>
        [ObservableProperty]
        private double _safeZ = 50.0;

        /// <summary>Радиальный шаг между проходами очистки, мм.</summary>
        [ObservableProperty]
        private double _radialStep = 4.0;

        /// <summary>Стратегия обработки (пока одна — трохоида по Z-уровням).</summary>
        [ObservableProperty]
        private string _strategy = "Трохоидальная по Z-уровням";

        /// <summary>Подача врезания, мм/мин.</summary>
        [ObservableProperty]
        private double _plungeFeed = 50;

        [JsonIgnore]
        public string Summary => $"T{ToolNumber} · S={SpindleSpeed:F0} · F={Feed:F0} · r={Correction:F2}";

        /// <summary>Глубина обработки в мм (положительная).</summary>
        [JsonIgnore]
        public double Depth => Math.Max(0, ZTop - ZBottom);
    }

    public sealed partial class CoordinateSystemSettings : ObservableObject
    {
        /// <summary>Имя конфигурации станка, например IvanHAAS.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        private string _machineName = "IvanHAAS";

        /// <summary>Имя локальной системы координат обработки, например ЛСК3.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        private string _localCsName = "ЛСК3";

        /// <summary>Рабочая система координат в УП.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        private string _workOffset = "G54";

        [ObservableProperty]
        private double _originX;

        [ObservableProperty]
        private double _originY;

        [ObservableProperty]
        private double _originZ;

        [JsonIgnore]
        public string Summary => $"{MachineName} · {LocalCsName} · {WorkOffset}";
    }

    /// <summary>Заготовка в абсолютных координатах детали.</summary>
    public sealed partial class StockDefinition : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SizeX))]
        [NotifyPropertyChangedFor(nameof(SizeText))]
        private double _minX = -55.0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SizeX))]
        [NotifyPropertyChangedFor(nameof(SizeText))]
        private double _maxX = 55.0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SizeY))]
        [NotifyPropertyChangedFor(nameof(SizeText))]
        private double _minY = -70.0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SizeY))]
        [NotifyPropertyChangedFor(nameof(SizeText))]
        private double _maxY = 70.0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SizeZ))]
        [NotifyPropertyChangedFor(nameof(SizeText))]
        private double _topZ = 20.0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SizeZ))]
        [NotifyPropertyChangedFor(nameof(SizeText))]
        private double _bottomZ = 0.0;

        /// <summary>Припуск заготовки по контуру, мм (для пересчёта от геометрии).</summary>
        [ObservableProperty]
        private double _margin = 5.0;

        [JsonIgnore]
        public double SizeX => MaxX - MinX;

        [JsonIgnore]
        public double SizeY => MaxY - MinY;

        [JsonIgnore]
        public double SizeZ => TopZ - BottomZ;

        [JsonIgnore]
        public string SizeText => $"{SizeX:F1} × {SizeY:F1} × {SizeZ:F1} мм";
    }

    /// <summary>План фрезерной обработки: система координат, заготовка, инструменты и операции.</summary>
    public sealed class MachiningPlan
    {
        public string Name { get; set; } = "План фрезерной обработки";
        public CoordinateSystemSettings CoordinateSystem { get; set; } = new CoordinateSystemSettings();
        public StockDefinition Stock { get; set; } = new StockDefinition();
        public List<ToolDefinition> Tools { get; set; } = new List<ToolDefinition>();
        public List<MachiningOperation> Operations { get; set; } = new List<MachiningOperation>();

        /// <summary>Грани, ограничивающие область обработки.</summary>
        public List<int> SelectedFaceIds { get; set; } = new List<int>();

        /// <summary>Рёбра, ограничивающие область обработки.</summary>
        public List<int> SelectedEdgeIds { get; set; } = new List<int>();

        public ToolDefinition? FindTool(int number) => Tools.FirstOrDefault(t => t.Number == number);

        public int NextToolNumber() => Tools.Count == 0 ? 1 : Tools.Max(t => t.Number) + 1;

        public int NextOperationNumber() => Operations.Count + 1;

        /// <summary>Пересчёт заготовки по габаритам детали с припуском.</summary>
        public void ApplyGeometryBounds(PartGeometry geometry, double? margin = null)
        {
            double m = margin ?? Stock.Margin;
            Stock.MinX = geometry.MinX - m;
            Stock.MaxX = geometry.MaxX + m;
            Stock.MinY = geometry.MinY - m;
            Stock.MaxY = geometry.MaxY + m;
            Stock.BottomZ = geometry.MinZ;
            Stock.TopZ = geometry.MaxZ;
        }
    }

    /// <summary>Файл проекта: план + геометрия, чтобы дерево и модель открывались вместе.</summary>
    public sealed class MachiningProject
    {
        public int Version { get; set; } = 1;
        public MachiningPlan Plan { get; set; } = new MachiningPlan();
        public PartGeometry? Geometry { get; set; }
    }
}
