namespace PrintShopCompanion.Infrastructure.Storage;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;

/// <summary>
/// Stores the long-lived device token encrypted with Windows DPAPI (current-user scope).
/// The token never touches disk in cleartext. The companion reads it at startup and
/// re-writes it on refresh.
/// </summary>
public sealed class DeviceTokenStore : IDeviceTokenStore
{
    private readonly string _path;
    private readonly ILogger<DeviceTokenStore> _logger;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DeviceTokenStore(ILogger<DeviceTokenStore> logger)
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrintShop", "Companion", "device.dat");
        _logger = logger;
    }

    public async Task<DeviceToken?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(_path, ct);
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            await using var ms = new MemoryStream(plain);
            return await JsonSerializer.DeserializeAsync<DeviceToken>(ms, Json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load device token");
            return null;
        }
    }

    public async Task SaveAsync(DeviceToken token, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, token, Json, ct);
        var plain = ms.ToArray();
        var cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_path, cipher, ct);
        _logger.LogInformation("Device token persisted (encrypted, current-user scope)");
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(_path))
        {
            try { File.Delete(_path); _logger.LogInformation("Device token cleared"); } catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }
}
