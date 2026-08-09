using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.LnurlPayBackend.Data;
using BTCPayServer.Plugins.LnurlPayBackend.Lightning;
using BTCPayServer.Plugins.LnurlPayBackend.Payments;
using BTCPayServer.Lightning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.LnurlPayBackend.Tests;

public class InvoiceRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LnurlBackendInvoiceRepository _repo;

    public InvoiceRepositoryTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _repo = new LnurlBackendInvoiceRepository(new SqliteContextFactory(_connection));
        // Same DDL as LnurlBackendInvoicesMigration (the table is not part of the EF model)
        using var ctx = new SqliteContextFactory(_connection).CreateDbContext();
        ctx.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "lnurl_backend_invoices" (
                "PaymentHash" text PRIMARY KEY,
                "Bolt11" text NOT NULL,
                "VerifyUrl" text NOT NULL,
                "AmountMsat" bigint NOT NULL,
                "ExpiresAt" timestamptz NOT NULL
            );
            """);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Persist_ThenLoad_ReturnsInvoice()
    {
        await _repo.PersistAsync("hash1", TestData.ValidBolt11, TestData.VerifyUrl,
            5000, DateTimeOffset.UtcNow.AddMinutes(5));

        var pending = await _repo.LoadPendingAsync();
        var inv = Assert.Single(pending);
        Assert.Equal("hash1", inv.PaymentHash);
        Assert.Equal(TestData.ValidBolt11, inv.Bolt11);
        Assert.Equal(TestData.VerifyUrl, inv.VerifyUrl);
        Assert.Equal(5000, inv.AmountMsat);
    }

    [Fact]
    public async Task Persist_DuplicateHash_KeepsFirst()
    {
        await _repo.PersistAsync("hash1", "first-bolt11", TestData.VerifyUrl, 5000, DateTimeOffset.UtcNow.AddMinutes(5));
        await _repo.PersistAsync("hash1", "second-bolt11", "https://wallet.com/verify/other", 9999, DateTimeOffset.UtcNow.AddMinutes(5));

        // ON CONFLICT DO NOTHING: the original row must survive
        var inv = Assert.Single(await _repo.LoadPendingAsync());
        Assert.Equal("first-bolt11", inv.Bolt11);
    }

    [Fact]
    public async Task Load_Expired_IsExcludedAndDeleted()
    {
        await _repo.PersistAsync("expired1", "bolt11", TestData.VerifyUrl, 5000, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Empty(await _repo.LoadPendingAsync());

        // If the expired row was actually deleted (not just filtered), the same
        // primary key can be reused with a fresh expiry.
        await _repo.PersistAsync("expired1", "bolt11", TestData.VerifyUrl, 5000, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Single(await _repo.LoadPendingAsync());
    }

    [Fact]
    public async Task Remove_DeletesRow()
    {
        await _repo.PersistAsync("hash1", TestData.ValidBolt11, TestData.VerifyUrl, 5000, DateTimeOffset.UtcNow.AddMinutes(5));

        await _repo.RemoveAsync("hash1");

        Assert.Empty(await _repo.LoadPendingAsync());
    }

    [Fact]
    public async Task Client_CreateInvoice_Persists_AndRecoversAfterRestart()
    {
        var verifyCalled = false;
        var handler = new FakeHttpHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("/verify/"))
            {
                verifyCalled = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(TestData.VerifyUnsettledJson, Encoding.UTF8, "application/json") };
            }
            if (req.RequestUri.ToString().Contains("/callback"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(TestData.CallbackJson, Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(TestData.Lud06Json, Encoding.UTF8, "application/json") };
        });
        var lnurl = new LnurlClient(new HttpClient(handler));

        // "Before restart": invoice created and persisted
        var client1 = new LnurlBackendLightningClient(TestData.LightningAddress, Network.Main, lnurl, repository: _repo);
        var invoice = await client1.CreateInvoice(LightMoney.MilliSatoshis(5000), "memo",
            TimeSpan.FromMinutes(5), CancellationToken.None);

        // The static spec-vector invoice carries a 2017 timestamp, so its persisted
        // expiry is in the past and LoadPendingAsync would prune it. In production
        // the LNURL server returns fresh invoices; push the expiry forward to
        // simulate that (the persistence contract itself is covered above).
        using (var ctx = new SqliteContextFactory(_connection).CreateDbContext())
            ctx.Database.ExecuteSqlRaw(
                "UPDATE \"lnurl_backend_invoices\" SET \"ExpiresAt\" = {0}",
                DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Single(await _repo.LoadPendingAsync());

        // "After restart": a fresh client (empty in-memory cache) recovers the
        // invoice from the DB and polls the persisted verify URL.
        // Note: the paid path (settled + matching preimage) needs a real preimage
        // pair, which cannot be synthesized without an invoice generator — that
        // path is exercised on regtest instead.
        var client2 = new LnurlBackendLightningClient(TestData.LightningAddress, Network.Main, lnurl, repository: _repo);
        var recovered = await client2.GetInvoice(invoice.PaymentHash!, CancellationToken.None);

        Assert.True(verifyCalled);
        Assert.Equal(LightningInvoiceStatus.Unpaid, recovered.Status);
        // Not settled → row stays for continued polling
        Assert.Single(await _repo.LoadPendingAsync());
    }
}

internal class SqliteContextFactory : IDbContextFactory<ApplicationDbContext>
{
    private readonly SqliteConnection _connection;
    public SqliteContextFactory(SqliteConnection connection) => _connection = connection;

    public ApplicationDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
}
