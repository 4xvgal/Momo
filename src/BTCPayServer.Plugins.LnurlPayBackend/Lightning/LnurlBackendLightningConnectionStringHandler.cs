#nullable enable
using System;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.LnurlPayBackend.Data;
using BTCPayServer.Plugins.LnurlPayBackend.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.LnurlPayBackend.Lightning;

public class LnurlBackendLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly LnurlBackendInvoiceRepository? _repository;
    private readonly bool _allowHttp;
    private readonly ILogger? _logger;

    public LnurlBackendLightningConnectionStringHandler(
        LnurlBackendInvoiceRepository? repository = null,
        ILogger<LnurlBackendLightningConnectionStringHandler>? logger = null,
        IConfiguration? configuration = null)
    {
        _repository = repository;
        _logger = logger;
        // Dev-only switch, same key as the DI-registered LnurlClient
        _allowHttp = configuration?.GetValue<bool>("LnurlBackendAllowHttp") ?? false;
    }

    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        var kv = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
        if (type != "lnurl-backend")
        {
            error = null;
            return null;
        }

        if (!kv.TryGetValue("address", out var address) || string.IsNullOrWhiteSpace(address))
        {
            error = "Lightning Address is required (address=you@wallet.com)";
            return null;
        }

        error = null;
        return new LnurlBackendLightningClient(address, network, logger: _logger,
            repository: _repository, allowHttp: _allowHttp);
    }
}

internal class LnurlBackendLightningClient : ILightningClient, IExtendedLightningClient
{
    private readonly string _address;
    private readonly Network _network;
    private readonly LnurlClient _lnurlClient;
    private readonly ILogger? _logger;
    private readonly LnurlBackendInvoiceRepository? _repository;
    private readonly ConcurrentDictionary<string, (string VerifyUrl, string Bolt11, long AmountMsat)> _verifyUrls = new();
    private Task? _cacheLoadTask;

    public LnurlBackendLightningClient(string address, Network network, LnurlClient? lnurlClient = null, ILogger? logger = null, LnurlBackendInvoiceRepository? repository = null, bool allowHttp = false)
    {
        _address = address;
        _network = network;
        // ponytail: no DI here, so replicate Plugin.cs SSRF guard (redirects off);
        // allowHttp is the dev-only switch (same config key as the DI client)
        _lnurlClient = lnurlClient ?? new LnurlClient(
            new HttpClient(LnurlHttpHandlerFactory.Create(allowLoopback: allowHttp)), allowHttp);

        _logger = logger;
        _repository = repository;
    }

