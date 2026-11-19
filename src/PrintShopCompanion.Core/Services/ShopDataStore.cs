namespace PrintShopCompanion.Core.Services;

using System.Collections.ObjectModel;
using System.ComponentModel;
using PrintShopCompanion.Core.Models;

/// <summary>
/// The single in-memory source of truth for shop data shown on every page.
///
/// WHY THIS EXISTS (the "web and app show the same thing" requirement):
/// Previously each page fetched its own data when opened (Overview →
/// GetAnalyticsAsync, Orders → ListOrdersAsync …) and never refreshed, while
/// SyncEngine pulled the SAME data every 15s and threw the results away.
/// Result: the shopkeeper webpage and the app drifted apart within seconds.
///
/// Now: SyncEngine's pull loop and the Socket.IO realtime client WRITE INTO
/// this store, and every page BINDS to it. When the sync tick lands (15s) or
/// a realtime event arrives (job.dispatch), every open page updates at once —
/// exactly what the webpage shows.
///
/// All mutation funnels through one method family; PropertyChanged is raised
/// on the UI thread via the injected marshal action so views can bind freely.
/// </summary>
public sealed class ShopDataStore : INotifyPropertyChanged
{
    private readonly Action<Action> _marshalToUi;

    public ShopDataStore(Action<Action> marshalToUi) => _marshalToUi = marshalToUi;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Connection / sync state ──

    private bool _online = true;                 // REST reachable (SyncEngine probe)
    private bool _realtimeConnected;             // Socket.IO authenticated
    private DateTimeOffset _lastSync = DateTimeOffset.MinValue;
    private string _syncPhase = "Starting";      // Starting | Live | Offline

    public bool Online => _online;
    public bool RealtimeConnected => _realtimeConnected;
    public DateTimeOffset LastSync => _lastSync;

    /// <summary>Short live-status line for headers: "● Live" vs "● Offline".</summary>
    public string LiveStatusText => _online ? "Live" : "Offline";
    public bool IsLive => _online;

    /// <summary>Detailed pill text for the Sync Center / header tooltip.</summary>
    public string SyncPhase
    {
        get => _realtimeConnected ? "Live (realtime)" : (_online ? "Polling" : "Offline");
    }

    public string LastSyncText => _lastSync == DateTimeOffset.MinValue
        ? "Never"
        : Core.Presentation.RelativeTime.From(_lastSync);

    // ── Shop identity ──

    private string _shopName = "Loading…";
    public string ShopName
    {
        get => _shopName;
        private set => Set(ref _shopName, value);
    }

    public string ShopNameInitial => string.IsNullOrWhiteSpace(_shopName) || _shopName == "Loading…"
        ? "P"
        : _shopName.Substring(0, 1).ToUpperInvariant();

    private bool _authenticated;
    public bool Authenticated
    {
        get => _authenticated;
        private set => Set(ref _authenticated, value);
    }

    // ── Analytics (web dashboard parity) ──

    private ShopAnalytics? _analytics;
    public ShopAnalytics? Analytics
    {
        get => _analytics;
        private set => Set(ref _analytics, value);
    }

    // ── Orders ──

    private IReadOnlyList<OrderRecord> _orders = Array.Empty<OrderRecord>();
    /// <summary>Latest orders snapshot (newest first, as returned by the API).</summary>
    public IReadOnlyList<OrderRecord> Orders
    {
        get => _orders;
        private set => Set(ref _orders, value);
    }

    // ── Devices ──

    private IReadOnlyList<DeviceRegistration> _devices = Array.Empty<DeviceRegistration>();
    public IReadOnlyList<DeviceRegistration> Devices
    {
        get => _devices;
        private set => Set(ref _devices, value);
    }

    // ── Local print-queue revision ──

    private int _queueRevision;
    /// <summary>
    /// Incremented whenever the LOCAL print queue may have changed (a
    /// job.dispatch arrived, the JobProcessor reported a transition, or the
    /// queue page's poll tick fired). Pages that show queue data compare this
    /// to their last-seen value and refresh when it moves — cheap, no event
    /// storm, works with the durable SQLite queue.
    /// </summary>
    public int QueueRevision => _queueRevision;

    // ── Mutation entry points (called from SyncEngine / realtime client / pages) ──

    /// <summary>Store one full pull result from the sync loop.</summary>
    public void ApplyShopSnapshot(
        string? shopName,
        ShopAnalytics? analytics,
        IReadOnlyList<OrderRecord> orders,
        IReadOnlyList<DeviceRegistration>? devices = null,
        bool authenticated = true)
    {
        _marshalToUi(() =>
        {
            Authenticated = authenticated;
            if (!string.IsNullOrWhiteSpace(shopName)) ShopName = shopName;
            if (analytics is not null) Analytics = analytics;
            if (orders is not null && orders.Count > 0) Orders = orders;
            if (devices is not null) Devices = devices;
            OnPropertyChanged(nameof(ShopNameInitial));
        });
    }

    public void SetSyncState(bool online, DateTimeOffset lastSync)
    {
        _marshalToUi(() =>
        {
            var changed = _online != online || _lastSync != lastSync;
            _online = online;
            _lastSync = lastSync;
            if (changed)
            {
                OnPropertyChanged(nameof(Online));
                OnPropertyChanged(nameof(LiveStatusText));
                OnPropertyChanged(nameof(IsLive));
                OnPropertyChanged(nameof(SyncPhase));
                OnPropertyChanged(nameof(LastSyncText));
            }
        });
    }

    public void SetRealtimeConnected(bool connected)
    {
        _marshalToUi(() =>
        {
            if (_realtimeConnected == connected) return;
            _realtimeConnected = connected;
            OnPropertyChanged(nameof(RealtimeConnected));
            OnPropertyChanged(nameof(SyncPhase));
        });
    }

    public void BumpQueueRevision()
    {
        _marshalToUi(() =>
        {
            _queueRevision++;
            OnPropertyChanged(nameof(QueueRevision));
        });
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
