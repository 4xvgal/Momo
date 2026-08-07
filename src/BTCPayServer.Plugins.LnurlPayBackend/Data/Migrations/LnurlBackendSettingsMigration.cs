// Data/Migrations/LnurlBackendSettingsMigration.cs
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.LnurlPayBackend.Data.Migrations;

/// <summary>
/// Creates the lnurl_backend_settings table if it doesn't exist.
/// Raw SQL for simplicity — no EF model snapshot needed.
/// </summary>
public class LnurlBackendSettingsMigration : MigrationBase<ApplicationDbContext>
{
    public LnurlBackendSettingsMigration() : base("20260101_lnurlbackend_settings") { }

    public override Task MigrateAsync(ApplicationDbContext dbContext, CancellationToken cancellationToken)
        => dbContext.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "lnurl_backend_settings" (
                "StoreId" text PRIMARY KEY,
                "LightningAddress" text,
                "Enabled" boolean NOT NULL DEFAULT false,
                "LastValidatedAt" timestamptz,
                "VerifySupportConfirmed" boolean NOT NULL DEFAULT false
            );
            """, cancellationToken);
}