    public override string ToString() => $"type=lnurl-backend;address={_address}";

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken c = default)
    {
        var msat = amount.MilliSatoshi;
        var payParams = await _lnurlClient.FetchLud06Params(_address, c);
        var invoice = await _lnurlClient.FetchInvoice(payParams.Callback, msat, c);

        var bolt11 = BOLT11PaymentRequest.Parse(invoice.Pr, _network);
        var paymentHash = bolt11.PaymentHash?.ToString() ?? string.Empty;

        if (string.IsNullOrEmpty(invoice.Verify))
            throw new NotSupportedException(
                "This Lightning Address provider does not support LUD-21 verify. " +
                "Payment status cannot be detected. Use a provider that supports LUD-21.");

        // Store verify URL for later GetInvoice polling (memory + DB if available)
        _verifyUrls[paymentHash] = (invoice.Verify, invoice.Pr, msat);
        if (_repository is not null)
        {
            try
            {
                await _repository.PersistAsync(paymentHash, invoice.Pr, invoice.Verify,
                    msat, bolt11.ExpiryDate, c);
            }
            catch (Exception ex)
            {
                // invoice creation must not fail because persistence failed;
                // polling still works for this session via the in-memory dict
                _logger?.LogError(ex, "Failed to persist invoice {Hash}", paymentHash);
            }
        }

        return new LightningInvoice
        {
            Id = paymentHash,
            PaymentHash = paymentHash,
            BOLT11 = invoice.Pr,
            Amount = amount,
            AmountReceived = LightMoney.Zero,
            Status = LightningInvoiceStatus.Unpaid,
            ExpiresAt = bolt11.ExpiryDate,
        };
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams p, CancellationToken c = default)
        => await CreateInvoice(p.Amount, p.Description, p.Expiry, c);

    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken c = default)
    {
        await EnsureCacheLoadedAsync(c);
        if (!_verifyUrls.TryGetValue(invoiceId, out var entry))
            return new LightningInvoice { Id = invoiceId, Status = LightningInvoiceStatus.Unpaid };

        return await GetInvoiceCore(invoiceId, entry.VerifyUrl, c);
    }

    private async Task<LightningInvoice> GetInvoiceCore(string invoiceId, string verifyUrl, CancellationToken c)
    {
        var result = await _lnurlClient.VerifyPayment(verifyUrl, c);

        if (result.Settled)
        {
            if (!uint256.TryParse(invoiceId, out var paymentHash) ||
                string.IsNullOrEmpty(result.Preimage) ||
                !LnurlVerifyListener.ValidatePreimage(result.Preimage, paymentHash))
            {
                _logger?.LogError("GetInvoiceCore got invalid preimage for invoice {InvoiceId}", invoiceId);
                return new LightningInvoice { Id = invoiceId, Status = LightningInvoiceStatus.Unpaid };
            }
            await TryRemoveInvoiceAsync(invoiceId);
        }

        return new LightningInvoice
        {
            Id = invoiceId,
            Status = result.Settled ? LightningInvoiceStatus.Paid : LightningInvoiceStatus.Unpaid,
            Amount = result.Settled ? LightMoney.Zero : null,
            AmountReceived = null,
            PaidAt = result.Settled ? DateTimeOffset.UtcNow : null,
            Preimage = result.Preimage,
        };
    }

    public Task<LightningInvoice> GetInvoice(uint256 id, CancellationToken c = default)
        => GetInvoice(id.ToString(), c);

    // -- IExtendedLightningClient (save-time validation hook) --

    public string? DisplayName => "LNURL-backend";
    public Uri? ServerUri => null;

    public async Task<ValidationResult?> Validate()
    {
        try
        {
            var payParams = await _lnurlClient.FetchLud06Params(_address);
            var invoice = await _lnurlClient.FetchInvoice(payParams.Callback, payParams.MinSendable);
            if (string.IsNullOrEmpty(invoice.Verify))
                return new ValidationResult(
                    "This Lightning Address provider does not support LUD-21 verify. " +
                    "Payment status cannot be detected. Use a provider that supports LUD-21.");
            return ValidationResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LNURL backend validation failed for {Address}", _address);
            return new ValidationResult("Could not connect to Lightning Address provider. See server logs for details.");
        }
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken c = default)
    {
        var payParams = await _lnurlClient.FetchLud06Params(_address, c);
        var invoice = await _lnurlClient.FetchInvoice(payParams.Callback, payParams.MinSendable, c);

        if (string.IsNullOrEmpty(invoice.Verify))
            throw new NotSupportedException(
                "This Lightning Address provider does not support LUD-21 verify. " +
                "Payment status cannot be detected. Use a provider that supports LUD-21.");

        // ponytail: no real node info, just signal that LUD-21 probe passed
        return new LightningNodeInformation();
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken c = default)
    {
        var listener = new PollingListener(_verifyUrls, _lnurlClient, _logger, _repository);
        listener.Start(c);
        return Task.FromResult<ILightningInvoiceListener>(listener);
    }

    /// <summary>
    /// Loads pending invoices from the DB into the in-memory cache once.
    /// A failed load resets the flag so the next call retries.
    /// </summary>
    private async Task EnsureCacheLoadedAsync(CancellationToken c)
    {
        if (_repository is null || _cacheLoadTask is not null) return;
        _cacheLoadTask = LoadPendingInvoicesAsync(c);
        try { await _cacheLoadTask; }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load pending invoices from DB");
            _cacheLoadTask = null;
        }
    }

    private async Task LoadPendingInvoicesAsync(CancellationToken c)
    {
        var pending = await _repository!.LoadPendingAsync(c);
        foreach (var inv in pending)
            _verifyUrls[inv.PaymentHash] = (inv.VerifyUrl, inv.Bolt11, inv.AmountMsat);
    }

    private async Task TryRemoveInvoiceAsync(string invoiceId)
    {
        if (_repository is null) return;
        try { await _repository.RemoveAsync(invoiceId); }
        catch (Exception ex) { _logger?.LogError(ex, "Failed to remove invoice {Hash}", invoiceId); }
    }

    private class PollingListener : ILightningInvoiceListener
    {
        private readonly ConcurrentDictionary<string, (string VerifyUrl, string Bolt11, long AmountMsat)> _urls;
        private readonly LnurlClient _client;
        private readonly ILogger? _logger;
        private readonly LnurlBackendInvoiceRepository? _repository;
        private readonly ConcurrentQueue<LightningInvoice> _paid = new();
        private readonly ConcurrentQueue<TaskCompletionSource<LightningInvoice>> _waiters = new();

        public PollingListener(ConcurrentDictionary<string, (string VerifyUrl, string Bolt11, long AmountMsat)> urls, LnurlClient client, ILogger? logger, LnurlBackendInvoiceRepository? repository)
        { _urls = urls; _client = client; _logger = logger; _repository = repository; }

        public void Start(CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    // The connection-string handler creates a fresh client instance per
                    // call, so this listener's in-memory dict may be empty even though
                    // invoices were persisted by another instance. Load pending invoices
                    // from the DB every cycle to cover that gap.
                    if (_repository is not null)
                    {
                        try
                        {
                            foreach (var inv in await _repository.LoadPendingAsync(ct))
                                _urls[inv.PaymentHash] = (inv.VerifyUrl, inv.Bolt11, inv.AmountMsat);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "PollingListener failed to load pending invoices");
                        }
                    }
                    foreach (var (hash, (url, bolt11, amountMsat)) in _urls.ToArray())
                    {
                        try
                        {
                            var r = await _client.VerifyPayment(url, ct);
                            if (r.Settled && !string.IsNullOrEmpty(r.Preimage))
                            {
                                if (!uint256.TryParse(hash, out var paymentHash) ||
                                    !LnurlVerifyListener.ValidatePreimage(r.Preimage, paymentHash))
                                {
                                    _logger?.LogError("PollingListener got invalid preimage for {Hash}", hash);
                                    continue;
                                }

                                if (!_urls.TryRemove(hash, out var entry))
                                    continue;

                                if (_repository is not null)
                                {
                                    try { await _repository.RemoveAsync(hash, ct); }
                                    catch (Exception ex) { _logger?.LogError(ex, "Failed to remove invoice {Hash}", hash); }
                                }

                                var money = new LightMoney(entry.AmountMsat, LightMoneyUnit.MilliSatoshi);
                                var inv = new LightningInvoice
                                {
                                    Id = hash,
                                    BOLT11 = entry.Bolt11,
                                    PaymentHash = hash,
                                    Status = LightningInvoiceStatus.Paid,
                                    Preimage = r.Preimage,
                                    Amount = money,
                                    AmountReceived = money,
                                    PaidAt = DateTimeOffset.UtcNow
                                };
                                _paid.Enqueue(inv);
                                if (_waiters.TryDequeue(out var tcs))
                                    tcs.TrySetResult(inv);
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "PollingListener failed for {Hash}", hash);
                        }
                    }
                    await Task.Delay(5000, ct);
                }
            }, ct);
        }

        public Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
        {
            if (_paid.TryDequeue(out var alreadyPaid))
                return Task.FromResult(alreadyPaid);

            var tcs = new TaskCompletionSource<LightningInvoice>();
            cancellation.Register(() => tcs.TrySetCanceled());
            _waiters.Enqueue(tcs);

            // Avoid a race where an invoice was paid between the first TryDequeue and Enqueue.
            if (_paid.TryDequeue(out var racedPaid) && _waiters.TryDequeue(out var waiter))
                waiter.TrySetResult(racedPaid);

            return tcs.Task;
        }

        public void Dispose() { }
    }

    // -- unused stubs --
    public Task<LightningInvoice[]> ListInvoices(CancellationToken c = default)
        => Task.FromResult(Array.Empty<LightningInvoice>());
    public Task<LightningInvoice[]> ListInvoices(ListInvoicesParams p, CancellationToken c = default)
        => Task.FromResult(Array.Empty<LightningInvoice>());
    public Task<LightningInvoice[]> ListPendingInvoices(CancellationToken c = default)
        => Task.FromResult(Array.Empty<LightningInvoice>());
    public Task<LightningNodeBalance> GetBalance(CancellationToken c = default)
        => Task.FromResult(new LightningNodeBalance());
    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams p, CancellationToken c = default)
        => throw new NotSupportedException();
    public Task<PayResponse> Pay(string bolt11, CancellationToken c = default)
        => throw new NotSupportedException();
    public Task<PayResponse> Pay(PayInvoiceParams p, CancellationToken c = default)
        => throw new NotSupportedException();
    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest req, CancellationToken c = default)
        => throw new NotSupportedException();
    public Task<ConnectionResult> ConnectTo(NodeInfo ni, CancellationToken c = default)
        => throw new NotSupportedException();
    public Task CancelInvoice(string invoiceId, CancellationToken c = default) => Task.CompletedTask;
    public Task<LightningChannel[]> ListChannels(CancellationToken c = default)
        => Task.FromResult(Array.Empty<LightningChannel>());
    public Task<LightningPayment> GetPayment(string hash, CancellationToken c = default)
        => throw new NotSupportedException();
    public Task<LightningPayment[]> ListPayments(CancellationToken c = default)
        => Task.FromResult(Array.Empty<LightningPayment>());
    public Task<LightningPayment[]> ListPayments(ListPaymentsParams p, CancellationToken c = default)
        => Task.FromResult(Array.Empty<LightningPayment>());
    public Task<BitcoinAddress> GetDepositAddress(CancellationToken c = default)
        => throw new NotSupportedException();

    private class NoopListener : ILightningInvoiceListener
    {
        public Task<LightningInvoice> WaitInvoice(CancellationToken cancellation)
            => new TaskCompletionSource<LightningInvoice>().Task;
        public void Dispose() { }
    }
}
