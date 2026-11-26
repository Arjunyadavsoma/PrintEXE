using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Infrastructure.Backend;
using Xunit;

namespace PrintShopCompanion.Infrastructure.Tests;

/// <summary>
/// Decision D-12 — shop-ownership defense in depth. Mismatched jobs are
/// rejected; unknown values are tolerated so older backends keep working.
/// </summary>
public sealed class JobOwnershipValidatorTests
{
    private static PrintJobRecord Record(string shopId) => new()
    {
        JobId = "J-1", ShopId = shopId, DeviceId = "dev", Title = "T",
        Copies = 1, CreatedAt = DateTimeOffset.UtcNow,
        QueuedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Matching_shop_is_accepted()
    {
        Assert.True(JobOwnershipValidator.IsAcceptable(Record("shop-1"), "shop-1"));
    }

    [Fact]
    public void Mismatched_shop_is_rejected()
    {
        Assert.False(JobOwnershipValidator.IsAcceptable(Record("shop-2"), "shop-1"));
    }

    [Fact]
    public void Unknown_local_shop_is_tolerated()
    {
        Assert.True(JobOwnershipValidator.IsAcceptable(Record("shop-1"), null));
        Assert.True(JobOwnershipValidator.IsAcceptable(Record("shop-1"), ""));
    }

    [Fact]
    public void Job_without_shop_id_is_tolerated()
    {
        Assert.True(JobOwnershipValidator.IsAcceptable(Record(""), "shop-1"));
    }

    [Fact]
    public void Comparison_is_case_insensitive()
    {
        Assert.True(JobOwnershipValidator.IsAcceptable(Record("SHOP-1"), "shop-1"));
    }
}
