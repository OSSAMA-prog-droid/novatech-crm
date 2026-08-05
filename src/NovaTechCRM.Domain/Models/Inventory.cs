namespace NovaTechCRM.Domain.Models;

public enum InventoryTransactionType
{
    Purchase,       // stock received from supplier
    Sale,           // stock consumed by order
    Return,         // customer returned item
    Adjustment,     // manual correction
    Reserved,       // held for pending order
    Released,       // reservation cancelled
    Transfer,       // moved between warehouses
    Shrinkage,      // damaged/lost/stolen
    WriteOff
}

public class Inventory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ProductSku { get; set; } = string.Empty;
    public Guid? ProductId { get; set; }
    public Guid? VariantId { get; set; }

    // which warehouse — null means default/only warehouse
    public string? WarehouseId { get; set; }
    public string? WarehouseName { get; set; }

    public int QuantityOnHand { get; set; }

    // QuantityReserved must be changed via ReserveStockAtomicAsync so
    // concurrent reservations cannot oversell under load (NOVA-61).
    public int QuantityReserved { get; set; }

    public int QuantityAvailable => QuantityOnHand - QuantityReserved;

    public int ReorderPoint { get; set; } = 10;
    public int ReorderQuantity { get; set; } = 50;

    public bool IsLowStock => QuantityAvailable <= ReorderPoint;
    public bool IsOutOfStock => QuantityAvailable <= 0;

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCountedAt { get; set; }

    public string? LastUpdatedByUserId { get; set; }

    // not persisted — computed by service layer
    public List<InventoryTransaction> RecentTransactions { get; set; } = new();
}

public class InventoryTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ProductSku { get; set; } = string.Empty;
    public Guid? InventoryId { get; set; }
    public string? WarehouseId { get; set; }

    public InventoryTransactionType Type { get; set; }

    // positive = stock in, negative = stock out
    public int QuantityDelta { get; set; }
    public int QuantityBefore { get; set; }
    public int QuantityAfter { get; set; }

    // reference to what caused this transaction
    public Guid? OrderId { get; set; }
    public Guid? ShipmentId { get; set; }
    public string? PurchaseOrderNumber { get; set; }

    public string? Notes { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // unit cost at time of transaction — for COGS tracking
    public decimal? UnitCost { get; set; }
}

public class InventoryReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProductSku { get; set; } = string.Empty;
    public Guid? InventoryId { get; set; }
    public Guid OrderId { get; set; }
    public int Quantity { get; set; }
    public DateTime ReservedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public bool IsReleased { get; set; }
    public DateTime? ReleasedAt { get; set; }
}

public class StockAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProductSku { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public int QuantityAvailable { get; set; }
    public int ReorderPoint { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public bool IsAcknowledged { get; set; }
    public string? AcknowledgedByUserId { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
}

public class Warehouse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? AddressLine1 { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
}
