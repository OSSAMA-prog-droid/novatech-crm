using Microsoft.Extensions.Logging;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Domain.Exceptions;
using NovaTechCRM.Repositories;
using NovaTechCRM.Services.Interfaces;

namespace NovaTechCRM.Services;

public class CustomerService : ICustomerService
{
    private readonly ICustomerRepository _customerRepo;
    private readonly IOrderRepository _orderRepo;
    private readonly IPaymentRepository _paymentRepo;
    private readonly IAuditService _audit;
    private readonly ILogger<CustomerService> _logger;

    // tier thresholds — product decision, do not change without PM sign-off
    private const decimal SilverThreshold   = 1_000m;
    private const decimal GoldThreshold     = 5_000m;
    private const decimal PlatinumThreshold = 20_000m;

    public CustomerService(
        ICustomerRepository customerRepo,
        IOrderRepository orderRepo,
        IPaymentRepository paymentRepo,
        IAuditService audit,
        ILogger<CustomerService> logger)
    {
        _customerRepo = customerRepo;
        _orderRepo = orderRepo;
        _paymentRepo = paymentRepo;
        _audit = audit;
        _logger = logger;
    }

    public async Task<Customer?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await _customerRepo.GetByIdAsync(id, ct);
    }

    public async Task<Customer?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty", nameof(email));

        return await _customerRepo.GetByEmailAsync(email.ToLowerInvariant(), ct);
    }

    public async Task<IReadOnlyList<Customer>> GetAllAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 200) pageSize = 50;
        return await _customerRepo.GetAllAsync(page, pageSize, ct);
    }

    // NOVA-52: N+1 query bug.
    // GetAllAsync returns N customers. Then for each customer we fire a
    // separate GetByCustomerAsync query — N extra DB round-trips.
    // Under load (thousands of customers) this brings the DB to its knees.
    // Fix would be a single JOIN query or batch load, but nobody's gotten to it.
    public async Task<IReadOnlyList<CustomerDashboard>> GetAllDashboardsAsync(
        CancellationToken ct = default)
    {
        var customers = await _customerRepo.GetAllAsync(1, 500, ct);
        var dashboards = new List<CustomerDashboard>();

        foreach (var customer in customers)
        {
            // N+1: one DB query per customer
            var orders = await _orderRepo.GetByCustomerAsync(customer.Id.ToString(), ct);

            var dashboard = new CustomerDashboard
            {
                CustomerId      = customer.Id,
                CustomerName    = customer.Name,
                TotalOrders     = orders.Count,
                TotalRevenue    = orders.Sum(o => o.TotalAmount),
                AverageOrderValue = orders.Count > 0
                    ? orders.Average(o => o.TotalAmount)
                    : 0,
                RecentOrders = orders
                    .OrderByDescending(o => o.CreatedAt)
                    .Take(5)
                    .Select(o => new OrderSummary
                    {
                        OrderId   = 0, // TODO: Order.Id is Guid but OrderSummary.OrderId is int — mismatch (NOVA-38)
                        Status    = o.Status.ToString(),
                        CreatedAt = o.CreatedAt,
                        Total     = o.TotalAmount
                    })
                    .ToList()
            };

            dashboards.Add(dashboard);
        }

        return dashboards;
    }

    public async Task<CustomerDashboard> GetDashboardAsync(
        int customerId, CancellationToken ct = default)
    {
        var customer = await _customerRepo.GetByIdAsync(customerId, ct)
            ?? throw new CustomerNotFoundException(customerId);

        var orders = await _orderRepo.GetByCustomerAsync(customerId.ToString(), ct);

        return new CustomerDashboard
        {
            CustomerId        = customer.Id,
            CustomerName      = customer.Name,
            TotalOrders       = orders.Count,
            TotalRevenue      = orders.Sum(o => o.TotalAmount),
            AverageOrderValue = orders.Count > 0 ? orders.Average(o => o.TotalAmount) : 0,
            RecentOrders      = orders
                .OrderByDescending(o => o.CreatedAt)
                .Take(10)
                .Select(o => new OrderSummary
                {
                    Status    = o.Status.ToString(),
                    CreatedAt = o.CreatedAt,
                    Total     = o.TotalAmount
                })
                .ToList()
        };
    }

    public async Task<Customer> CreateAsync(Customer customer, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customer.Name))
            throw new DomainException("VALIDATION", "Customer name is required.");

        if (string.IsNullOrWhiteSpace(customer.Email))
            throw new DomainException("VALIDATION", "Customer email is required.");

        var existing = await _customerRepo.GetByEmailAsync(customer.Email.ToLowerInvariant(), ct);
        if (existing != null)
            throw new DomainException("DUPLICATE_EMAIL", $"A customer with email '{customer.Email}' already exists.");

        customer.Email     = customer.Email.ToLowerInvariant().Trim();
        customer.CreatedAt = DateTime.UtcNow;
        customer.Status    = CustomerStatus.Active;
        customer.Tier      = CustomerTier.Standard;

        var created = await _customerRepo.CreateAsync(customer, ct);

        await _audit.LogAsync(AuditAction.Created, "Customer", created.Id.ToString(),
            userId: null, newValues: created, ct: ct);

        _logger.LogInformation("Customer {CustomerId} created: {Email}", created.Id, created.Email);

        return created;
    }

    public async Task<Customer> UpdateAsync(Customer customer, CancellationToken ct = default)
    {
        var existing = await _customerRepo.GetByIdAsync(customer.Id, ct)
            ?? throw new CustomerNotFoundException(customer.Id);

        var old = ShallowCopy(existing);

        existing.Name       = customer.Name;
        existing.Phone      = customer.Phone;
        existing.Company    = customer.Company;
        existing.VatNumber  = customer.VatNumber;
        existing.MarketingOptIn = customer.MarketingOptIn;
        existing.InternalNotes  = customer.InternalNotes;

        // billing address
        existing.BillingAddressLine1  = customer.BillingAddressLine1;
        existing.BillingAddressLine2  = customer.BillingAddressLine2;
        existing.BillingCity          = customer.BillingCity;
        existing.BillingState         = customer.BillingState;
        existing.BillingPostalCode    = customer.BillingPostalCode;
        existing.BillingCountry       = customer.BillingCountry;

        // shipping address
        existing.ShippingAddressLine1 = customer.ShippingAddressLine1;
        existing.ShippingAddressLine2 = customer.ShippingAddressLine2;
        existing.ShippingCity         = customer.ShippingCity;
        existing.ShippingState        = customer.ShippingState;
        existing.ShippingPostalCode   = customer.ShippingPostalCode;
        existing.ShippingCountry      = customer.ShippingCountry;

        existing.UpdatedAt = DateTime.UtcNow;

        var updated = await _customerRepo.UpdateAsync(existing, ct);

        await _audit.LogAsync(AuditAction.Updated, "Customer", updated.Id.ToString(),
            userId: null, oldValues: old, newValues: updated, ct: ct);

        return updated;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var customer = await _customerRepo.GetByIdAsync(id, ct)
            ?? throw new CustomerNotFoundException(id);

        // soft delete — set status to Closed, don't actually remove
        customer.Status    = CustomerStatus.Closed;
        customer.UpdatedAt = DateTime.UtcNow;

        await _customerRepo.UpdateAsync(customer, ct);

        await _audit.LogAsync(AuditAction.Deleted, "Customer", id.ToString(),
            userId: null, ct: ct);

        _logger.LogWarning("Customer {CustomerId} closed (soft delete)", id);
    }

    public async Task<bool> EvaluateTierAsync(int customerId, CancellationToken ct = default)
    {
        var customer = await _customerRepo.GetByIdAsync(customerId, ct)
            ?? throw new CustomerNotFoundException(customerId);

        var orders = await _orderRepo.GetByCustomerAsync(customerId.ToString(), ct);

        var grossLifetimeValue = orders
            .Where(o => o.Status == OrderStatus.Fulfilled)
            .Sum(o => o.TotalAmount);

        // NOVA-105: refunds must reduce effective lifetime spend. A customer who
        // spent $22k but was refunded $19k has $3k of effective spend (Silver),
        // not Platinum. Refunds are tracked against payments, not orders, so we
        // net them out here — otherwise a refunded customer keeps an inflated tier
        // and the tier-based discounts that come with it.
        var payments = await _paymentRepo.GetByCustomerAsync(customerId, ct);
        var totalRefunded = payments.Sum(p => p.RefundedAmount ?? 0m);

        var lifetimeValue = grossLifetimeValue - totalRefunded;

        var newTier = lifetimeValue switch
        {
            >= PlatinumThreshold => CustomerTier.Platinum,
            >= GoldThreshold     => CustomerTier.Gold,
            >= SilverThreshold   => CustomerTier.Silver,
            _                    => CustomerTier.Standard
        };

        if (newTier == customer.Tier) return false;

        var oldTier = customer.Tier;
        customer.Tier            = newTier;
        customer.TierUpgradedAt  = DateTime.UtcNow;
        customer.LifetimeValue   = (double)lifetimeValue;

        await _customerRepo.UpdateAsync(customer, ct);

        _logger.LogInformation(
            "Customer {CustomerId} tier changed: {Old} -> {New} (LTV: {Ltv:C})",
            customerId, oldTier, newTier, lifetimeValue);

        return true;
    }

    public async Task<IReadOnlyList<Customer>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Array.Empty<Customer>();

        return await _customerRepo.SearchAsync(query.Trim(), ct);
    }

    // quick shallow copy for audit diff — not deep, just primitives
    private static object ShallowCopy(Customer c) => new
    {
        c.Name, c.Email, c.Phone, c.Company, c.VatNumber,
        c.BillingAddressLine1, c.BillingCity, c.BillingCountry,
        c.MarketingOptIn, c.Status, c.Tier
    };
}
