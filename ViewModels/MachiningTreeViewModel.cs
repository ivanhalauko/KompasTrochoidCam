using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KompasTrochoidCam.Models;

namespace KompasTrochoidCam.ViewModels
{
    public enum PlanNodeKind
    {
        Plan,
        CoordinateSystems,
        CoordinateSystemItem,
        StockTools,
        StockItem,
        ToolsGroup,
        Tool,
        Region,
        Operation
    }

    /// <summary>Узел дерева плана фрезерной обработки.</summary>
    public sealed partial class PlanTreeNode : ObservableObject
    {
        private readonly PropertyChangedEventHandler? _payloadHandler;
        private readonly List<PlanTreeNode> _children = new List<PlanTreeNode>();

        public MachiningTreeViewModel Owner { get; }
        public PlanNodeKind Kind { get; }
        public int Depth { get; }
        public object? Payload { get; }
        public Func<(string Title, string Subtitle)>? TextFactory { get; set; }

        [ObservableProperty]
        private string _title = string.Empty;

        [ObservableProperty]
        private string _subtitle = string.Empty;

        [ObservableProperty]
        private bool _isExpanded = true;

        [ObservableProperty]
        private bool _isEditing;

        [ObservableProperty]
        private string _editName = string.Empty;

        public IReadOnlyList<PlanTreeNode> Children => _children;

        public bool HasChildren => _children.Count > 0;
        public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
        public string ExpandGlyph => !HasChildren ? "·" : (IsExpanded ? "▾" : "▸");
        public Thickness IndentMargin => new Thickness(Depth * 14, 0, 0, 0);
        public FontWeight TitleWeight => Kind == PlanNodeKind.Operation || Kind == PlanNodeKind.Tool
            ? FontWeight.Normal
            : FontWeight.SemiBold;

        public PlanTreeNode(MachiningTreeViewModel owner, PlanNodeKind kind, int depth, object? payload)
        {
            Owner = owner;
            Kind = kind;
            Depth = depth;
            Payload = payload;

            if (payload is ObservableObject observable)
            {
                _payloadHandler = (_, _) => Owner.OnPayloadChanged(this);
                observable.PropertyChanged += _payloadHandler;
            }
        }

        public void SetChildren(IEnumerable<PlanTreeNode> children)
        {
            _children.Clear();
            _children.AddRange(children);
            OnPropertyChanged(nameof(HasChildren));
            OnPropertyChanged(nameof(ExpandGlyph));
        }

        public void RefreshText()
        {
            if (TextFactory == null) return;
            var (title, subtitle) = TextFactory();
            Title = title;
            Subtitle = subtitle;
        }

        public void Detach()
        {
            if (_payloadHandler != null && Payload is ObservableObject observable)
                observable.PropertyChanged -= _payloadHandler;

            foreach (var child in _children) child.Detach();
        }

        partial void OnIsExpandedChanged(bool value)
        {
            OnPropertyChanged(nameof(ExpandGlyph));
            Owner.OnNodeExpansionChanged(this);
        }

        partial void OnSubtitleChanged(string value) => OnPropertyChanged(nameof(HasSubtitle));

        partial void OnIsEditingChanged(bool value)
        {
            if (value) EditName = EditableName;
        }

        partial void OnEditNameChanged(string value) => Owner.ApplyRename(this, value);

        /// <summary>Имя, которое редактируется в строке дерева (без префикса номера).</summary>
        public string EditableName => Payload switch
        {
            MachiningOperation operation => operation.Name,
            ToolDefinition tool => tool.Name,
            MachiningPlan plan => plan.Name,
            _ => Title
        };
    }

    /// <summary>
    /// Дерево плана: система координат, заготовка и инструменты, область обработки,
    /// операции фрезерования в порядке выполнения.
    /// </summary>
    public partial class MachiningTreeViewModel : ViewModelBase
    {
        private readonly Dictionary<string, bool> _expanded = new Dictionary<string, bool>();
        private PlanTreeNode? _root;
        private PlanTreeNode? _regionNode;
        private bool _isRebuilding;
        private string _regionSummary = "Выберите грани и рёбра в правой панели";

        public MachiningPlan Plan { get; private set; }

        public ObservableCollection<PlanTreeNode> Rows { get; } = new ObservableCollection<PlanTreeNode>();
        public ObservableCollection<ToolDefinition> Tools { get; } = new ObservableCollection<ToolDefinition>();

        [ObservableProperty]
        private PlanTreeNode? _selectedNode;

