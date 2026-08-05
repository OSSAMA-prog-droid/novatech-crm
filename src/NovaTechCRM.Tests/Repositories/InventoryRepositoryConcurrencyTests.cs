using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;
using Xunit;

namespace NovaTechCRM.Tests.Repositories;

public class InventoryRepositoryConcurrencyTests
{
    private sealed class InventoryTestDbContext : DbContext
    {
        public InventoryTestDbContext(DbContextOptions options) : base(options) { }

        public DbSet<Inventory> Inventory => Set<Inventory>();
        public DbSet<InventoryReservation> InventoryReservations => Set<InventoryReservation>();
        public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();

        protected override void OnModelCreating(ModelBuilder mb)
        {
            mb.Entity<Inventory>(e =>
            {
                e.HasKey(i => i.Id);
                e.Ignore(i => i.RecentTransactions);
                e.Ignore(i => i.QuantityAvailable);
                e.Ignore(i => i.IsLowStock);
                e.Ignore(i => i.IsOutOfStock);
            });
            mb.Entity<InventoryReservation>().HasKey(r => r.Id);
            mb.Entity<InventoryTransaction>().HasKey(t => t.Id);
        }
    }

    [Fact]
    public async Task ReserveStockAtomicAsync_ConcurrentRequests_CannotOverCommit()
    {
        var connectionString = $"Data Source=file:inv-{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var inventoryId = Guid.NewGuid();

        await using (var setup = CreateDb(connectionString))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Inventory.Add(new Inventory
            {
                Id               = inventoryId,
                ProductSku       = "SKU-8821",
                WarehouseId      = "WH-US-EAST",
                QuantityOnHand   = 200,
                QuantityReserved = 0,
            });
            await setup.SaveChangesAsync();
        }

        var tasks = Enumerable.Range(0, 340).Select(async _ =>
        {
            await using var db = CreateDb(connectionString);
            return await new InventoryRepository(db).ReserveStockAtomicAsync(
                "SKU-8821", 1, Guid.NewGuid(), TimeSpan.FromMinutes(15));
        });

        var results = await Task.WhenAll(tasks);
        Assert.Equal(200, results.Count(r => r != null));

        await using var verify = CreateDb(connectionString);
        var row = await verify.Inventory.AsNoTracking().SingleAsync(i => i.Id == inventoryId);
        Assert.Equal(200, row.QuantityReserved);
        Assert.Equal(200, await verify.InventoryReservations.CountAsync());
        Assert.Equal(200, await verify.InventoryTransactions.CountAsync());
    }

    private static InventoryTestDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<InventoryTestDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new InventoryTestDbContext(options);
    }
}
