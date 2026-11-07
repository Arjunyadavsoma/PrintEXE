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
    private readonly IPendingOpsQueue _pendingOps;
    private readonly IShopBackend _backend;

    public SyncCenterViewModel(SyncEngine sync, IPendingOpsQueue pendingOps, IShopBackend backend)
    {
        _sync = sync;
        _pendingOps = pendingOps;
        _backend = backend;

        // Subscribe to engine events so the UI updates live.
        _sync.OnlineStatusChanged += OnOnlineStatusChanged;
        _sync.SyncCompleted += OnSyncCompleted;
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
            ConnectionPillBackground = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
            ConnectionPillForeground = Brushes.White;
            ConnectionDotBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
        }
        else
        {
            ConnectionStatusText = "Offline";
            ConnectionSubText = "Pending operations will sync when connectivity returns";
            ConnectionPillText = "OFFLINE";
            ConnectionPillBackground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            ConnectionPillForeground = Brushes.White;
            ConnectionDotBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
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
        var pending = _pendingOps.GetPendingOps(limit: 50);
        PendingOps = new ObservableCollection<PendingOpRow>(
            pending.Select(p => new PendingOpRow
            {
                OperationType = p.OperationType,
                TargetId = p.TargetId,
                Status = p.Status,
                LastError = p.LastError ?? "—"
            }));
        PendingCountText = PendingOps.Count.ToString();
        HasNoPending = PendingOps.Count == 0 ? "yes" : "";

        // Failed ops are those whose status is "Failed"
        var failed = pending.Where(p => p.Status == "Failed").ToList();
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
            if (_backend.CurrentShop is null)
            {
                await _backend.GetShopAsync();
            }
            // Device ID would come from the device-token store if exposed; for
            // now we use the shop ID as a proxy so the user sees something
            // meaningful. This can be replaced with a real device ID display
            // once the credential store exposes it.
            DeviceId = _backend.CurrentShop?.Id ?? "—";
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
