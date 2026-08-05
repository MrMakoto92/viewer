using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OpenIPC.Viewer.App.Messages;
using OpenIPC.Viewer.App.Services;
using OpenIPC.Viewer.App.ViewModels.Dialogs;
using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Services;
using OpenIPC.Viewer.Core.Snapshots;
using OpenIPC.Viewer.Core.Video;

namespace OpenIPC.Viewer.App.ViewModels;

public sealed partial class GridPageViewModel : ViewModelBase,
    IRecipient<WindowMinimizedMessage>,
    IRecipient<WindowRestoredMessage>,
    IRecipient<CloseTileMessage>,
    IRecipient<ConfigImportedMessage>,
    IAsyncDisposable
{
    private readonly CameraDirectoryService _directory;
    private readonly LiveStreamCoordinator _coordinator;
    private readonly UserSettingsService _userSettings;
    private readonly ISnapshotService _snapshots;
    private readonly OpenIPC.Viewer.Core.Analytics.IAnalyticsEngine _analytics;
    private readonly AnalyticsBootstrap _analyticsBootstrap;
    private readonly AudioMonitor _audio;
    private readonly IReachabilityProbe _reachability;
    private readonly OpenIPC.Viewer.Core.Status.CameraStatusRegistry _statusRegistry;
    private readonly OpenIPC.Viewer.Core.Snapshots.ISnapshotFrameSource _frameSource;
    private readonly OpenIPC.Viewer.Core.Persistence.ILayoutRepository _layouts;
    private readonly IDialogService _dialogs;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GridPageViewModel> _logger;

    private IReadOnlyList<Camera> _allCameras = Array.Empty<Camera>();
    private bool _minimized;
    private bool _suppressSettingsRefresh;
    private CancellationTokenSource? _graceCts;

    public string Title => Localizer.Instance["Nav.Live"];

    public ObservableCollection<CameraTileViewModel> Tiles { get; } = new();
    public ObservableCollection<CameraTileViewModel?> Slots { get; } = new();

    public ObservableCollection<GridLayout> Layouts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteLayout))]
    private GridLayout? _activeLayout;

    public bool CanDeleteLayout => Layouts.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Columns))]
    [NotifyPropertyChangedFor(nameof(Rows))]
    private int _layoutSize = 2;

    public int Columns => LayoutSize;
    public int Rows => LayoutSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageDisplay))]
    [NotifyPropertyChangedFor(nameof(CanPrevPage))]
    [NotifyPropertyChangedFor(nameof(CanNextPage))]
    private int _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultiplePages))]
    [NotifyPropertyChangedFor(nameof(CanNextPage))]
    private int _pageCount = 1;

    public ObservableCollection<int> Pages { get; } = new();

    public int CurrentPageDisplay => CurrentPage + 1;
    public bool HasMultiplePages => PageCount > 1;
    public bool CanPrevPage => CurrentPage > 0;
    public bool CanNextPage => CurrentPage + 1 < PageCount;

    public GridPageViewModel(
        CameraDirectoryService directory,
        LiveStreamCoordinator coordinator,
        UserSettingsService userSettings,
        ISnapshotService snapshots,
        OpenIPC.Viewer.Core.Analytics.IAnalyticsEngine analytics,
        AnalyticsBootstrap analyticsBootstrap,
        AudioMonitor audio,
        IReachabilityProbe reachability,
        OpenIPC.Viewer.Core.Status.CameraStatusRegistry statusRegistry,
        OpenIPC.Viewer.Core.Snapshots.ISnapshotFrameSource frameSource,
        OpenIPC.Viewer.Core.Persistence.ILayoutRepository layouts,
        IDialogService dialogs,
        ILoggerFactory loggerFactory)
    {
        _directory = directory;
        _coordinator = coordinator;
        _userSettings = userSettings;
        _snapshots = snapshots;
        _analytics = analytics;
        _analyticsBootstrap = analyticsBootstrap;
        _audio = audio;
        _reachability = reachability;
        _statusRegistry = statusRegistry;
        _frameSource = frameSource;
        _layouts = layouts;
        _dialogs = dialogs;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GridPageViewModel>();

        _stillsMode = _userSettings.Current.GridStillsMode;
        _stillsIntervalSeconds = _userSettings.Current.GridStillsIntervalSeconds;

        WeakReferenceMessenger.Default.Register<WindowMinimizedMessage>(this);
        WeakReferenceMessenger.Default.Register<WindowRestoredMessage>(this);
        WeakReferenceMessenger.Default.Register<CloseTileMessage>(this);
        WeakReferenceMessenger.Default.Register<ConfigImportedMessage>(this);

        _userSettings.Changed += async (_, _) =>
        {
            if (_suppressSettingsRefresh) return;
            try { await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => RefreshTilesAsync(CancellationToken.None)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Grid refresh after settings change failed"); }
        };
    }

    [RelayCommand]
    private async Task OpenHealthAsync()
    {
        var vm = new HealthCenterViewModel(_directory, _reachability, _statusRegistry, _loggerFactory.CreateLogger<HealthCenterViewModel>());
        await _dialogs.ShowHealthCenterAsync(vm).ConfigureAwait(true);
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        if (_minimized) return;
        _allCameras = await _directory.ListAsync(ct).ConfigureAwait(true);
        await LoadLayoutsAsync(ct).ConfigureAwait(true);
        await RefreshTilesAsync(ct).ConfigureAwait(true);
    }

    private async Task LoadLayoutsAsync(CancellationToken ct)
    {
        var all = await _layouts.GetAllAsync(ct).ConfigureAwait(true);
        Layouts.Clear();
        foreach (var l in all) Layouts.Add(l);
        OnPropertyChanged(nameof(CanDeleteLayout));

        var activeId = _userSettings.Current.ActiveLayoutId;
        ActiveLayout = Layouts.FirstOrDefault(l => l.Id.Value == activeId) ?? Layouts.FirstOrDefault();
        if (ActiveLayout is { } a) LayoutSize = a.GridSize;
        CurrentPage = 0;
    }

    [RelayCommand]
    private async Task SetLayoutAsync(string size)
    {
        if (!int.TryParse(size, out var n) || n < 1 || n > 5) return;
        LayoutSize = n;
        CurrentPage = 0;
        if (ActiveLayout is { } a)
        {
            await _layouts.SetGridSizeAsync(a.Id, n, CancellationToken.None).ConfigureAwait(true);
            ActiveLayout = a with { GridSize = n };
            ReplaceLayoutInList(ActiveLayout);
        }
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoToPageAsync(object? page)
    {
        if (page is null) return;
        int oneBased;
        try { oneBased = System.Convert.ToInt32(page, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception) { return; }
        var target = oneBased - 1;
        if (target < 0 || target >= PageCount || target == CurrentPage) return;
        CurrentPage = target;
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!CanNextPage) return;
        CurrentPage++;
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (!CanPrevPage) return;
        CurrentPage--;
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SwitchLayoutAsync(GridLayout? layout)
    {
        if (layout is null || (ActiveLayout is { } cur && cur.Id == layout.Id)) return;
        ActiveLayout = layout;
        LayoutSize = layout.GridSize;
        CurrentPage = 0;
        await PersistActiveLayoutAsync(layout.Id.Value).ConfigureAwait(true);
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddLayoutAsync()
    {
        var name = await _dialogs.PromptAsync(
            Localizer.Instance["Layouts.NewTitle"], "", Localizer.Instance["Common.Create"], Localizer.Instance["Common.Cancel"])
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = await _layouts.AddAsync(name.Trim(), 2, Layouts.Count, CancellationToken.None).ConfigureAwait(true);
        await LoadLayoutsAsync(CancellationToken.None).ConfigureAwait(true);
        var created = Layouts.FirstOrDefault(l => l.Id.Value == id.Value);
        if (created is not null) await SwitchLayoutAsync(created).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ManageCamerasAsync()
    {
        if (ActiveLayout is not { } a) return;
        var vm = new ManageLayoutCamerasViewModel(
            a.Id, a.Name, _layouts, _directory,
            _loggerFactory.CreateLogger<ManageLayoutCamerasViewModel>());
        await _dialogs.ShowManageLayoutCamerasAsync(vm).ConfigureAwait(true);
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RenameLayoutAsync()
    {
        if (ActiveLayout is not { } a) return;
        var name = await _dialogs.PromptAsync(
            Localizer.Instance["Layouts.RenameTitle"], a.Name, Localizer.Instance["Common.Rename"], Localizer.Instance["Common.Cancel"])
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == a.Name) return;
        await _layouts.RenameAsync(a.Id, name.Trim(), CancellationToken.None).ConfigureAwait(true);
        await LoadLayoutsAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteLayoutAsync()
    {
        if (ActiveLayout is not { } a || Layouts.Count <= 1) return;
        var ok = await _dialogs.ConfirmAsync(
            Localizer.Instance["Layouts.DeleteTitle"],
            string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Layouts.DeleteMessageFormat"], a.Name),
            Localizer.Instance["Common.Delete"], Localizer.Instance["Common.Cancel"]).ConfigureAwait(true);
        if (!ok) return;
        await _layouts.RemoveAsync(a.Id, CancellationToken.None).ConfigureAwait(true);
        await LoadLayoutsAsync(CancellationToken.None).ConfigureAwait(true);
        if (ActiveLayout is { } now) await PersistActiveLayoutAsync(now.Id.Value).ConfigureAwait(true);
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    public async Task MoveLayoutAsync(int fromIndex, int toIndex, CancellationToken ct)
    {
        if (fromIndex < 0 || fromIndex >= Layouts.Count) return;
        if (toIndex < 0 || toIndex >= Layouts.Count) return;
        if (fromIndex == toIndex) return;

        Layouts.Move(fromIndex, toIndex);
        try
        {
            await _layouts.ReorderAsync(Layouts.Select(l => l.Id).ToList(), ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisting layout order failed");
        }
    }

    private async Task PersistActiveLayoutAsync(int id)
    {
        _suppressSettingsRefresh = true;
        try { await _userSettings.UpdateAsync(_userSettings.Current with { ActiveLayoutId = id }).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Persisting active layout failed"); }
        finally { _suppressSettingsRefresh = false; }
    }

    private void ReplaceLayoutInList(GridLayout updated)
    {
        for (var i = 0; i < Layouts.Count; i++)
            if (Layouts[i].Id == updated.Id) { Layouts[i] = updated; break; }
    }

    [ObservableProperty] private bool _stillsMode;
    [ObservableProperty] private int _stillsIntervalSeconds;

    public int[] StillsIntervalOptions { get; } = { 2, 5, 10, 30, 60 };

    partial void OnStillsModeChanged(bool value) => _ = ApplyStillsChangeAsync();

    partial void OnStillsIntervalSecondsChanged(int value) => _ = ApplyStillsChangeAsync();

    private async Task ApplyStillsChangeAsync()
    {
        await PersistStillsAsync().ConfigureAwait(true);
        await RebuildTilesAsync().ConfigureAwait(true);
    }

    private async Task PersistStillsAsync()
    {
        _suppressSettingsRefresh = true;
        try
        {
            await _userSettings.UpdateAsync(_userSettings.Current with
            {
                GridStillsMode = StillsMode,
                GridStillsIntervalSeconds = StillsIntervalSeconds,
            }).ConfigureAwait(true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Persisting stills settings failed"); }
        finally { _suppressSettingsRefresh = false; }
    }

    private async Task RebuildTilesAsync()
    {
        for (var i = Tiles.Count - 1; i >= 0; i--)
        {
            var tile = Tiles[i];
            Tiles.RemoveAt(i);
            try { await tile.DisposeAsync().ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing tile during stills rebuild"); }
        }
        await RefreshTilesAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RefreshTilesAsync(CancellationToken ct)
    {
        var pageSize = LayoutSize * LayoutSize;
        var capacity = Math.Min(pageSize, Math.Max(1, _userSettings.MaxConcurrentGridSessions));

        List<Camera> members;
        if (ActiveLayout is { } layout)
        {
            var tileIds = await _layouts.GetTilesAsync(layout.Id, ct).ConfigureAwait(true);
            var byId = _allCameras.ToDictionary(c => c.Id);
            members = tileIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }
        else
        {
            members = _allCameras.Where(c => c.IncludedInGrid).ToList();
        }

        var pageCount = Math.Max(1, (members.Count + pageSize - 1) / pageSize);
        if (CurrentPage >= pageCount) CurrentPage = pageCount - 1;
        if (CurrentPage < 0) CurrentPage = 0;
        UpdatePager(pageCount);

        var visible = members.Skip(CurrentPage * pageSize).Take(capacity).ToList();
        var visibleIds = visible.Select(c => c.Id).ToHashSet();

        var stale = new List<CameraTileViewModel>();
        for (var i = Tiles.Count - 1; i >= 0; i--)
        {
            if (!visibleIds.Contains(Tiles[i].Camera.Id))
            {
                stale.Add(Tiles[i]);
                Tiles.RemoveAt(i);
            }
        }
        if (stale.Count > 0)
            _ = DisposeTilesInBackgroundAsync(stale);

        int index = 0;

        foreach (var camera in visible)
        {
            var quality = DesiredQuality(camera);
            var existing = Tiles.FirstOrDefault(t => t.Camera.Id == camera.Id);
            if (existing is not null)
            {
                if (!StreamUriChanged(existing.Camera, camera) && !AnalyticsChanged(existing.Camera, camera))
                {
                    try { await existing.SetQualityAsync(quality, ct).ConfigureAwait(true); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to switch quality for {Camera}", camera.Name); }
                    index++;
                    continue;
                }

                var idx = Tiles.IndexOf(existing);
                Tiles.RemoveAt(idx);
                try { await existing.DisposeAsync().ConfigureAwait(true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error releasing stale tile for {Camera}", camera.Name); }

                var rebuilt = new CameraTileViewModel(camera, _coordinator, _directory, _userSettings, _snapshots, _analytics, _analyticsBootstrap, _audio, _reachability, _statusRegistry, _frameSource, StillsMode, StillsIntervalSeconds, _loggerFactory.CreateLogger<CameraTileViewModel>());
                rebuilt.SetInitialQuality(quality);
                Tiles.Insert(idx, rebuilt);

                if (index > 0)
                    await Task.Delay(200, ct).ConfigureAwait(true);

                try { await rebuilt.ActivateAsync(ct).ConfigureAwait(true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to activate rebuilt tile for {Camera}", camera.Name); }
                
                index++;
                continue;
            }

            var tile = new CameraTileViewModel(camera, _coordinator, _directory, _userSettings, _snapshots, _analytics, _analyticsBootstrap, _audio, _reachability, _statusRegistry, _frameSource, StillsMode, StillsIntervalSeconds, _loggerFactory.CreateLogger<CameraTileViewModel>());
            tile.SetInitialQuality(quality);
            Tiles.Add(tile);

            if (index > 0)
                await Task.Delay(200, ct).ConfigureAwait(true);

            try { await tile.ActivateAsync(ct).ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to activate tile for {Camera}", camera.Name); }

            index++;
        }

        var visualCapacity = LayoutSize * LayoutSize;
        Slots.Clear();
        for (var i = 0; i < visualCapacity; i++)
            Slots.Add(i < Tiles.Count ? Tiles[i] : null);
    }

    private static bool StreamUriChanged(Camera a, Camera b) =>
        (a.RtspSubUri ?? a.RtspMainUri) != (b.RtspSubUri ?? b.RtspMainUri);

    private static bool AnalyticsChanged(Camera a, Camera b)
    {
        var x = a.AnalyticsOrDefault;
        var y = b.AnalyticsOrDefault;
        if (x.Enabled != y.Enabled || x.AutoRecord != y.AutoRecord || x.AnalyticsFps != y.AnalyticsFps
            || x.PostEventSeconds != y.PostEventSeconds
            || Math.Abs(x.ConfidenceThreshold - y.ConfidenceThreshold) > 0.001f)
            return true;
        var xc = (x.ClassIds ?? Array.Empty<int>()).OrderBy(i => i);
        var yc = (y.ClassIds ?? Array.Empty<int>()).OrderBy(i => i);
        return !xc.SequenceEqual(yc);
    }

    private StreamQuality DesiredQuality(Camera camera) =>
        StreamQualityPolicy.Resolve(camera.StreamQualityOverride, _userSettings.Current.AutoSdHd, LayoutSize);

    public async Task MoveTileAsync(int fromIndex, int toIndex, CancellationToken ct)
    {
        if (fromIndex < 0 || fromIndex >= Tiles.Count) return;
        if (toIndex < 0 || toIndex >= Tiles.Count) return;
        if (fromIndex == toIndex) return;

        Tiles.Move(fromIndex, toIndex);

        var visualCapacity = LayoutSize * LayoutSize;
        Slots.Clear();
        for (var i = 0; i < visualCapacity; i++)
            Slots.Add(i < Tiles.Count ? Tiles[i] : null);

        if (ActiveLayout is not { } a) return;

        try
        {
            var offset = CurrentPage * LayoutSize * LayoutSize;
            var from = offset + fromIndex;
            var to = offset + toIndex;
            var full = (await _layouts.GetTilesAsync(a.Id, ct).ConfigureAwait(true)).ToList();
            if (from < full.Count && to < full.Count)
            {
                var moved = full[from];
                full.RemoveAt(from);
                full.Insert(to, moved);
                await _layouts.SetTilesAsync(a.Id, full, ct).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisting layout tile order failed");
        }
    }

    public async void Receive(CloseTileMessage message)
    {
        var tile = Tiles.FirstOrDefault(t => t.Camera.Id == message.CameraId);
        if (tile is null) return;
        Tiles.Remove(tile);
        RebuildSlots();
        try { await tile.DisposeAsync().ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error releasing closed tile"); }
    }

    private async Task DisposeTilesInBackgroundAsync(IReadOnlyList<CameraTileViewModel> tiles)
    {
        await Task.WhenAll(tiles.Select(async tile =>
        {
            try { await tile.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error releasing tile in background"); }
        })).ConfigureAwait(false);
    }

    private void UpdatePager(int pageCount)
    {
        PageCount = pageCount;
        if (Pages.Count != pageCount)
        {
            Pages.Clear();
            for (var i = 1; i <= pageCount; i++) Pages.Add(i);
        }
    }

    private void RebuildSlots()
    {
        var visualCapacity = LayoutSize * LayoutSize;
        Slots.Clear();
        for (var i = 0; i < visualCapacity; i++)
            Slots.Add(i < Tiles.Count ? Tiles[i] : null);
    }

    private static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(10);

    public void Receive(WindowMinimizedMessage message)
    {
        _minimized = true;
        foreach (var tile in Tiles)
            tile.Pause();

        _graceCts?.Cancel();
        _graceCts = new CancellationTokenSource();
        var token = _graceCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(PauseGrace, token).ConfigureAwait(true); }
            catch (OperationCanceledException) { return; }
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (_minimized) await ReleaseAllAsync().ConfigureAwait(true);
            });
        });
    }

    public async void Receive(ConfigImportedMessage message)
    {
        try { await LoadAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Grid reload after import failed"); }
    }

    public async void Receive(WindowRestoredMessage message)
    {
        _minimized = false;
        _graceCts?.Cancel();
        if (Tiles.Count > 0)
        {
            foreach (var tile in Tiles)
                tile.Resume();
        }
        else
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task ReleaseAllAsync()
    {
        var copy = Tiles.ToArray();
        Tiles.Clear();
        Slots.Clear();
        foreach (var tile in copy)
        {
            try { await tile.DisposeAsync().ConfigureAwait(true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error releasing tile during minimize"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _graceCts?.Cancel();
        await ReleaseAllAsync().ConfigureAwait(false);
    }
}
