-- =============================================================================
-- InventoryTransactions
--
-- Ledger of inventory movements. Required by ReserveStockAtomicAsync.
-- Run on SQL Server before deploying the NOVA-61 inventory reservation fix.
-- =============================================================================
IF OBJECT_ID(N'dbo.InventoryTransactions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventoryTransactions
    (
        Id                  UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_InventoryTransactions PRIMARY KEY,
        ProductSku          NVARCHAR(100)    NOT NULL,
        InventoryId         UNIQUEIDENTIFIER NULL,
        WarehouseId         NVARCHAR(64)     NULL,
        Type                INT              NOT NULL,
        QuantityDelta       INT              NOT NULL,
        QuantityBefore      INT              NOT NULL,
        QuantityAfter       INT              NOT NULL,
        OrderId             UNIQUEIDENTIFIER NULL,
        ShipmentId          UNIQUEIDENTIFIER NULL,
        PurchaseOrderNumber NVARCHAR(64)     NULL,
        Notes               NVARCHAR(MAX)    NULL,
        CreatedByUserId     NVARCHAR(100)    NOT NULL
            CONSTRAINT DF_InventoryTransactions_CreatedBy DEFAULT (N''),
        CreatedAt           DATETIME2        NOT NULL
            CONSTRAINT DF_InventoryTransactions_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UnitCost            DECIMAL(18, 2)   NULL
    );

    CREATE INDEX IX_InventoryTransactions_ProductSku
        ON dbo.InventoryTransactions (ProductSku);

    CREATE INDEX IX_InventoryTransactions_OrderId
        ON dbo.InventoryTransactions (OrderId);
END
GO
