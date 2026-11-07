namespace PrintShopCompanion.Backend.Data;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public sealed class MockBackendOptions
{
    public string ShopName { get; set; } = "Demo Print Shop";
    public TimeSpan JobDeliveryInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool AutoGenerateJobs { get; set; } = true;
}

/// <summary>
/// Generates one synthetic job per paired device every JobDeliveryInterval.
/// Useful to exercise the full queue / print / report loop without manual triggering.
/// </summary>
public sealed class JobGeneratorService : BackgroundService
{
    private readonly MockStore _store;
    private readonly IOptionsMonitor<MockBackendOptions> _options;

    public JobGeneratorService(MockStore store, IOptionsMonitor<MockBackendOptions> options)
    {
        _store = store;
        _options = options;
        // Embed a 1-page test PDF in the store.
        _store.TestPdf = System.Text.Encoding.ASCII.GetBytes("""
            %PDF-1.4
            1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
            2 0 obj << /Type /Pages /Count 1 /Kids [3 0 R] >> endobj
            3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842]
                       /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj
            4 0 obj << /Length 90 >> stream
            BT /F1 24 Tf 80 760 Td (PrintShop Companion test print) Tj ET
            endstream endobj
            5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj
            xref
            0 6
            0000000000 65535 f 
            0000000010 00000 n 
            0000000060 00000 n 
            0000000110 00000 n 
            0000000260 00000 n 
            0000000390 00000 n 
            trailer << /Size 6 /Root 1 0 R >>
            startxref
            450
            %%EOF
            """);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.CurrentValue.AutoGenerateJobs)
                {
                    foreach (var (deviceId, _) in _store.Devices)
                    {
                        var dto = new Shared.Dto.LegacyPrintJobDto
                        {
                            JobId = Guid.NewGuid().ToString("N"),
                            ShopId = _store.ShopId,
                            DeviceId = deviceId,
                            Title = "Auto-generated test job",
                            Copies = 1,
                            PdfUrl = new Uri($"https://localhost:5241/api/companion/v1/jobs/{Guid.NewGuid():N}/pdf"),
                            PdfSha256 = null,
                            CreatedAt = DateTimeOffset.UtcNow
                        };
                        _store.Jobs[dto.JobId] = new JobRecord(dto, false, null, null, null, null);
                    }
                }
            }
            catch { /* keep ticking */ }
            await Task.Delay(_options.CurrentValue.JobDeliveryInterval, stoppingToken);
        }
    }
}
