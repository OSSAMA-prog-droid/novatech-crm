using NovaTechCRM.Domain.Models;

namespace NovaTechCRM.Repositories;

public interface IInventoryRepository
{
    Task<Inventory?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Inventory?> GetByProductAsync(
        Guid productId, Guid? variantId, string? warehouseId, CancellationToken ct = default);
    Task<IReadOnlyList<Inventory>> GetByWarehouseAsync(
        string warehouseId, CancellationToken ct = default);
    Task<IReadOnlyList<Inventory>> GetLowStockAsync(
        int threshold, CancellationToken ct = default);
    Task<Inventory> UpdateAsync(Inventory inventory, CancellationToken ct = default);
    
    Task<bool> TryReserveAsync(
        string sku, string? warehouseId, int quantity, CancellationToken ct = default);

    Task<InventoryReservation> CreateReservationAsync(
        InventoryReservation reservation, CancellationToken ct = default);
    Task<InventoryReservation?> GetReservationAsync(Guid reservationId, CancellationToken ct = default);
    Task DeleteReservationAsync(Guid reservationId, CancellationToken ct = default);
    Task DeleteExpiredReservationsAsync(CancellationToken ct = default);
}
