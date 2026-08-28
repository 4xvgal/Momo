// Data/Migrations/LnurlBackendInvoicesMigration.cs
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Momo.Data.Migrations;

/// <summary>
/// Creates the lnurl_backend_invoices table if it doesn't exist.
/// Persists path-B (connection string) invoices so verify polling
/// survives restarts. Raw SQL — same pattern as the settings migration.
/// </summary>
public class LnurlBackendInvoicesMigration : MigrationBase<ApplicationDbContext>
{
    public LnurlBackendInvoicesMigration() : base("20260102_lnurlbackend_invoices") { }

    public override Task MigrateAsync(ApplicationDbContext dbContext, CancellationToken cancellationToken)
        => dbContext.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "lnurl_backend_invoices" (
                "PaymentHash" text PRIMARY KEY,
                "Bolt11" text NOT NULL,
                "VerifyUrl" text NOT NULL,
                "AmountMsat" bigint NOT NULL,
                "ExpiresAt" timestamptz NOT NULL
            );
            """, cancellationToken);
}