        public event Action? PlanChanged;
        public event Action<string>? StatusMessage;
        public event Action? SelectionChanged;

        public MachiningTreeViewModel(MachiningPlan plan)
        {
            Plan = plan;
            Rebuild();
        }

        public void SetPlan(MachiningPlan plan)
        {
            _root?.Detach();
            Plan = plan;
            SelectedNode = null;
            Rebuild();
            NotifySelectionChanged();
        }

        // ---------- Построение дерева ----------

        public void Rebuild(object? preservePayload = null)
        {
            _isRebuilding = true;
            try
            {
                _root?.Detach();

                var root = CreateNode(PlanNodeKind.Plan, 0, Plan, "root", true);
                root.TextFactory = () => (Plan.Name, $"Операций: {Plan.Operations.Count} · инструментов: {Plan.Tools.Count}");

                var cs = CreateNode(PlanNodeKind.CoordinateSystems, 1, null, "cs", true);
                cs.TextFactory = () => ("Система координат", Plan.CoordinateSystem.Summary);

                var machine = CreateNode(PlanNodeKind.CoordinateSystemItem, 2, null, null, true);
                machine.TextFactory = () => ($"Станок: {Plan.CoordinateSystem.MachineName}", $"Рабочая СК: {Plan.CoordinateSystem.WorkOffset}");

                var lcs = CreateNode(PlanNodeKind.CoordinateSystemItem, 2, null, null, true);
                lcs.TextFactory = () => ($"ЛСК ЧПУ: {Plan.CoordinateSystem.LocalCsName}", "Локальная система координат обработки");

                cs.SetChildren(new[] { machine, lcs });

                var stockTools = CreateNode(PlanNodeKind.StockTools, 1, null, "stock", true);
                stockTools.TextFactory = () => ("Заготовка, инструменты", $"{Plan.Stock.SizeText} · инструментов: {Plan.Tools.Count}");

                var stockItem = CreateNode(PlanNodeKind.StockItem, 2, Plan.Stock, null, true);
                stockItem.TextFactory = () => ($"Заготовка: {Plan.Stock.SizeText}", "Двойной щелчок — редактирование справа");

                var toolsGroup = CreateNode(PlanNodeKind.ToolsGroup, 2, null, "tools", true);
                toolsGroup.TextFactory = () => ($"Инструменты: {Plan.Tools.Count} поз.", "T-номера не обязаны совпадать с порядком операций");

                var toolNodes = new List<PlanTreeNode>();
                foreach (var tool in Plan.Tools.OrderBy(t => t.Number))
                {
                    var toolNode = CreateNode(PlanNodeKind.Tool, 3, tool, null, true);
                    toolNode.TextFactory = () => ($"T{tool.Number} — {tool.Name}", $"{tool.TypeName} · Ø{tool.Diameter:F2} мм");
                    toolNodes.Add(toolNode);
                }
                toolsGroup.SetChildren(toolNodes);

                stockTools.SetChildren(new[] { stockItem, toolsGroup });

                _regionNode = CreateNode(PlanNodeKind.Region, 1, null, "region", true);
                _regionNode.TextFactory = () => ("Область обработки", _regionSummary);

                var children = new List<PlanTreeNode> { cs, stockTools, _regionNode };

                int index = 0;
                foreach (var op in Plan.Operations)
                {
                    index++;
                    int operationIndex = index;
                    var opNode = CreateNode(PlanNodeKind.Operation, 1, op, null, true);
                    opNode.TextFactory = () => ($"{operationIndex}. {op.Name}", op.Summary);
                    children.Add(opNode);
                }

                root.SetChildren(children);
                foreach (var node in children) node.RefreshText();
                foreach (var node in toolNodes) node.RefreshText();

                root.RefreshText();
                cs.RefreshText();
                machine.RefreshText();
                lcs.RefreshText();
                stockTools.RefreshText();
                stockItem.RefreshText();
                toolsGroup.RefreshText();

                _root = root;
                SyncTools();
            }
            finally
            {
                _isRebuilding = false;
            }

            RebuildRows(preservePayload);
        }

        private PlanTreeNode CreateNode(PlanNodeKind kind, int depth, object? payload, string? expansionKey, bool defaultExpanded)
        {
            bool expanded = expansionKey != null && _expanded.TryGetValue(expansionKey, out bool saved)
                ? saved
                : defaultExpanded;

            return new PlanTreeNode(this, kind, depth, payload) { IsExpanded = expanded };
        }

