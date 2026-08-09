// Data/LnurlBackendInvoiceRepository.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.LnurlPayBackend.Data;

/// <summary>
/// A pending path-B invoice as stored in lnurl_backend_invoices.
/// Column names match the record constructor parameters (SqlQueryRaw binding).
/// </summary>
public record PendingLnurlInvoice(string PaymentHash, string Bolt11, string VerifyUrl, long AmountMsat);

/// <summary>
/// Persists path-B (connection string) invoices so the verify polling
/// survives BTCPayServer restarts. Raw SQL — the table is not part of
/// the EF model (see LnurlBackendInvoicesMigration).
/// </summary>
public class LnurlBackendInvoiceRepository
{
    private readonly IDbContextFactory<ApplicationDbContext> _db;

    public LnurlBackendInvoiceRepository(IDbContextFactory<ApplicationDbContext> db) => _db = db;

    public async Task PersistAsync(string paymentHash, string bolt11, string verifyUrl,
        long amountMsat, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var ctx = await _db.CreateDbContextAsync(ct);
        // explicit object[] so ct binds to the CancellationToken overload, not a SQL param
        await ctx.Database.ExecuteSqlRawAsync("""
            INSERT INTO "lnurl_backend_invoices"
                ("PaymentHash", "Bolt11", "VerifyUrl", "AmountMsat", "ExpiresAt")
            VALUES ({0}, {1}, {2}, {3}, {4})
            ON CONFLICT DO NOTHING
            """, new object[] { paymentHash, bolt11, verifyUrl, amountMsat, expiresAt }, ct);
    }

    /// <summary>
    /// Returns invoices that have not expired yet, and deletes expired rows.
    /// </summary>
    public async Task<List<PendingLnurlInvoice>> LoadPendingAsync(CancellationToken ct = default)
    {
        await using var ctx = await _db.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var pending = await ctx.Database.SqlQueryRaw<PendingLnurlInvoice>("""
            SELECT "PaymentHash", "Bolt11", "VerifyUrl", "AmountMsat"
            FROM "lnurl_backend_invoices"
            WHERE "ExpiresAt" > {0}
            """, new object[] { now }).ToListAsync(ct);
        await ctx.Database.ExecuteSqlRawAsync("""
            DELETE FROM "lnurl_backend_invoices" WHERE "ExpiresAt" <= {0}
            """, new object[] { now }, ct);
        return pending;
    }

    public async Task RemoveAsync(string paymentHash, CancellationToken ct = default)
    {
        await using var ctx = await _db.CreateDbContextAsync(ct);
        await ctx.Database.ExecuteSqlRawAsync(
            """DELETE FROM "lnurl_backend_invoices" WHERE "PaymentHash" = {0}""",
            new object[] { paymentHash }, ct);
    }
}
