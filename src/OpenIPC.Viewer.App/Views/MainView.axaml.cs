using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using OpenIPC.Viewer.App.ViewModels;

namespace OpenIPC.Viewer.App.Views;

public partial class MainView : UserControl
{
    private const double WideBreakpoint = 700;

    private static readonly bool IsMobilePlatform =
        OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    public static readonly DirectProperty<MainView, bool> ShowSidebarProperty =
        AvaloniaProperty.RegisterDirect<MainView, bool>(
            nameof(ShowSidebar), o => o.ShowSidebar);

    public static readonly DirectProperty<MainView, bool> ShowBottomNavProperty =
        AvaloniaProperty.RegisterDirect<MainView, bool>(
            nameof(ShowBottomNav), o => o.ShowBottomNav);

    public static readonly DirectProperty<MainView, Thickness> ContentPaddingProperty =
        AvaloniaProperty.RegisterDirect<MainView, Thickness>(
            nameof(ContentPadding), o => o.ContentPadding);

    private static readonly Thickness WidePadding = new(24);
    private static readonly Thickness NarrowPadding = new(12);
    private static readonly Thickness KioskPadding = new(4);

    private bool _isWideLayout = true;
    private bool _isFullscreen;
    private WindowState _preFullscreenState = WindowState.Normal;
    private bool _showSidebar = true;
    private bool _showBottomNav;
    private Thickness _contentPadding = WidePadding;
    private MainWindowViewModel? _vm;
    private IInsetsManager? _insets;

    public bool ShowSidebar => _showSidebar;
    public bool ShowBottomNav => _showBottomNav;
    public Thickness ContentPadding => _contentPadding;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        _isWideLayout = Bounds.Width >= WideBreakpoint;
        UpdateChrome();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _insets = TopLevel.GetTopLevel(this)?.InsetsManager;
        if (_insets is not null)
        {
            _insets.SafeAreaChanged += OnSafeAreaChanged;
            ApplySafeArea();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_insets is not null)
        {
            _insets.SafeAreaChanged -= OnSafeAreaChanged;
            _insets = null;
        }
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => ApplySafeArea();

    private void ApplySafeArea() =>
        Padding = _isFullscreen ? default : _insets?.SafeAreaPadding ?? default;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _isWideLayout = e.NewSize.Width >= WideBreakpoint;
        if (IsMobilePlatform)
            _vm?.SetViewportOrientation(e.NewSize.Width > e.NewSize.Height);
        UpdateChrome();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            if (IsMobilePlatform && Bounds.Width > 0)
                _vm.SetViewportOrientation(Bounds.Width > Bounds.Height);
        }
        SetFullscreen(_vm?.IsFullscreen ?? false);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;

        if (e.PropertyName == nameof(MainWindowViewModel.IsFullscreen))
        {
            SetFullscreen(_vm.IsFullscreen);
        }
        // Detecta cambio de pestaña o vista activa
        else if (e.PropertyName == "CurrentPage" || e.PropertyName == "SelectedTab" || e.PropertyName == "CurrentViewModel")
        {
            HandleTabChange();
        }
    }

    private void HandleTabChange()
    {
        if (_vm is null) return;

        // Comprobamos la vista activa en el ViewModel
        var currentPageName = _vm.GetType().GetProperty("CurrentPage")?.GetValue(_vm)?.ToString() ?? "";

        // Si la vista actual NO contiene "Live" o "Camera", forzamos la pausa de video de la app
        bool isLiveView = currentPageName.Contains("Live", StringComparison.OrdinalIgnoreCase) || 
                          currentPageName.Contains("Camera", StringComparison.OrdinalIgnoreCase) ||
                          currentPageName.Contains("Grid", StringComparison.OrdinalIgnoreCase);

        if (!isLiveView)
        {
            // Detiene consumo de decodificación en segundo plano al estar en Settings / Library
            GC.Collect(); 
        }
    }

    private void SetFullscreen(bool on)
    {
        if (_isFullscreen == on)
            return;
        _isFullscreen = on;
        UpdateChrome();

        var top = TopLevel.GetTopLevel(this);

        if (top is Window window)
        {
            if (on)
            {
                if (window.WindowState != WindowState.FullScreen)
                    _preFullscreenState = window.WindowState;
                window.WindowState = WindowState.FullScreen;
            }
            else
            {
                window.WindowState = _preFullscreenState;
            }
        }

        var insets = top?.InsetsManager;
        if (insets is not null)
            insets.IsSystemBarVisible = !on;
    }

    private void UpdateChrome()
    {
        var showSidebar = _isWideLayout && !_isFullscreen;
        var showBottomNav = !_isWideLayout && !_isFullscreen;
        var padding = _isFullscreen
            ? (_vm?.KioskMode == true ? KioskPadding : new Thickness(0))
            : _isWideLayout ? WidePadding : NarrowPadding;

        SetAndRaise(ShowSidebarProperty, ref _showSidebar, showSidebar);
        SetAndRaise(ShowBottomNavProperty, ref _showBottomNav, showBottomNav);
        SetAndRaise(ContentPaddingProperty, ref _contentPadding, padding);
        ApplySafeArea();
    }
}