        private void RebuildRows(object? preservePayload = null)
        {
            if (_isRebuilding) return;

            object? targetPayload = preservePayload ?? SelectedNode?.Payload;

            _isRebuilding = true;
            try
            {
                Rows.Clear();
                if (_root != null) AddVisibleRows(_root);
            }
            finally
            {
                _isRebuilding = false;
            }

            var match = targetPayload == null
                ? null
                : Rows.FirstOrDefault(r => ReferenceEquals(r.Payload, targetPayload));

            if (match != null)
            {
                SelectedNode = match;
            }
            else if (SelectedNode != null && !Rows.Contains(SelectedNode))
            {
                SelectedNode = null;
            }
        }

        private void AddVisibleRows(PlanTreeNode node)
        {
            Rows.Add(node);
            if (!node.IsExpanded) return;
            foreach (var child in node.Children) AddVisibleRows(child);
        }

        public void OnNodeExpansionChanged(PlanTreeNode node)
        {
            if (_isRebuilding || node.Owner != this) return;

            string? key = node.Kind switch
            {
                PlanNodeKind.CoordinateSystems => "cs",
                PlanNodeKind.StockTools => "stock",
                PlanNodeKind.ToolsGroup => "tools",
                PlanNodeKind.Plan => "root",
                _ => null
            };

            if (key != null) _expanded[key] = node.IsExpanded;

            RebuildRows();
        }

        public void OnPayloadChanged(PlanTreeNode node)
        {
            node.RefreshText();
            PlanChanged?.Invoke();
        }

        public void ApplyRename(PlanTreeNode node, string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0) return;

            switch (node.Payload)
            {
                case MachiningOperation operation:
                    if (operation.Name != trimmed) operation.Name = trimmed;
                    break;
                case ToolDefinition tool:
                    if (tool.Name != trimmed) tool.Name = trimmed;
                    break;
                case MachiningPlan plan:
                    if (plan.Name != trimmed) plan.Name = trimmed;
                    node.Title = trimmed;
                    break;
            }
        }

        public void SetRegionSummary(string summary)
        {
            _regionSummary = summary;
            if (_regionNode == null) return;
            _regionNode.Subtitle = summary;
        }

        private void SyncTools()
        {
            for (int i = Tools.Count - 1; i >= 0; i--)
            {
                if (!Plan.Tools.Contains(Tools[i])) Tools.RemoveAt(i);
            }

            for (int i = 0; i < Plan.Tools.Count; i++)
            {
                var tool = Plan.Tools[i];
                if (i >= Tools.Count)
                {
                    Tools.Add(tool);
                }
                else if (!ReferenceEquals(Tools[i], tool))
                {
                    if (Tools.Contains(tool)) Tools.Remove(tool);
                    Tools.Insert(i, tool);
                }
            }
        }

        // ---------- Выбор в дереве ----------

        public MachiningOperation? SelectedOperation => SelectedNode?.Payload as MachiningOperation;

        public ToolDefinition? SelectedTool => SelectedNode?.Payload as ToolDefinition;

        public bool IsOperationSelected => SelectedOperation != null;

        public bool IsToolSelected => SelectedTool != null;

        public bool IsStockSelected => SelectedNode?.Kind == PlanNodeKind.StockItem;

        public bool IsRegionSelected => SelectedNode?.Kind == PlanNodeKind.Region;

        public bool IsNothingSelected => SelectedNode == null;

        public ToolDefinition? SelectedOperationTool
        {
            get => SelectedOperation == null ? null : Plan.FindTool(SelectedOperation.ToolNumber);
            set
            {
                var operation = SelectedOperation;
                if (operation == null || value == null || operation.ToolNumber == value.Number) return;

                operation.ToolNumber = value.Number;
                OnPropertyChanged();
                PlanChanged?.Invoke();
            }
        }

        partial void OnSelectedNodeChanged(PlanTreeNode? value) => NotifySelectionChanged();

