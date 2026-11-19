namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Infrastructure.Sync;

/// <summary>
/// View model for the Sync Center page. Surfaces the SyncEngine's connection
/// status, last-sync timestamp, pending operations, and failed operations.
/// Subscribes to SyncEngine events so the UI updates live as the sync loop
/// runs (every 15s) instead of requiring a manual refresh.
/// </summary>
public partial class SyncCenterViewModel : ObservableObject
{
    private readonly SyncEngine _sync;
    private readonly IPendingOpsQueue _pendingOpsQueue;
    private readonly IShopBackend _backend;
    private readonly IPrintShopBackend _deviceBackend;
    private readonly Core.Services.ShopDataStore _store;

    public SyncCenterViewModel(
        SyncEngine sync, IPendingOpsQueue pendingOps,
        IShopBackend backend, IPrintShopBackend deviceBackend,
        Core.Services.ShopDataStore store)
    {
        _sync = sync;
        _pendingOpsQueue = pendingOps;
        _backend = backend;
        _deviceBackend = deviceBackend;
        _store = store;

        // Subscribe to engine events so the UI updates live.
        _sync.OnlineStatusChanged += OnOnlineStatusChanged;
        _sync.SyncCompleted += OnSyncCompleted;
        _store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Core.Services.ShopDataStore.RealtimeConnected))
                UpdateConnectionStatus(_sync.IsOnline);
        };
    }

    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _connectionStatusText = "Checking...";
    [ObservableProperty] private string _connectionSubText = "Determining connection status";
    [ObservableProperty] private string _connectionPillText = "—";
    [ObservableProperty] private Brush _connectionPillBackground = Brushes.Gray;
    [ObservableProperty] private Brush _connectionPillForeground = Brushes.White;
    [ObservableProperty] private Brush _connectionDotBrush = Brushes.Gray;
    [ObservableProperty] private string _lastSyncText = "Never";
    [ObservableProperty] private string _pendingCountText = "0";
    [ObservableProperty] private string _failedCountText = "0";
    [ObservableProperty] private string _deviceId = "—";
    [ObservableProperty] private ObservableCollection<PendingOpRow> _pendingOps = new();
    [ObservableProperty] private ObservableCollection<PendingOpRow> _failedOps = new();
    [ObservableProperty] private string _hasNoPending = "yes";
    [ObservableProperty] private string _hasNoFailed = "yes";

    public Brush ConnectionPillBrush => ConnectionPillBackground;

    /// <summary>Load the current state from the SyncEngine and PendingOpsQueue.</summary>
    public async Task LoadAsync()
    {
        UpdateConnectionStatus(_sync.IsOnline);
        UpdateLastSyncTime(_sync.LastSyncTime);
        await RefreshPendingOpsAsync();
        await RefreshDeviceIdAsync();
    }

    /// <summary>Trigger an immediate sync cycle (the user clicked "Sync Now").</summary>
    public async Task SyncNowAsync()
    {
        await _sync.SyncNowAsync();
        await LoadAsync();
    }

    private void OnOnlineStatusChanged(bool isOnline)
    {
        UpdateConnectionStatus(isOnline);
    }

    private void OnSyncCompleted()
    {
        UpdateLastSyncTime(_sync.LastSyncTime);
        _ = RefreshPendingOpsAsync();
    }

    private void UpdateConnectionStatus(bool isOnline)
    {
        IsOnline = isOnline;
        if (isOnline)
        {
            ConnectionStatusText = "Connected";
            ConnectionSubText = "Background sync running every 15 seconds";
            ConnectionPillText = "ONLINE";
            ConnectionPillBackground = new SolidColorBrush(Color.FromRgb(0x00, 0x7F, 0x3D));
            ConnectionPillForeground = Brushes.White;
            ConnectionDotBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x7F, 0x3D));
        }
        else
        {
            ConnectionStatusText = "Offline";
            ConnectionSubText = "Pending operations will sync when connectivity returns";
            ConnectionPillText = "OFFLINE";
            ConnectionPillBackground = new SolidColorBrush(Color.FromRgb(0xBE, 0x22, 0x2A));
            ConnectionPillForeground = Brushes.White;
            ConnectionDotBrush = new SolidColorBrush(Color.FromRgb(0xBE, 0x22, 0x2A));
        }
        OnPropertyChanged(nameof(ConnectionPillBrush));
    }

    private void UpdateLastSyncTime(DateTimeOffset lastSync)
    {
        if (lastSync == DateTimeOffset.MinValue)
        {
            LastSyncText = "Never";
            return;
        }
        var ago = DateTimeOffset.UtcNow - lastSync;
        LastSyncText = ago.TotalSeconds < 60
            ? $"{(int)ago.TotalSeconds}s ago"
            : ago.TotalMinutes < 60
                ? $"{(int)ago.TotalMinutes}m ago"
                : ago.TotalHours < 24
                    ? $"{(int)ago.TotalHours}h ago"
                    : lastSync.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private async Task RefreshPendingOpsAsync()
    {
        // GetAllOps (NOT GetPendingOps): the dispatchable filter excluded
        // 'failed' and 'in_flight' rows, so the failed list below was ALWAYS
        // empty — real failures were invisible on this page.
        var ops = _pendingOpsQueue.GetAllOps(limit: 50);
        var active = ops.Where(p => p.Status is "pending" or "retry" or "in_flight").ToList();
        var failed = ops.Where(p => p.Status == "failed").ToList();

        PendingOps = new ObservableCollection<PendingOpRow>(
            active.Select(p => new PendingOpRow
            {
                OperationType = p.OperationType,
                TargetId = p.TargetId,
                Status = p.Status,
                LastError = p.LastError ?? "—"
            }));
        PendingCountText = PendingOps.Count.ToString();
        HasNoPending = PendingOps.Count == 0 ? "yes" : "";

        FailedOps = new ObservableCollection<PendingOpRow>(
            failed.Select(p => new PendingOpRow
            {
                OperationType = p.OperationType,
                TargetId = p.TargetId,
                Status = p.Status,
                LastError = p.LastError ?? "—"
            }));
        FailedCountText = FailedOps.Count.ToString();
        HasNoFailed = FailedOps.Count == 0 ? "yes" : "";
    }

    private async Task RefreshDeviceIdAsync()
    {
        try
        {
            // The REAL device id — GET /api/v1/sync/status returns it for the
            // paired device. (The old code used the shop id as a stand-in.)
            var status = await _deviceBackend.SyncStatusAsync();
            DeviceId = status.DeviceId ?? status.ShopId ?? "—";
        }
        catch
        {
            DeviceId = "—";
        }
    }

    /// <summary>One row in the pending/failed ops list.</summary>
    public sealed class PendingOpRow
    {
        public string OperationType { get; init; } = "";
        public string TargetId { get; init; } = "";
        public string Status { get; init; } = "";
        public string LastError { get; init; } = "";
    }
}
