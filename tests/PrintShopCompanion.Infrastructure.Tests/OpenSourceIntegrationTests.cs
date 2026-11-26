namespace PrintShopCompanion.Infrastructure.Tests;

using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Printers;
using PrintShopCompanion.Infrastructure.Printers;
using Xunit;

/// <summary>
/// v0.7.17 integration tests for the open-source-adapted techniques.
/// All tests are SIMULATED (pure logic / fake seams) — no spooler events,
/// no hardware printing is claimed. See
/// docs/research/PRINTEXE_OPEN_SOURCE_INTEGRATION_ARCHITECTURE.md §Testing.
/// </summary>
public class OpenSourceIntegrationTests
{
    // ── A3: status mapping (technique adapted from PrintHub, MIT) ──

    [Theory]
    [InlineData(true,  false, false, false, PrinterStatus.Offline)] // offline wins over everything
    [InlineData(false, true,  false, false, PrinterStatus.Error)]
    [InlineData(false, true,  true,  false, PrinterStatus.Error)]   // error beats paused
    [InlineData(false, false, true,  false, PrinterStatus.Busy)]    // paused → busy (operator can act)
    [InlineData(false, false, false, true, PrinterStatus.Busy)]     // printing → busy
    [InlineData(false, false, false, false, PrinterStatus.Ready)]   // all clear
    public void Status_mapping_precedence(bool offline, bool error, bool paused, bool busy, PrinterStatus expected)
        => Assert.Equal(expected, PrinterStatusMapper.Map(offline, paused, error, busy, isPrinting: false, isInitializing: false, isPendingDeletion: false));

    [Fact]
    public void Status_text_names_the_recovery_action()
    {
        Assert.Contains("power", PrinterStatusMapper.TextFor(PrinterStatus.Offline));
        Assert.Contains("paper", PrinterStatusMapper.TextFor(PrinterStatus.Error));
        Assert.Equal("Ready", PrinterStatusMapper.TextFor(PrinterStatus.Ready));
    }

    // ── A4/A5: watcher debounce (technique adapted from QZ Tray, LGPL — behavior only) ──

    [Fact]
    public void Watcher_fires_PrintersChanged_only_when_set_changes()
    {
        var w = new FakeWatcher();
        var fired = 0;
        w.PrintersChanged += _ => fired++;
        w.SimulateChange(new[] { "A", "B" });   // set changed → fire
        w.SimulateChange(new[] { "A", "B" });   // same set → no fire
        w.SimulateChange(new[] { "A" });        // removal → fire
        Assert.Equal(2, fired);
    }

    private sealed class FakeWatcher : SpoolerChangeWatcher
    {
        // Simulates what RunLoop does on each spooler signal: compare the
        // enumerated set, fire only on change. (Simulated — no OS events.)
        public void SimulateChange(string[] printers)
        {
            var current = printers.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!current.SequenceEqual(_lastSet))
            {
                _lastSet = current;
                RaisePrintersChanged(current);
            }
        }
        private string[] _lastSet = Array.Empty<string>();
    }

    // ── G1: diagnostics recommendations (technique adapted from PrintHub, MIT) ──

    [Fact]
    public void Recommendations_cover_each_failure_mode()
    {
        var r = Diagnostics.DiagnosticsSnapshot.ComputeRecommendations(
            cloudReachable: false, tokenValid: false, deviceRegistered: false, databaseHealthy: false,
            printerCount: 0, hasDefaultPrinter: false, selectedPrinterOffline: true, failedJobs: 2);
        Assert.Equal(7, r.Count); // all seven rules fire: unreachable+pair+register+db+no-printers+offline+failed
        Assert.Contains(r, x => x.Contains("unreachable"));
        Assert.Contains(r, x => x.Contains("pair"));
        Assert.Contains(r, x => x.Contains("printer"));
        Assert.Contains(r, x => x.Contains("2 job(s) failed"));
    }

    [Fact]
    public void Recommendations_empty_when_healthy()
        => Assert.Empty(Diagnostics.DiagnosticsSnapshot.ComputeRecommendations(
            true, true, true, true, printerCount: 3, hasDefaultPrinter: true, selectedPrinterOffline: false, failedJobs: 0));

    [Fact]
    public void Recommendations_flag_missing_default_but_present_printers()
    {
        var r = Diagnostics.DiagnosticsSnapshot.ComputeRecommendations(
            true, true, true, true, printerCount: 3, hasDefaultPrinter: false, selectedPrinterOffline: false, failedJobs: 0);
        var single = Assert.Single(r);
        Assert.Contains("default", single);
    }
}
