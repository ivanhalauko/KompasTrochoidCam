using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KompasTrochoidCam.Services;
using KompasTrochoidCam.ViewModels;

namespace KompasTrochoidCam.Views;

public partial class MainWindow : Window
{
    private readonly CamSceneContext _scene = new CamSceneContext();
    private readonly PartRenderer _renderer = new PartRenderer();

    private MainWindowViewModel? _viewModel;
    private bool _isRotating;
    private bool _isPanning;
    private bool _needsFit = true;
    private Point _lastMousePosition;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        SizeChanged += (_, _) => RenderScene();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.SceneChanged -= RenderScene;
            _viewModel.FitRequested -= FitView;
        }

        _viewModel = DataContext as MainWindowViewModel;

        if (_viewModel != null)
        {
            _viewModel.SceneChanged += RenderScene;
            _viewModel.FitRequested += FitView;
        }

        _needsFit = true;
        Dispatcher.UIThread.Post(RenderScene, DispatcherPriority.Loaded);
    }

    private void FitView()
    {
        _needsFit = true;
        RenderScene();
    }

    private void RenderScene()
    {
        var canvas = this.FindControl<Canvas>("PartCanvas");
        if (canvas == null || _viewModel == null) return;
        if (canvas.Bounds.Width < 20 || canvas.Bounds.Height < 20) return;

        if (_needsFit)
        {
            var bounds = GetBounds();
            _scene.FitTo(bounds.MinX, bounds.MaxX, bounds.MinY, bounds.MaxY, bounds.MinZ, bounds.MaxZ,
                canvas.Bounds.Width, canvas.Bounds.Height);
            _needsFit = false;
        }

        var options = new PartRenderOptions
        {
            Geometry = _viewModel.Geometry,
            Stock = _viewModel.Plan.Stock,
            Region = _viewModel.Region,
            Trajectory = _viewModel.PreviewPoints,
            SelectedFaceIds = _viewModel.FaceItems.Where(i => i.IsSelected).Select(i => i.Id).ToList(),
            SelectedEdgeIds = _viewModel.EdgeItems.Where(i => i.IsSelected).Select(i => i.Id).ToList(),
            TrajectoryZOffset = 0
        };

        _renderer.Render(canvas, _scene, options);
    }

    private (double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ) GetBounds()
    {
        if (_viewModel?.Geometry is { } geometry && !geometry.IsEmpty)
            return (geometry.MinX, geometry.MaxX, geometry.MinY, geometry.MaxY, geometry.MinZ, geometry.MaxZ);

        var stock = _viewModel?.Plan.Stock;
        if (stock != null)
            return (stock.MinX, stock.MaxX, stock.MinY, stock.MaxY, stock.BottomZ, stock.TopZ);

        return (-50, 50, -50, 50, 0, 20);
    }

    // ---------- Мышь ----------

    private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        _isRotating = properties.IsLeftButtonPressed;
        _isPanning = properties.IsMiddleButtonPressed;
        _lastMousePosition = e.GetPosition(this);
        e.Handled = true;
    }

    private void Canvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isRotating && !_isPanning) return;

        var position = e.GetPosition(this);
        double deltaX = position.X - _lastMousePosition.X;
        double deltaY = position.Y - _lastMousePosition.Y;

        if (_isRotating)
        {
            _scene.RotZ += deltaX * 0.008;
            _scene.RotX -= deltaY * 0.008;
        }
        else
        {
            _scene.OffsetX += deltaX;
            _scene.OffsetY += deltaY;
        }

        _lastMousePosition = position;
        RenderScene();
        e.Handled = true;
    }

    private void Canvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isRotating = false;
        _isPanning = false;
        e.Handled = true;
    }

    private void Canvas_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var canvas = this.FindControl<Canvas>("PartCanvas");
        if (canvas == null) return;

        var position = e.GetPosition(canvas);
        double halfWidth = canvas.Bounds.Width / 2.0;
        double halfHeight = canvas.Bounds.Height / 2.0;

        double oldScale = _scene.Scale;
        double newScale = e.Delta.Y > 0 ? oldScale * 1.15 : oldScale / 1.15;
        if (newScale < 0.05) newScale = 0.05;
        if (newScale > 500) newScale = 500;

        double ratio = newScale / oldScale;
        _scene.OffsetX = (position.X - halfWidth) - ((position.X - halfWidth) - _scene.OffsetX) * ratio;
        _scene.OffsetY = (position.Y - halfHeight) - ((position.Y - halfHeight) - _scene.OffsetY) * ratio;
        _scene.Scale = newScale;

        RenderScene();
        e.Handled = true;
    }

    // ---------- Дерево ----------

    private void TreeRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is PlanTreeNode node && _viewModel != null)
            _viewModel.Tree.SelectedNode = node;
    }

    /// <summary>Копирует строку статуса целиком в буфер обмена.</summary>
    private async void CopyStatus_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var clipboard = Clipboard;
        if (clipboard == null) return;

        try
        {
            await clipboard.SetTextAsync(_viewModel.ConnectionStatus);
            if (sender is Button button) button.Content = "Скопировано";
        }
        catch
        {
            // буфер обмена может быть занят другим приложением
        }
    }

    private void TreeRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not PlanTreeNode node || _viewModel == null) return;

        _viewModel.Tree.StartRenameCommand.Execute(node);

        // TextBox редактора уже находится в дереве, но появляется только при IsEditing = true.
        Dispatcher.UIThread.Post(() =>
        {
            var editor = control.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (editor == null || !editor.IsVisible) return;
            editor.Focus();
            editor.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void TreeRowEditor_Attached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox box) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!box.IsVisible) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void TreeRowEditor_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is PlanTreeNode node)
            node.IsEditing = false;
    }

    private void TreeRowEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Escape) return;

        if (sender is Control control && control.DataContext is PlanTreeNode node)
            node.IsEditing = false;

        e.Handled = true;
    }
}
