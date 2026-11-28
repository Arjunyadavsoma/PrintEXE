namespace PrintShopCompanion.Queue.Tests;

using FluentAssertions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

/// <summary>
/// Phase 7 (Stage 4) — file storage coverage that runs on BOTH target
/// frameworks (neutral net10.0 AND net10.0-windows), unlike the DI-composition
/// suite which is windows-leg only.
///
/// <para>Covers: <see cref="LocalStorageLayout.EnsureDirectories"/> must create
/// every documented directory (Security/, Database/, Jobs/{incoming,queued,
/// printing,completed}, Temp/transfers/, Logs/), must be IDEMPOTENT (the app
/// calls it on every startup), and the Security directory must be writable for
/// the device token (file create + delete round trip).</para>
/// </summary>
public class StorageLayoutTests
{
    private static LocalStorageLayout NewLayout(string root) =>
        new(Options.Create(new StorageOptions()), rootOverride: root);

    [Fact]
    public void EnsureDirectories_creates_every_documented_directory()
    {
        var temp = Path.Combine(Path.GetTempPath(), "PrintShopLayoutTests", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = NewLayout(temp);
            layout.Root.Should().Be(temp);

            layout.EnsureDirectories();

            foreach (var dir in new[]
                     {
                         layout.SecurityDir, layout.DatabaseDir,
                         layout.JobsIncomingDir, layout.JobsQueuedDir,
                         layout.JobsPrintingDir, layout.JobsCompletedDir,
                         layout.TempTransfersDir, layout.LogsDir,
                     })
            {
                Directory.Exists(dir).Should().BeTrue($"EnsureDirectories must create {dir}");
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void EnsureDirectories_is_idempotent()
    {
        var temp = Path.Combine(Path.GetTempPath(), "PrintShopLayoutTests", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = NewLayout(temp);
            layout.EnsureDirectories();

            // The app calls EnsureDirectories on EVERY startup — a second call
            // over existing directories must not throw.
            var act = () => layout.EnsureDirectories();
            act.Should().NotThrow();
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void SecurityDir_accepts_the_device_token_file()
    {
        var temp = Path.Combine(Path.GetTempPath(), "PrintShopLayoutTests", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = NewLayout(temp);
            layout.EnsureDirectories();

            // The device token lives here (DPAPI-protected on Windows) — the
            // directory must accept create + overwrite + delete.
            var probe = Path.Combine(layout.SecurityDir, "probe.dat");
            File.WriteAllText(probe, "0123456789abcdef");
            File.WriteAllText(probe, "fedcba9876543210");
            File.ReadAllText(probe).Should().Be("fedcba9876543210");
            File.Delete(probe);
            File.Exists(probe).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
