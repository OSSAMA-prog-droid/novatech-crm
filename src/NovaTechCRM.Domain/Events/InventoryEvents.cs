namespace NovaTechCRM.Domain.Events;

public class InventoryReservedEvent : DomainEvent
{
    public string ProductSku { get; }
    public int QuantityReserved { get; }
    public Guid OrderId { get; }

    public InventoryReservedEvent(string sku, int qty, Guid orderId)
    {
        ProductSku = sku;
        QuantityReserved = qty;
        OrderId = orderId;
    }
}

public class InventoryLowStockEvent : DomainEvent
{
    public string ProductSku { get; }
    public int QuantityAvailable { get; }
    public int ReorderPoint { get; }

    public InventoryLowStockEvent(string sku, int available, int reorderPoint)
    {
        ProductSku = sku;
        QuantityAvailable = available;
        ReorderPoint = reorderPoint;
    }
}
