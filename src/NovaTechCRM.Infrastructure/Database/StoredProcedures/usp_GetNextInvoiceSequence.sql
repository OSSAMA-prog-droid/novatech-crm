-- =============================================================================
-- usp_GetNextInvoiceSequence
--
-- Returns the next invoice sequence number for a given year. Invoice numbers are
-- formatted as INV-<year>-<sequence>, e.g. INV-2026-00034.
--
-- Called by: NovaTechCRM.Repositories.InvoiceRepository.GetNextSequenceAsync
-- Backing table: dbo.InvoiceSequences (see Tables/InvoiceSequences.sql)
-- Added in migration v11.
-- =============================================================================
CREATE OR ALTER PROCEDURE dbo.usp_GetNextInvoiceSequence
    @Year INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- NOVA-102: The previous implementation read MAX(SequenceNumber)+1 into a
    -- variable and then wrote it back in a separate statement. Under concurrent
    -- load two sessions could read the same value before either wrote, so both
    -- returned the same sequence -> duplicate invoice numbers (INV-2026-00034 x2).
    --
    -- Fix: increment and read the value in a single atomic UPDATE ... OUTPUT
    -- statement. UPDLOCK + HOLDLOCK serialise concurrent callers for the same
    -- year (including the first-insert case, via a key-range lock), guaranteeing
    -- every caller receives a unique, gap-free sequence.

    DECLARE @next TABLE (SequenceNumber INT);

    BEGIN TRANSACTION;

    -- Atomically bump the counter and capture the new value.
    UPDATE dbo.InvoiceSequences WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
    SET    SequenceNumber = SequenceNumber + 1
    OUTPUT INSERTED.SequenceNumber INTO @next (SequenceNumber)
    WHERE  [Year] = @Year;

    -- First invoice of the year: no row existed yet. The HOLDLOCK above holds a
    -- key-range lock for @Year, so a concurrent first caller blocks here rather
    -- than racing us to insert a duplicate seed row.
    IF NOT EXISTS (SELECT 1 FROM @next)
        INSERT INTO dbo.InvoiceSequences ([Year], SequenceNumber)
        OUTPUT INSERTED.SequenceNumber INTO @next (SequenceNumber)
        VALUES (@Year, 1);

    COMMIT TRANSACTION;

    SELECT SequenceNumber AS NextSequence FROM @next;
END
GO
