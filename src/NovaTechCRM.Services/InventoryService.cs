using Microsoft.Extensions.Logging;
using NovaTechCRM.Domain.Events;
using NovaTechCRM.Domain.Exceptions;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;
using NovaTechCRM.Services.Interfaces;

namespace NovaTechCRM.Services;

public class InventoryService : IInventoryService
{
    private readonly IInventoryRepository _inventoryRepo;
    private readonly IAuditService _audit;
    private readonly ILogger<InventoryService> _logger;
    private static readonly List<Action<InventoryReservedEvent>> _reservationHandlers = new();
    private static readonly TimeSpan ReservationHold = TimeSpan.FromMinutes(15);

    public InventoryService(
        IInventoryRepository inventoryRepo,
        IAuditService audit,
        ILogger<InventoryService> logger)
    {
        _inventoryRepo = inventoryRepo;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Inventory?> GetBySkuAsync(
        string sku, string? warehouseId = null, CancellationToken ct = default)
    {
        return await _inventoryRepo.GetBySkuAsync(sku, warehouseId, ct);
    }

    public async Task<IReadOnlyList<Inventory>> GetLowStockAsync(CancellationToken ct = default)
    {
        return await _inventoryRepo.GetLowStockAsync(threshold: 10, ct);
    }

    public async Task<bool> IsAvailableAsync(
        string sku, int quantity, CancellationToken ct = default)
    {
        var inv = await _inventoryRepo.GetBySkuAsync(sku, null, ct);
        return inv != null && inv.QuantityAvailable >= quantity;
    }

    // NOVA-61: atomic conditional UPDATE + reservation/ledger in one DB transaction.
    public async Task ReserveStockAsync(
        string sku, int quantity, Guid orderId, CancellationToken ct = default)
    {
        if (quantity <= 0)
            throw new DomainException("INVALID_QUANTITY", "Reservation quantity must be positive.");

        var reservation = await _inventoryRepo.ReserveStockAtomicAsync(
            sku, quantity, orderId, ReservationHold, ct);

        if (reservation == null)
        {
            var current = await _inventoryRepo.GetBySkuAsync(sku, null, ct);
            throw new InsufficientInventoryException(
                sku, quantity, current?.QuantityAvailable ?? 0);
        }

        try
        {
            var evt = new InventoryReservedEvent(sku, quantity, orderId);
            foreach (var handler in _reservationHandlers)
                handler(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "InventoryReservedEvent handler failed after reserve of {Qty}x {Sku} for order {OrderId}",
                quantity, sku, orderId);
        }

        _logger.LogInformation(
            "Reserved {Qty}x {Sku} for order {OrderId}",
            quantity, sku, orderId);
    }

    public async Task ReleaseReservationAsync(Guid orderId, CancellationToken ct = default)
    {
        var reservations = await _inventoryRepo.GetReservationsByOrderAsync(orderId, ct);

        foreach (var reservation in reservations)
        {
            if (reservation.IsReleased) continue;

            var inv = await _inventoryRepo.GetBySkuAsync(reservation.ProductSku, null, ct);
            if (inv == null) continue;

            inv.QuantityReserved = Math.Max(0, inv.QuantityReserved - reservation.Quantity);
            inv.LastUpdatedAt    = DateTime.UtcNow;

            await _inventoryRepo.UpdateAsync(inv, ct);

            reservation.IsReleased = true;
            reservation.ReleasedAt = DateTime.UtcNow;
            await _inventoryRepo.UpdateReservationAsync(reservation, ct);

            await _inventoryRepo.AddTransactionAsync(new InventoryTransaction
            {
                ProductSku      = reservation.ProductSku,
                InventoryId     = inv.Id,
                Type            = InventoryTransactionType.Released,
                QuantityDelta   = reservation.Quantity,
                QuantityBefore  = inv.QuantityAvailable - reservation.Quantity,
                QuantityAfter   = inv.QuantityAvailable,
                OrderId         = orderId,
                CreatedByUserId = "system"
            }, ct);
        }

        _logger.LogInformation("Released inventory reservations for order {OrderId}", orderId);
    }

    public async Task CommitReservationAsync(Guid orderId, CancellationToken ct = default)
    {
        var reservations = await _inventoryRepo.GetReservationsByOrderAsync(orderId, ct);

        foreach (var reservation in reservations.Where(r => !r.IsReleased))
        {
            var inv = await _inventoryRepo.GetBySkuAsync(reservation.ProductSku, null, ct);
            if (inv == null) continue;

            inv.QuantityOnHand   -= reservation.Quantity;
            inv.QuantityReserved -= reservation.Quantity;
            inv.LastUpdatedAt     = DateTime.UtcNow;

            await _inventoryRepo.UpdateAsync(inv, ct);

            await _inventoryRepo.AddTransactionAsync(new InventoryTransaction
            {
                ProductSku      = reservation.ProductSku,
                InventoryId     = inv.Id,
                Type            = InventoryTransactionType.Sale,
                QuantityDelta   = -reservation.Quantity,
                QuantityBefore  = inv.QuantityOnHand + reservation.Quantity,
                QuantityAfter   = inv.QuantityOnHand,
                OrderId         = orderId,
                CreatedByUserId = "system"
            }, ct);

            if (inv.IsLowStock)
            {
                _logger.LogWarning(
                    "Low stock alert: {Sku} has {Available} units remaining (reorder point: {Reorder})",
                    reservation.ProductSku, inv.QuantityAvailable, inv.ReorderPoint);
            }

            reservation.IsReleased = true;
            reservation.ReleasedAt = DateTime.UtcNow;
            await _inventoryRepo.UpdateReservationAsync(reservation, ct);
        }
    }

    public async Task AdjustAsync(
        string sku, int delta, string reason, string userId, CancellationToken ct = default)
    {
        var inv = await _inventoryRepo.GetBySkuAsync(sku, null, ct)
            ?? throw new DomainException("INVENTORY_NOT_FOUND", $"No inventory record for SKU '{sku}'.");

        var before = inv.QuantityOnHand;
        inv.QuantityOnHand        += delta;
        inv.LastUpdatedAt          = DateTime.UtcNow;
        inv.LastUpdatedByUserId    = userId;

        if (inv.QuantityOnHand < 0)
            throw new DomainException("INVENTORY_NEGATIVE", "Adjustment would result in negative stock.");

        await _inventoryRepo.UpdateAsync(inv, ct);

        await _inventoryRepo.AddTransactionAsync(new InventoryTransaction
        {
            ProductSku      = sku,
            InventoryId     = inv.Id,
            Type            = InventoryTransactionType.Adjustment,
            QuantityDelta   = delta,
            QuantityBefore  = before,
            QuantityAfter   = inv.QuantityOnHand,
            Notes           = reason,
            CreatedByUserId = userId
        }, ct);

        await _audit.LogAsync(AuditAction.Updated, "Inventory", inv.Id.ToString(), userId,
            oldValues: new { QuantityOnHand = before },
            newValues: new { inv.QuantityOnHand }, ct: ct);
    }

    public async Task<IReadOnlyList<InventoryTransaction>> GetTransactionsAsync(
        string sku, CancellationToken ct = default)
    {
        return await _inventoryRepo.GetTransactionsAsync(sku, ct);
    }

    public async Task<IReadOnlyList<StockAlert>> GetActiveAlertsAsync(CancellationToken ct = default)
    {
        var lowStock = await _inventoryRepo.GetLowStockAsync(ct: ct);

        return lowStock.Select(inv => new StockAlert
        {
            ProductSku         = inv.ProductSku,
            QuantityAvailable  = inv.QuantityAvailable,
            ReorderPoint       = inv.ReorderPoint,
            GeneratedAt        = DateTime.UtcNow
        }).ToList();
    }
}