        private void NotifySelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedOperation));
            OnPropertyChanged(nameof(SelectedTool));
            OnPropertyChanged(nameof(IsOperationSelected));
            OnPropertyChanged(nameof(IsToolSelected));
            OnPropertyChanged(nameof(IsStockSelected));
            OnPropertyChanged(nameof(IsRegionSelected));
            OnPropertyChanged(nameof(IsNothingSelected));
            OnPropertyChanged(nameof(SelectedOperationTool));
            SelectionChanged?.Invoke();
        }

        // ---------- Команды дерева ----------

        [RelayCommand]
        private void ToggleExpand(PlanTreeNode? node)
        {
            node ??= SelectedNode;
            if (node == null || !node.HasChildren) return;
            node.IsExpanded = !node.IsExpanded;
        }

        [RelayCommand]
        private void AddOperation(PlanTreeNode? node)
        {
            Plan.Tools.Sort((a, b) => a.Number.CompareTo(b.Number));

            var tool = (node?.Payload as ToolDefinition)
                       ?? (SelectedNode?.Payload as ToolDefinition)
                       ?? Plan.Tools.FirstOrDefault();

            if (tool == null)
            {
                tool = new ToolDefinition { Number = 1, Name = "Фреза", Diameter = 10 };
                Plan.Tools.Add(tool);
            }

            var operation = new MachiningOperation
            {
                Name = $"Фрезерование d{tool.Diameter:0.#}",
                ToolNumber = tool.Number,
                SpindleSpeed = 10000,
                Feed = 1000,
                Correction = 0,
                StepAe = 1.0,
                StepZ = 2.0,
                ZTop = Plan.Stock.TopZ,
                ZBottom = Plan.Stock.TopZ - 10.0,
                Allowance = 0.2,
                SafeZ = 50,
                RadialStep = Math.Max(1.0, tool.Diameter * 0.4)
            };

            int index = Plan.Operations.Count;
            if (SelectedNode?.Payload is MachiningOperation current)
            {
                int currentIndex = Plan.Operations.IndexOf(current);
                if (currentIndex >= 0) index = currentIndex + 1;
            }

            Plan.Operations.Insert(index, operation);
            Rebuild(operation);
            StatusMessage?.Invoke($"Добавлена операция «{operation.Name}»");
            PlanChanged?.Invoke();
        }

        [RelayCommand]
        private void AddTool(PlanTreeNode? node)
        {
            var tool = new ToolDefinition
            {
                Number = Plan.NextToolNumber(),
                Name = "Фреза",
                TypeName = "Фреза ГОСТ 17025",
                Diameter = 10,
                Length = 40
            };

            Plan.Tools.Add(tool);
            Plan.Tools.Sort((a, b) => a.Number.CompareTo(b.Number));
            Rebuild(tool);
            StatusMessage?.Invoke($"Добавлен инструмент T{tool.Number}");
            PlanChanged?.Invoke();
        }

        [RelayCommand]
        private void DeleteNode(PlanTreeNode? node)
        {
            node ??= SelectedNode;
            if (node == null) return;

            if (node.Payload is MachiningOperation operation)
            {
                Plan.Operations.Remove(operation);
                Rebuild();
                StatusMessage?.Invoke($"Операция «{operation.Name}» удалена");
                PlanChanged?.Invoke();
                return;
            }

            if (node.Payload is ToolDefinition tool)
            {
                var used = Plan.Operations.FirstOrDefault(o => o.ToolNumber == tool.Number);
                if (used != null)
                {
                    StatusMessage?.Invoke($"Инструмент T{tool.Number} используется в операции «{used.Name}» — сначала смените инструмент");
                    return;
                }

                Plan.Tools.Remove(tool);
                Rebuild();
                StatusMessage?.Invoke($"Инструмент T{tool.Number} удалён");
                PlanChanged?.Invoke();
            }
        }

        [RelayCommand]
        private void MoveUp(PlanTreeNode? node) => MoveOperation(node, -1);

        [RelayCommand]
        private void MoveDown(PlanTreeNode? node) => MoveOperation(node, 1);

        private void MoveOperation(PlanTreeNode? node, int direction)
        {
            var operation = node?.Payload as MachiningOperation ?? SelectedOperation;
            if (operation == null) return;

            int index = Plan.Operations.IndexOf(operation);
            int newIndex = index + direction;
            if (index < 0 || newIndex < 0 || newIndex >= Plan.Operations.Count) return;

            Plan.Operations.RemoveAt(index);
            Plan.Operations.Insert(newIndex, operation);

            Rebuild(operation);
            StatusMessage?.Invoke($"Операция «{operation.Name}» перемещена");
            PlanChanged?.Invoke();
        }

        [RelayCommand]
        private void StartRename(PlanTreeNode? node)
        {
            node ??= SelectedNode;
            if (node == null) return;
            node.IsEditing = true;
        }

        [RelayCommand]
        private void EndRename(PlanTreeNode? node)
        {
            if (node == null) return;
            node.IsEditing = false;
        }
    }
}
