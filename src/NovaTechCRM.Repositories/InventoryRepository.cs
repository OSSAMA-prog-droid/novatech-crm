using Microsoft.EntityFrameworkCore;
using NovaTechCRM.Domain.Models;

namespace NovaTechCRM.Repositories;

public class InventoryRepository : IInventoryRepository
{
    private readonly NovaTechDbContext _db;

    public InventoryRepository(NovaTechDbContext db) => _db = db;

    public async Task<Inventory?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.Inventory.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<Inventory?> GetByProductAsync(
        Guid productId, Guid? variantId, string? warehouseId, CancellationToken ct = default)
    {
        var q = _db.Inventory.AsNoTracking().Where(i => i.ProductId == productId);

        if (variantId.HasValue)
            q = q.Where(i => i.VariantId == variantId.Value);

        if (!string.IsNullOrEmpty(warehouseId))
            q = q.Where(i => i.WarehouseId == warehouseId);

        return await q.FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<Inventory>> GetByWarehouseAsync(
        string warehouseId, CancellationToken ct = default)
        => await _db.Inventory
            .AsNoTracking()
            .Where(i => i.WarehouseId == warehouseId)
            .OrderBy(i => i.ProductId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Inventory>> GetLowStockAsync(
        int threshold, CancellationToken ct = default)
        => await _db.Inventory
            .AsNoTracking()
            .Where(i => i.QuantityAvailable <= threshold && !i.IsDiscontinued)
            .OrderBy(i => i.QuantityAvailable)
            .ToListAsync(ct);

    // Full-entity EF Core update used by the release/commit/adjust paths.
    // Reserve no longer flows through here — see TryReserveAsync for the atomic path (NOVA-61).
    public async Task<Inventory> UpdateAsync(Inventory inventory, CancellationToken ct = default)
    {
        _db.Inventory.Update(inventory);
        await _db.SaveChangesAsync(ct);
        return inventory;
    }
    
    public async Task<bool> TryReserveAsync(
        string sku, string? warehouseId, int quantity, CancellationToken ct = default)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(quantity), "Reservation quantity must be positive.");

        var rowsAffected = await _db.Inventory
            .Where(i => i.ProductSku == sku
                     && (warehouseId == null || i.WarehouseId == warehouseId)
                     && i.QuantityOnHand - i.QuantityReserved >= quantity)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.QuantityReserved, i => i.QuantityReserved + quantity)
                .SetProperty(i => i.LastUpdatedAt, i => DateTime.UtcNow), ct);

        return rowsAffected > 0;
    }

    public async Task<InventoryReservation> CreateReservationAsync(
        InventoryReservation reservation, CancellationToken ct = default)
    {
        _db.InventoryReservations.Add(reservation);
        await _db.SaveChangesAsync(ct);
        return reservation;
    }

    public async Task<InventoryReservation?> GetReservationAsync(
        Guid reservationId, CancellationToken ct = default)
        => await _db.InventoryReservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);

    public async Task DeleteReservationAsync(Guid reservationId, CancellationToken ct = default)
    {
        await _db.InventoryReservations
            .Where(r => r.Id == reservationId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task DeleteExpiredReservationsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await _db.InventoryReservations
            .Where(r => r.ExpiresAt < now)
            .ExecuteDeleteAsync(ct);
    }
}
