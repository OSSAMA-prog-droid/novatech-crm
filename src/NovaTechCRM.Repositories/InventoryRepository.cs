using Microsoft.EntityFrameworkCore;
using NovaTechCRM.Domain.Models;

namespace NovaTechCRM.Repositories;

public class InventoryRepository : IInventoryRepository
{
    private readonly DbContext _db;

    public InventoryRepository(DbContext db) => _db = db;

    private DbSet<Inventory> Inventory => _db.Set<Inventory>();
    private DbSet<InventoryReservation> Reservations => _db.Set<InventoryReservation>();
    private DbSet<InventoryTransaction> Transactions => _db.Set<InventoryTransaction>();

    public async Task<Inventory?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await Inventory.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<Inventory?> GetBySkuAsync(
        string sku, string? warehouseId = null, CancellationToken ct = default)
    {
        var q = Inventory.AsNoTracking().Where(i => i.ProductSku == sku);

        if (!string.IsNullOrEmpty(warehouseId))
            q = q.Where(i => i.WarehouseId == warehouseId);

        return await q
            .OrderBy(i => i.WarehouseId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Inventory?> GetByProductAsync(
        Guid productId, Guid? variantId, string? warehouseId, CancellationToken ct = default)
    {
        var q = Inventory.AsNoTracking().Where(i => i.ProductId == productId);

        if (variantId.HasValue)
            q = q.Where(i => i.VariantId == variantId.Value);

        if (!string.IsNullOrEmpty(warehouseId))
            q = q.Where(i => i.WarehouseId == warehouseId);

        return await q.FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<Inventory>> GetByWarehouseAsync(
        string warehouseId, CancellationToken ct = default)
        => await Inventory
            .AsNoTracking()
            .Where(i => i.WarehouseId == warehouseId)
            .OrderBy(i => i.ProductId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Inventory>> GetLowStockAsync(
        int threshold = 10, CancellationToken ct = default)
        => await Inventory
            .AsNoTracking()
            .Where(i => i.QuantityOnHand - i.QuantityReserved <= threshold)
            .OrderBy(i => i.QuantityOnHand - i.QuantityReserved)
            .ToListAsync(ct);

    // NOVA-61: conditional UPDATE + reservation + ledger in one transaction.
    public async Task<InventoryReservation?> ReserveStockAtomicAsync(
        string sku,
        int quantity,
        Guid orderId,
        TimeSpan holdDuration,
        CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var inv = await Inventory
                .Where(i => i.ProductSku == sku)
                .OrderBy(i => i.WarehouseId)
                .FirstOrDefaultAsync(ct);

            if (inv == null)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            var availableBefore = inv.QuantityOnHand - inv.QuantityReserved;

            var updated = await Inventory
                .Where(i => i.Id == inv.Id
                            && i.QuantityOnHand - i.QuantityReserved >= quantity)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.QuantityReserved, i => i.QuantityReserved + quantity)
                    .SetProperty(i => i.LastUpdatedAt, _ => DateTime.UtcNow),
                    ct);

            if (updated != 1)
            {
                await tx.RollbackAsync(ct);
                return null;
            }

            var reservation = new InventoryReservation
            {
                ProductSku  = sku,
                InventoryId = inv.Id,
                OrderId     = orderId,
                Quantity    = quantity,
                ExpiresAt   = DateTime.UtcNow.Add(holdDuration),
            };
            Reservations.Add(reservation);

            Transactions.Add(new InventoryTransaction
            {
                ProductSku      = sku,
                InventoryId     = inv.Id,
                WarehouseId     = inv.WarehouseId,
                Type            = InventoryTransactionType.Reserved,
                QuantityDelta   = -quantity,
                QuantityBefore  = availableBefore,
                QuantityAfter   = availableBefore - quantity,
                OrderId         = orderId,
                CreatedByUserId = "system",
                CreatedAt       = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return reservation;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<Inventory> UpdateAsync(Inventory inventory, CancellationToken ct = default)
    {
        Inventory.Update(inventory);
        await _db.SaveChangesAsync(ct);
        return inventory;
    }

    public async Task<InventoryReservation> CreateReservationAsync(
        InventoryReservation reservation, CancellationToken ct = default)
    {
        Reservations.Add(reservation);
        await _db.SaveChangesAsync(ct);
        return reservation;
    }

    public async Task<InventoryReservation?> GetReservationAsync(
        Guid reservationId, CancellationToken ct = default)
        => await Reservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);

    public async Task<IReadOnlyList<InventoryReservation>> GetReservationsByOrderAsync(
        Guid orderId, CancellationToken ct = default)
        => await Reservations
            .Where(r => r.OrderId == orderId)
            .ToListAsync(ct);

    public async Task UpdateReservationAsync(
        InventoryReservation reservation, CancellationToken ct = default)
    {
        Reservations.Update(reservation);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteReservationAsync(Guid reservationId, CancellationToken ct = default)
    {
        await Reservations
            .Where(r => r.Id == reservationId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteExpiredReservationsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await Reservations
            .Where(r => r.ExpiresAt < now)
            .ExecuteDeleteAsync(ct);
    }

    public async Task AddTransactionAsync(
        InventoryTransaction transaction, CancellationToken ct = default)
    {
        Transactions.Add(transaction);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<InventoryTransaction>> GetTransactionsAsync(
        string sku, CancellationToken ct = default)
        => await Transactions
            .AsNoTracking()
            .Where(t => t.ProductSku == sku)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
}
