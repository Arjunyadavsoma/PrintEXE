namespace PrintShopCompanion.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PrintShopCompanion.Core.Models;

/// <summary>
/// Surfaces meaningful events to the shopkeeper via:
///  - Windows toast notifications (via WPF's NotificationArea or the Win32 NotifyIcon)
///  - The in-app dashboard's status badges
///  - The system-tray icon's tooltip text (live state at a glance)
///
/// <para>Notifications are deliberately conservative — the shopkeeper is busy and
/// we do not want to train them to ignore pop-ups. The companion only fires a
/// notification for: (1) first pairing success, (2) first test-print success,
/// (3) a print job that needs operator attention (UncertainSpooled/OutcomeUnknown),
/// (4) the printer going offline while jobs are pending, (5) a connection drop
/// lasting more than 60 seconds.</para>
/// </summary>
public sealed class NotificationService
{
    private readonly Tray.TrayIconHost _tray;

    public NotificationService(Tray.TrayIconHost tray) => _tray = tray;

    public enum Severity { Info, Success, Warning, Error }

    /// <summary>
    /// Show a toast to the shopkeeper. Title and body are kept short and in plain
    /// language — no internal API terminology, no job IDs unless the user has
    /// opened the Print Queue screen.
    /// </summary>
    public void Show(string title, string body, Severity severity = Severity.Info)
    {
        // In a production build this calls the WinRT Windows.UI.Notifications.ToastNotificationManager
        // or the H.NotifyIcon toast helper. For the scaffold we use a small popup.
        _tray.ShowBalloon(title, body, severity);
    }

    // ─────────────── pre-canned notifications ───────────────

    public void PairingSucceeded(string shopName) =>
        Show("PrintShop connected", $"This PC is now connected to {shopName}.", Severity.Success);

    public void TestPrintSucceeded(string printerName) =>
        Show("Test print sent", $"A test page was sent to {printerName}.", Severity.Info);

    public void TestPrintFailed(string printerName, string reason) =>
        Show("Test print failed", $"Could not print to {printerName}: {reason}", Severity.Error);

    public void PrinterWentOffline(string printerName) =>
        Show("Printer offline", $"{printerName} is no longer available. New jobs will wait until it comes back.",
             Severity.Warning);

    public void JobNeedsAttention(string jobTitle) =>
        Show("Print job needs your attention",
             $"We could not confirm whether '{jobTitle}' actually printed. Please check the printer and confirm in Print Queue.",
             Severity.Warning);

    public void ConnectionLost() =>
        Show("Connection lost", "Could not reach PrintShop. Jobs will queue up locally and send when you reconnect.",
             Severity.Warning);

    public void ConnectionRestored() =>
        Show("Back online", "PrintShop connection restored.", Severity.Success);
}
