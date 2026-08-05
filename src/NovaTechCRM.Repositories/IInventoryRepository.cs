using NovaTechCRM.Domain.Models;

namespace NovaTechCRM.Repositories;

public interface IInventoryRepository
{
    Task<Inventory?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Inventory?> GetBySkuAsync(string sku, string? warehouseId = null, CancellationToken ct = default);
    Task<Inventory?> GetByProductAsync(
        Guid productId, Guid? variantId, string? warehouseId, CancellationToken ct = default);
    Task<IReadOnlyList<Inventory>> GetByWarehouseAsync(
        string warehouseId, CancellationToken ct = default);
    Task<IReadOnlyList<Inventory>> GetLowStockAsync(
        int threshold = 10, CancellationToken ct = default);

    /// <summary>
    /// Atomically reserves stock on one inventory row and writes the reservation
    /// + ledger entry in the same database transaction.
    /// Returns null if the SKU is missing or stock is insufficient.
    /// </summary>
    Task<InventoryReservation?> ReserveStockAtomicAsync(
        string sku,
        int quantity,
        Guid orderId,
        TimeSpan holdDuration,
        CancellationToken ct = default);

    Task<Inventory> UpdateAsync(Inventory inventory, CancellationToken ct = default);
    Task<InventoryReservation> CreateReservationAsync(
        InventoryReservation reservation, CancellationToken ct = default);
    Task<InventoryReservation?> GetReservationAsync(Guid reservationId, CancellationToken ct = default);
    Task<IReadOnlyList<InventoryReservation>> GetReservationsByOrderAsync(
        Guid orderId, CancellationToken ct = default);
    Task UpdateReservationAsync(InventoryReservation reservation, CancellationToken ct = default);
    Task DeleteReservationAsync(Guid reservationId, CancellationToken ct = default);
    Task DeleteExpiredReservationsAsync(CancellationToken ct = default);
    Task AddTransactionAsync(InventoryTransaction transaction, CancellationToken ct = default);
    Task<IReadOnlyList<InventoryTransaction>> GetTransactionsAsync(
        string sku, CancellationToken ct = default);
}
