using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NovaTechCRM.Domain.Exceptions;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;
using NovaTechCRM.Services;
using NovaTechCRM.Services.Interfaces;
using Xunit;

namespace NovaTechCRM.Tests;

public class OrderServiceTests
{
    private readonly Mock<IOrderRepository> _repoMock = new();
    private readonly Mock<IFraudShieldService> _fraudMock = new();
    private readonly Mock<INotificationService> _notifMock = new();
    private readonly Mock<IInventoryService> _inventoryMock = new();

    private OrderService CreateSut() => new(
        _repoMock.Object,
        _fraudMock.Object,
        _notifMock.Object,
        _inventoryMock.Object,
        NullLogger<OrderService>.Instance);

    public OrderServiceTests()
    {
        _repoMock
            .Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task PlaceOrder_HighRiskOrder_ShouldNotFulfill()
    {
        var order = new Order
        {
            CustomerId = "cust-001",
            TotalAmount = 9999m,
            Items = new List<OrderItem>
            {
                new() { ProductSku = "SKU-X", ProductName = "Expensive Item", Quantity = 1, UnitPrice = 9999m }
            }
        };

        _fraudMock
            .Setup(f => f.CheckAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FraudCheckResult
            {
                CheckId = "chk-001",
                Passed = false,
                RiskLevel = FraudRiskLevel.Critical,
                Reason = "Amount exceeds threshold"
            });

        var result = await CreateSut().PlaceOrderAsync(order);

        Assert.Equal(OrderStatus.Rejected, result.Status);
        _inventoryMock.Verify(
            i => i.ReserveStockAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PlaceOrder_LowRiskOrder_WithStock_ShouldFulfill()
    {
        var order = new Order
        {
            CustomerId = "cust-002",
            TotalAmount = 49.99m,
            Items = new List<OrderItem>
            {
                new() { ProductSku = "SKU-A", ProductName = "Widget", Quantity = 1, UnitPrice = 49.99m }
            }
        };

        _fraudMock
            .Setup(f => f.CheckAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FraudCheckResult
            {
                CheckId = "chk-002",
                Passed = true,
                RiskLevel = FraudRiskLevel.Low,
                Reason = "Automated check passed"
            });

        _inventoryMock
            .Setup(i => i.ReserveStockAsync("SKU-A", 1, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _inventoryMock
            .Setup(i => i.CommitReservationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _notifMock
            .Setup(n => n.SendOrderConfirmationAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await CreateSut().PlaceOrderAsync(order);

        Assert.Equal(OrderStatus.Fulfilled, result.Status);
        _inventoryMock.Verify(
            i => i.ReserveStockAsync("SKU-A", 1, order.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        _inventoryMock.Verify(
            i => i.CommitReservationAsync(order.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaceOrder_InsufficientStock_ShouldRejectAndRelease()
    {
        var order = new Order
        {
            CustomerId = "cust-003",
            TotalAmount = 100m,
            Items = new List<OrderItem>
            {
                new() { ProductSku = "SKU-8821", ProductName = "Flash Sale Item", Quantity = 5, UnitPrice = 20m }
            }
        };

        _fraudMock
            .Setup(f => f.CheckAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FraudCheckResult { CheckId = "chk-003", Passed = true, RiskLevel = FraudRiskLevel.Low });

        _inventoryMock
            .Setup(i => i.ReserveStockAsync("SKU-8821", 5, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InsufficientInventoryException("SKU-8821", 5, 0));
        _inventoryMock
            .Setup(i => i.ReleaseReservationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await CreateSut().PlaceOrderAsync(order);

        Assert.Equal(OrderStatus.Rejected, result.Status);
        _inventoryMock.Verify(
            i => i.ReleaseReservationAsync(order.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        _notifMock.Verify(
            n => n.SendOrderConfirmationAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
