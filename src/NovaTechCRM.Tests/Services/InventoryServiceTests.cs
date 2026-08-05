using Moq;
using Xunit;
using Microsoft.Extensions.Logging;
using NovaTechCRM.Domain.Exceptions;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;
using NovaTechCRM.Services;
using NovaTechCRM.Services.Interfaces;

namespace NovaTechCRM.Tests.Services;

public class InventoryServiceTests
{
    private readonly Mock<IInventoryRepository>      _repo   = new();
    private readonly Mock<IAuditService>             _audit  = new();
    private readonly Mock<ILogger<InventoryService>> _logger = new();

    private InventoryService CreateSut() => new(_repo.Object, _audit.Object, _logger.Object);

    private static Inventory StockedItem(string sku = "SKU-8821", int onHand = 10, int reserved = 0) => new()
    {
        Id               = Guid.NewGuid(),
        ProductSku       = sku,
        ProductId        = Guid.NewGuid(),
        WarehouseId      = "WH-US-EAST",
        QuantityOnHand   = onHand,
        QuantityReserved = reserved,
    };

    [Fact]
    public async Task ReserveStockAsync_SucceedsWhenStockAvailable()
    {
        var inv = StockedItem(onHand: 10);
        var orderId = Guid.NewGuid();
        var reservation = new InventoryReservation
        {
            ProductSku  = inv.ProductSku,
            InventoryId = inv.Id,
            OrderId     = orderId,
            Quantity    = 3,
        };

        _repo.Setup(r => r.ReserveStockAtomicAsync(
                inv.ProductSku, 3, orderId, It.IsAny<TimeSpan>(), default))
            .ReturnsAsync(reservation);

        await CreateSut().ReserveStockAsync(inv.ProductSku, 3, orderId);

        _repo.Verify(r => r.ReserveStockAtomicAsync(
            inv.ProductSku, 3, orderId, It.IsAny<TimeSpan>(), default), Times.Once);
    }

    [Fact]
    public async Task ReserveStockAsync_Throws_WhenInsufficientStock()
    {
        var inv = StockedItem(onHand: 2);
        var orderId = Guid.NewGuid();

        _repo.Setup(r => r.ReserveStockAtomicAsync(
                inv.ProductSku, 5, orderId, It.IsAny<TimeSpan>(), default))
            .ReturnsAsync((InventoryReservation?)null);
        _repo.Setup(r => r.GetBySkuAsync(inv.ProductSku, null, default)).ReturnsAsync(inv);

        await Assert.ThrowsAsync<InsufficientInventoryException>(
            () => CreateSut().ReserveStockAsync(inv.ProductSku, 5, orderId));
    }

    [Fact]
    public async Task ReserveStockAsync_Throws_WhenInventoryNotFound()
    {
        const string sku = "MISSING-SKU";
        var orderId = Guid.NewGuid();

        _repo.Setup(r => r.ReserveStockAtomicAsync(
                sku, 1, orderId, It.IsAny<TimeSpan>(), default))
            .ReturnsAsync((InventoryReservation?)null);
        _repo.Setup(r => r.GetBySkuAsync(sku, null, default)).ReturnsAsync((Inventory?)null);

        await Assert.ThrowsAsync<InsufficientInventoryException>(
            () => CreateSut().ReserveStockAsync(sku, 1, orderId));
    }

    [Fact]
    public async Task ReserveStockAsync_Throws_WhenQuantityInvalid()
    {
        await Assert.ThrowsAsync<DomainException>(
            () => CreateSut().ReserveStockAsync("SKU-1", 0, Guid.NewGuid()));
    }

    [Fact]
    public async Task ReserveStockAsync_NOVA61_ConcurrentReservationsCannotOverCommitStock()
    {
        const string sku = "SKU-8821";
        const int onHand = 5;
        var reserved = 0;
        var gate = new object();
        var successes = 0;
        var failures = 0;

        _repo.Setup(r => r.ReserveStockAtomicAsync(
                sku, It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<TimeSpan>(), default))
            .ReturnsAsync((string _, int qty, Guid orderId, TimeSpan __, CancellationToken ____) =>
            {
                lock (gate)
                {
                    if (onHand - reserved < qty)
                        return null;
                    reserved += qty;
                    return new InventoryReservation
                    {
                        ProductSku  = sku,
                        InventoryId = Guid.NewGuid(),
                        OrderId     = orderId,
                        Quantity    = qty,
                    };
                }
            });

        _repo.Setup(r => r.GetBySkuAsync(sku, null, default))
            .ReturnsAsync(() => StockedItem(sku, onHand, reserved));

        var sut = CreateSut();
        var t1 = Task.Run(async () =>
        {
            try
            {
                await sut.ReserveStockAsync(sku, 4, Guid.NewGuid());
                Interlocked.Increment(ref successes);
            }
            catch (InsufficientInventoryException)
            {
                Interlocked.Increment(ref failures);
            }
        });
        var t2 = Task.Run(async () =>
        {
            try
            {
                await sut.ReserveStockAsync(sku, 4, Guid.NewGuid());
                Interlocked.Increment(ref successes);
            }
            catch (InsufficientInventoryException)
            {
                Interlocked.Increment(ref failures);
            }
        });

        await Task.WhenAll(t1, t2);

        Assert.Equal(1, successes);
        Assert.Equal(1, failures);
        Assert.Equal(4, reserved);
    }

    [Fact]
    public async Task ReleaseReservationAsync_RestoresQuantity()
    {
        var orderId = Guid.NewGuid();
        var inv = StockedItem(onHand: 10, reserved: 3);

        var reservation = new InventoryReservation
        {
            Id          = Guid.NewGuid(),
            InventoryId = inv.Id,
            ProductSku  = inv.ProductSku,
            OrderId     = orderId,
            Quantity    = 3,
            ExpiresAt   = DateTime.UtcNow.AddMinutes(10),
        };

        _repo.Setup(r => r.GetReservationsByOrderAsync(orderId, default))
            .ReturnsAsync(new List<InventoryReservation> { reservation });
        _repo.Setup(r => r.GetBySkuAsync(inv.ProductSku, null, default)).ReturnsAsync(inv);
        _repo.Setup(r => r.UpdateAsync(It.IsAny<Inventory>(), default))
            .ReturnsAsync((Inventory i, CancellationToken _) => i);
        _repo.Setup(r => r.UpdateReservationAsync(It.IsAny<InventoryReservation>(), default))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddTransactionAsync(It.IsAny<InventoryTransaction>(), default))
            .Returns(Task.CompletedTask);

        await CreateSut().ReleaseReservationAsync(orderId);

        _repo.Verify(r => r.UpdateAsync(
            It.Is<Inventory>(i => i.QuantityReserved == 0),
            default), Times.Once);
    }

    [Fact]
    public async Task GetLowStockAsync_ReturnsItemsBelowThreshold()
    {
        var low = StockedItem(onHand: 2);
        _repo.Setup(r => r.GetLowStockAsync(10, default))
            .ReturnsAsync(new List<Inventory> { low });

        var result = await CreateSut().GetLowStockAsync();

        Assert.Single(result);
        Assert.Equal(2, result[0].QuantityAvailable);
    }
}
