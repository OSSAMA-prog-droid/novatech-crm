using Microsoft.Extensions.Logging;
using NovaTechCRM.Domain.Exceptions;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;
using NovaTechCRM.Services.Interfaces;

namespace NovaTechCRM.Services;

public class OrderService
{
    private readonly IOrderRepository _orderRepo;
    private readonly IFraudShieldService _fraudShield;
    private readonly INotificationService _notifications;
    private readonly IInventoryService _inventory;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IOrderRepository orderRepo,
        IFraudShieldService fraudShield,
        INotificationService notifications,
        IInventoryService inventory,
        ILogger<OrderService> logger)
    {
        _orderRepo = orderRepo;
        _fraudShield = fraudShield;
        _notifications = notifications;
        _inventory = inventory;
        _logger = logger;
    }

    public async Task<Order> PlaceOrderAsync(Order order, CancellationToken ct = default)
    {
        order.Status = OrderStatus.FraudCheckPending;
        await _orderRepo.SaveAsync(order, ct);

        var fraudResult = await _fraudShield.CheckAsync(order, ct);

        if (!fraudResult.Passed)
        {
            order.Status = OrderStatus.Rejected;
            order.FraudCheckPassed = false;
            await _orderRepo.SaveAsync(order, ct);
            await _notifications.SendFraudAlertAsync(order, fraudResult, ct);
            _logger.LogWarning("Order {OrderId} rejected by FraudShield: risk={RiskLevel}, reason={Reason}",
                order.Id, fraudResult.RiskLevel, fraudResult.Reason);
            return order;
        }

        order.FraudCheckId = fraudResult.CheckId;
        order.FraudCheckPassed = true;

        try
        {
            foreach (var item in order.Items.Where(i => i.Quantity > 0))
            {
                await _inventory.ReserveStockAsync(item.ProductSku, item.Quantity, order.Id, ct);
            }
        }
        catch (InsufficientInventoryException ex)
        {
            await _inventory.ReleaseReservationAsync(order.Id, ct);
            order.Status = OrderStatus.Rejected;
            await _orderRepo.SaveAsync(order, ct);
            _logger.LogWarning(
                "Order {OrderId} rejected — insufficient stock for {Sku} (requested {Requested}, available {Available})",
                order.Id, ex.ProductSku, ex.Requested, ex.Available);
            return order;
        }

        await FulfillOrderAsync(order, ct);
        return order;
    }

    private async Task FulfillOrderAsync(Order order, CancellationToken ct = default)
    {
        await _inventory.CommitReservationAsync(order.Id, ct);

        order.Status = OrderStatus.Fulfilled;
        order.FulfilledAt = DateTime.UtcNow;
        await _orderRepo.SaveAsync(order, ct);

        await _notifications.SendOrderConfirmationAsync(order, ct);

        _logger.LogInformation("Order {OrderId} fulfilled for customer {CustomerId} (amount: {Amount:C})",
            order.Id, order.CustomerId, order.TotalAmount);
    }

    public async Task<Order?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        return await _orderRepo.GetByIdAsync(orderId, ct);
    }

    public async Task<IReadOnlyList<Order>> GetCustomerOrdersAsync(string customerId, CancellationToken ct = default)
    {
        return await _orderRepo.GetByCustomerAsync(customerId, ct);
    }
}
