using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KompasTrochoidCam.Models;
using KompasTrochoidCam.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace KompasTrochoidCam.ViewModels
{
    /// <summary>Главное окно: геометрия детали, дерево операций и предпросмотр траектории.</summary>
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly KompasService _kompasService;
        private readonly List<ToolpathPoint> _previewPoints = new List<ToolpathPoint>();
        private string _lastProgram = string.Empty;
        private bool _suspendRegionSync;
        private DispatcherTimer? _previewTimer;

        public MachiningTreeViewModel Tree { get; }

        public MachiningPlan Plan => Tree.Plan;

        public event Action? SceneChanged;
        public event Action? FitRequested;

        public MainWindowViewModel()
        {
            _kompasService = new KompasService();

            var geometry = DemoGeometryFactory.Create();
            var plan = new MachiningPlan();
            plan.ApplyGeometryBounds(geometry);
            plan.Tools.Add(new ToolDefinition
            {
                Number = 1,
                Name = "Фреза ГОСТ 17025",
                TypeName = "Фреза ГОСТ 17025",
                Diameter = 4
            });
            plan.Tools.Add(new ToolDefinition
            {
                Number = 2,
                Name = "Фреза ГОСТ 17025",
                TypeName = "Фреза ГОСТ 17025",
                Diameter = 6
            });
            plan.Tools.Add(new ToolDefinition
            {
                Number = 3,
                Name = "Фреза нестандартная",
                TypeName = "Фреза нестандартная",
                Diameter = 5.5
            });

            // Демонстрационный план как на скриншоте КОМПАС: три операции с разными инструментами.
            plan.Operations.Add(CreateDemoOperation("Фрезерование d5.5", 3, plan));
            plan.Operations.Add(CreateDemoOperation("Фрезерование d4", 1, plan));
            plan.Operations.Add(CreateDemoOperation("Фрезерование d6", 2, plan));

            Tree = new MachiningTreeViewModel(plan);
            Tree.PlanChanged += OnPlanChanged;
            Tree.SelectionChanged += OnTreeSelectionChanged;
            Tree.StatusMessage += message => SetStatus(message, false);

            LoadPostprocessors();
            ApplyGeometry(geometry, updateStock: true, keepSelection: false);

            var firstOperation = Tree.Rows.FirstOrDefault(row => row.Payload is MachiningOperation);
            if (firstOperation != null) Tree.SelectedNode = firstOperation;
        }

        private static MachiningOperation CreateDemoOperation(string name, int toolNumber, MachiningPlan plan)
        {
            var tool = plan.FindTool(toolNumber);
            double diameter = tool?.Diameter ?? 10;

            return new MachiningOperation
            {
                Name = name,
                ToolNumber = toolNumber,
                SpindleSpeed = 10000,
                Feed = 1000,
                Correction = 0,
                StepAe = 1.0,
                StepZ = 2.0,
                ZTop = plan.Stock.TopZ,
                ZBottom = plan.Stock.TopZ - 10.0,
                Allowance = 0.2,
                SafeZ = 50,
                RadialStep = Math.Max(1.0, diameter * 0.4)
            };
        }

        // ---------- Статус ----------

        [ObservableProperty]
        private string _connectionStatus = "Нет подключения к КОМПАС-3D";

        [ObservableProperty]
        private IBrush _statusColor = Brushes.Orange;

        [ObservableProperty]
        private string _geometryStatus = "Геометрия не загружена";

        [ObservableProperty]
        private string _regionSummary = "Область обработки не задана";

        [ObservableProperty]
        private string _programSummary = "УП не сформирована";

        [ObservableProperty]
        private string _previewSummary = "Операция не выбрана";

        [ObservableProperty]
        private string _planSummary = string.Empty;

        [ObservableProperty]
        private PartGeometry? _geometry;

        [ObservableProperty]
        private MachiningRegion? _region;

        public ObservableCollection<GeometryItem> FaceItems { get; } = new ObservableCollection<GeometryItem>();

        public ObservableCollection<GeometryItem> EdgeItems { get; } = new ObservableCollection<GeometryItem>();

        public IReadOnlyList<ToolpathPoint> PreviewPoints => _previewPoints;

        public string DocumentTitle => Geometry == null ? "Деталь не загружена" : Geometry.DocumentName;

        // ---------- Постпроцессоры и импорт ----------

        public ObservableCollection<string> AvailablePosts { get; } = new ObservableCollection<string>();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPostSelected))]
        private string? _selectedPost;

        public bool IsPostSelected => !string.IsNullOrEmpty(SelectedPost);

        private void LoadPostprocessors()
        {
            try
            {
                AvailablePosts.Clear();
                string postPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "POSTPROCESSORS");

                if (Directory.Exists(postPath))
                {
                    foreach (var file in Directory.GetFiles(postPath, "*.*"))
                        AvailablePosts.Add(Path.GetFileName(file));
                }
                else
                {
                    Directory.CreateDirectory(postPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка чтения папки POSTPROCESSORS: {ex.Message}");
            }
        }

        // ---------- Геометрия ----------

        [RelayCommand]
        private void Connect()
        {
            bool connected = _kompasService.Connect(out string message);
            ConnectionStatus = message;
            StatusColor = connected ? Brushes.Green : Brushes.Red;

            if (connected) RefreshGeometry();
        }

        [RelayCommand]
        private void RefreshGeometry()
        {
            if (!_kompasService.IsConnected)
            {
                ApplyGeometry(DemoGeometryFactory.Create(), updateStock: true, keepSelection: false);
                SetStatus("КОМПАС-3D не подключён — показана демо-деталь", false);
                return;
            }

            var geometry = _kompasService.ReadPartGeometry(out string message);
            if (geometry.IsEmpty)
            {
                SetStatus(message, true);
                return;
            }

            ApplyGeometry(geometry, updateStock: true, keepSelection: true);
            SetStatus($"{message} · документ: {geometry.DocumentName}", false);
        }

        [RelayCommand]
        private void LoadDemoGeometry()
        {
            ApplyGeometry(DemoGeometryFactory.Create(), updateStock: true, keepSelection: false);
            SetStatus("Загружена демо-деталь", false);
        }

        private void ApplyGeometry(PartGeometry geometry, bool updateStock, bool keepSelection)
        {
            Geometry = geometry;
            OnPropertyChanged(nameof(DocumentTitle));

            if (updateStock) Plan.ApplyGeometryBounds(geometry);

            string source = geometry.Source == "kompas" ? "КОМПАС-3D" : "демо";
            GeometryStatus = $"{geometry.DocumentName} · {geometry.SizeText} · граней: {geometry.Faces.Count}, рёбер: {geometry.Edges.Count} · источник: {source}";

            RebuildGeometryItems(keepSelection);
            SyncRegion();
            FitRequested?.Invoke();
        }

        private void RebuildGeometryItems(bool keepSelection)
        {
            var selectedFaces = keepSelection ? new HashSet<int>(Plan.SelectedFaceIds) : new HashSet<int>();
            var selectedEdges = keepSelection ? new HashSet<int>(Plan.SelectedEdgeIds) : new HashSet<int>();

            _suspendRegionSync = true;
            try
            {
                foreach (var item in FaceItems) item.PropertyChanged -= OnGeometryItemChanged;
                foreach (var item in EdgeItems) item.PropertyChanged -= OnGeometryItemChanged;

                FaceItems.Clear();
                EdgeItems.Clear();

                if (Geometry != null)
                {
                    foreach (var face in Geometry.Faces)
                    {
                        var item = new GeometryItem(face.Id, face.DisplayName, $"{face.Area:N0} мм²", selectedFaces.Contains(face.Id));
                        item.PropertyChanged += OnGeometryItemChanged;
                        FaceItems.Add(item);
                    }

                    foreach (var edge in Geometry.Edges)
                    {
                        string details = edge.IsArc ? $"дуга R{edge.Radius:F2}" : "отрезок";
                        var item = new GeometryItem(edge.Id, edge.Name, $"{details} · Z={edge.Z:F2}", selectedEdges.Contains(edge.Id));
                        item.PropertyChanged += OnGeometryItemChanged;
                        EdgeItems.Add(item);
                    }
                }
            }
            finally
            {
                _suspendRegionSync = false;
            }

            if (!keepSelection || (Plan.SelectedFaceIds.Count == 0 && Plan.SelectedEdgeIds.Count == 0))
                SelectDefaultRegion();
        }

        private void SelectDefaultRegion()
        {
            if (Geometry == null) return;

            var candidates = Geometry.Faces
                .Where(f => f.IsHorizontal && f.NormalZ > 0.9 && f.Loops.Count > 0)
                .ToList();

            if (candidates.Count == 0)
                candidates = Geometry.Faces.Where(f => f.IsHorizontal && f.Loops.Count > 0).ToList();

            if (candidates.Count == 0) return;

            var lowerFace = candidates
                .Where(f => f.Z < Geometry.MaxZ - 0.01)
                .OrderBy(f => f.Z)
                .FirstOrDefault();

            var target = lowerFace ?? candidates.OrderByDescending(f => f.Area).First();
            var item = FaceItems.FirstOrDefault(i => i.Id == target.Id);
            if (item != null) item.IsSelected = true;
        }

        private void OnGeometryItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(GeometryItem.IsSelected) || _suspendRegionSync) return;
            SyncRegion();
        }

        [RelayCommand]
        private void SelectAllFaces()
        {
            _suspendRegionSync = true;
            foreach (var item in FaceItems) item.IsSelected = true;
            _suspendRegionSync = false;
            SyncRegion();
        }

        [RelayCommand]
        private void ClearRegionSelection()
        {
            _suspendRegionSync = true;
            foreach (var item in FaceItems) item.IsSelected = false;
            foreach (var item in EdgeItems) item.IsSelected = false;
            _suspendRegionSync = false;
            SyncRegion();
        }

        [RelayCommand]
        private void ApplyFaceZ()
        {
            var operation = Tree.SelectedOperation;
            var faceItem = FaceItems.FirstOrDefault(i => i.IsSelected);

            if (operation == null || faceItem == null || Geometry == null)
            {
                SetStatus("Выберите операцию в дереве и грань области обработки", true);
                return;
            }

            var face = Geometry.Faces.FirstOrDefault(f => f.Id == faceItem.Id);
            if (face == null) return;

            operation.ZBottom = face.Z;
            if (operation.ZTop <= operation.ZBottom) operation.ZTop = Math.Min(Plan.Stock.TopZ, operation.ZBottom + 1);

            SetStatus($"Z дна операции «{operation.Name}» = {face.Z:F3} мм (грань #{face.Id})", false);
            SyncRegion();
        }

        // ---------- Область обработки и предпросмотр ----------

        private void SyncRegion()
        {
            if (_suspendRegionSync) return;

            var faceIds = FaceItems.Where(i => i.IsSelected).Select(i => i.Id).ToList();
            var edgeIds = EdgeItems.Where(i => i.IsSelected).Select(i => i.Id).ToList();

            Plan.SelectedFaceIds = faceIds;
            Plan.SelectedEdgeIds = edgeIds;

            var operation = Tree.SelectedOperation ?? Plan.Operations.FirstOrDefault();
            double zTop = operation?.ZTop ?? Plan.Stock.TopZ;
            double zBottom = operation?.ZBottom ?? Plan.Stock.TopZ - 5;

            Region = RegionCalculator.Build(Geometry, faceIds, edgeIds, zTop, zBottom);

            string suffix = faceIds.Count + edgeIds.Count == 0 ? " · авто по нижней грани" : string.Empty;
            RegionSummary = Region.Summary + suffix;

            Tree.SetRegionSummary(Region.HasArea ? Region.Summary : "Выберите грани и рёбра в правой панели");
            _lastProgram = string.Empty;
            SchedulePreview();
        }

        /// <summary>Отложенный пересчёт предпросмотра, чтобы не тормозить ввод в полях.</summary>
        private void SchedulePreview()
        {
            if (_previewTimer == null)
            {
                _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _previewTimer.Tick += (_, _) =>
                {
                    _previewTimer?.Stop();
                    UpdatePreview();
                };
            }

            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void UpdatePreview()
        {
            _previewPoints.Clear();
            PreviewSummary = "Операция не выбрана";

            var operation = Tree.SelectedOperation;
            var region = Region;
            if (operation == null || region == null || !region.HasArea)
            {
                SceneChanged?.Invoke();
                return;
            }

            var result = MachiningProgramBuilder.BuildForOperation(Plan, operation, region);
            if (!string.IsNullOrEmpty(result.GCode))
            {
                var parsed = GCodeParser.Parse(result.GCode);
                double zOffset = Plan.Stock.TopZ;

                foreach (var point in parsed.Points)
                    _previewPoints.Add(new ToolpathPoint(point.X, point.Y, point.Z + zOffset, point.LineIndex, point.Rapid));

                string truncated = result.Warnings.Count > 0 ? " · сокращён" : string.Empty;
                PreviewSummary = $"{operation.Name}: предпросмотр 1-го уровня, {_previewPoints.Count} точек · {result.CuttingLengthMm / 1000.0:F1} м резания · ~{FormatTime(result.EstimatedSeconds)}{truncated}";
            }
            else if (result.Warnings.Count > 0)
            {
                PreviewSummary = $"{operation.Name}: {string.Join("; ", result.Warnings)}";
            }

            SceneChanged?.Invoke();
        }

        private void OnTreeSelectionChanged()
        {
            SyncRegion();
        }

        private void OnPlanChanged()
        {
            PlanSummary = $"Операций: {Plan.Operations.Count} · инструментов: {Plan.Tools.Count} · заготовка {Plan.Stock.SizeText}";
            SyncRegion();
        }

        // ---------- Управляющая программа ----------

        [RelayCommand]
        private async Task GenerateProgram()
        {
            var region = Region;
            if (region == null || !region.HasArea)
            {
                SetStatus("Область обработки не задана: выберите грани или рёбра", true);
                return;
            }

            if (Plan.Operations.Count == 0)
            {
                SetStatus("В плане нет операций фрезерования", true);
                return;
            }

            var result = MachiningProgramBuilder.Build(Plan, region);
            if (result.OperationCount == 0)
            {
                string warnings = result.Warnings.Count > 0 ? string.Join("; ", result.Warnings) : "нет допустимых операций";
                SetStatus($"Не удалось сформировать УП: {warnings}", true);
                return;
            }

            _lastProgram = result.GCode;
            ProgramSummary = $"УП: операций {result.OperationCount} · резание {result.CuttingLengthMm / 1000.0:F1} м · ~{result.TimeText}";

            string? path = await PickSavePath("Сохранить управляющую программу", "trochoid_output.ngc", "ngc", "Управляющие программы");
            if (path != null)
            {
                try
                {
                    File.WriteAllText(path, result.GCode);
                    SetStatus($"УП сохранена: {Path.GetFileName(path)}", false);
                }
                catch (Exception ex)
                {
                    SetStatus($"Ошибка записи УП: {ex.Message}", true);
                }
            }
            else
            {
                SetStatus("УП сформирована (файл не сохранён)", false);
            }

            ShowSimulator();
        }

        [RelayCommand]
        private void ShowSimulator()
        {
            string program = _lastProgram;

            if (string.IsNullOrWhiteSpace(program))
            {
                var region = Region;
                if (region != null && region.HasArea && Plan.Operations.Count > 0)
                {
                    program = MachiningProgramBuilder.Build(Plan, region).GCode;
                }
            }

            if (string.IsNullOrWhiteSpace(program))
            {
                SetStatus("Нет управляющей программы: сначала сформируйте УП", true);
                return;
            }

            var viewModel = new GCodeWindowViewModel { GCodeText = program };
            var window = new Views.GCodeWindow { DataContext = viewModel };

            var operation = Tree.SelectedOperation ?? Plan.Operations.FirstOrDefault();
            var tool = operation == null ? null : Plan.FindTool(operation.ToolNumber);
            window.SetToolDiameter(tool?.Diameter ?? 10.0);

            window.ParseAndBuildTrajectory(program);
            window.Show();
        }

        // ---------- Проект ----------

        [RelayCommand]
        private async Task SavePlan()
        {
            string? path = await PickSavePath("Сохранить проект TrochoidCam", "trochoid_plan.tcplan", "tcplan", "Проект TrochoidCam");
            if (path == null)
            {
                SetStatus("Сохранение проекта отменено", false);
                return;
            }

            try
            {
                PlanFileService.Save(new MachiningProject { Plan = Plan, Geometry = Geometry }, path);
                SetStatus($"Проект сохранён: {Path.GetFileName(path)}", false);
            }
            catch (Exception ex)
            {
                SetStatus($"Ошибка сохранения проекта: {ex.Message}", true);
            }
        }

        [RelayCommand]
        private async Task OpenPlan()
        {
            var topLevel = GetTopLevel();
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Открыть проект TrochoidCam",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Проект TrochoidCam") { Patterns = new[] { "*.tcplan", "*.json" } }
                }
            });

            if (files == null || files.Count == 0) return;

            string? path = files[0].TryGetLocalPath();
            if (path == null)
            {
                SetStatus("Не удалось получить путь к файлу проекта", true);
                return;
            }

            try
            {
                var project = PlanFileService.Load(path);

                _suspendRegionSync = true;
                try
                {
                    Tree.SetPlan(project.Plan);
                }
                finally
                {
                    _suspendRegionSync = false;
                }

                OnPropertyChanged(nameof(Plan));

                var geometry = project.Geometry ?? DemoGeometryFactory.Create();
                ApplyGeometry(geometry, updateStock: false, keepSelection: true);

                SetStatus($"Проект загружен: {Path.GetFileName(path)}", false);
            }
            catch (Exception ex)
            {
                SetStatus($"Ошибка загрузки проекта: {ex.Message}", true);
            }
        }

        [RelayCommand]
        private async Task OpenGCodeFile()
        {
            if (string.IsNullOrEmpty(SelectedPost)) return;

            var topLevel = GetTopLevel();
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"Импорт файла через постпроцессор: {SelectedPost}",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Файлы ЧПУ") { Patterns = new[] { "*.nc", "*.ngc", "*.tap", "*.gcode" } }
                }
            });

            if (files == null || files.Count == 0) return;

            var selectedFile = files[0];
            string? path = selectedFile.TryGetLocalPath();

            try
            {
                string content;
                if (path != null)
                {
                    content = File.ReadAllText(path);
                }
                else
                {
                    await using var stream = await selectedFile.OpenReadAsync();
                    using var reader = new StreamReader(stream);
                    content = await reader.ReadToEndAsync();
                }

                var viewModel = new GCodeWindowViewModel { GCodeText = content };
                var window = new Views.GCodeWindow { DataContext = viewModel };
                window.ParseAndBuildTrajectory(content);
                window.Show();

                SetStatus($"Код {selectedFile.Name} импортирован через {SelectedPost}", false);
            }
            catch (Exception ex)
            {
                SetStatus($"Ошибка импорта файла: {ex.Message}", true);
            }
        }

        // ---------- Вспомогательное ----------

        private static TopLevel? GetTopLevel()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;
            return null;
        }

        private async Task<string?> PickSavePath(string title, string suggestedName, string extension, string typeName)
        {
            var topLevel = GetTopLevel();
            if (topLevel == null)
            {
                string folder = Path.Combine(AppContext.BaseDirectory, "g-cod");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, suggestedName);
            }

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                DefaultExtension = extension,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(typeName) { Patterns = new[] { $"*.{extension}" } }
                }
            });

            return file?.TryGetLocalPath();
        }

        private void SetStatus(string message, bool isError)
        {
            ConnectionStatus = message;
            StatusColor = isError ? Brushes.Red : Brushes.Green;
        }

        private static string FormatTime(double seconds)
        {
            var time = TimeSpan.FromSeconds(seconds);
            return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        }
    }
}
