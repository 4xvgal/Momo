#nullable enable
using System;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.LnurlPayBackend.Payments;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.LnurlPayBackend.Lightning;

public class LnurlBackendLightningConnectionStringHandler : ILightningConnectionStringHandler
{
    private readonly ILogger? _logger;

    public LnurlBackendLightningConnectionStringHandler(ILogger<LnurlBackendLightningConnectionStringHandler>? logger = null)
    {
        _logger = logger;
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
        return new LnurlBackendLightningClient(address, network, logger: _logger);
    }
}

internal class LnurlBackendLightningClient : ILightningClient, IExtendedLightningClient
{
    private readonly string _address;
    private readonly Network _network;
    private readonly LnurlClient _lnurlClient;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, (string VerifyUrl, string Bolt11, long AmountMsat)> _verifyUrls = new();

    public LnurlBackendLightningClient(string address, Network network, LnurlClient? lnurlClient = null, ILogger? logger = null)
    {
        _address = address;
        _network = network;
        _lnurlClient = lnurlClient ?? new LnurlClient(
            new HttpClient(LnurlHttpHandlerFactory.Create(allowLoopback: false)));

        _logger = logger;
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

        // Store verify URL for later GetInvoice polling
        _verifyUrls[paymentHash] = (invoice.Verify, invoice.Pr, msat);

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

    public Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken c = default)
    {
        if (!_verifyUrls.TryGetValue(invoiceId, out var entry))
            return Task.FromResult(new LightningInvoice { Id = invoiceId, Status = LightningInvoiceStatus.Unpaid });

        return GetInvoiceCore(invoiceId, entry.VerifyUrl, c);
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
        var listener = new PollingListener(_verifyUrls, _lnurlClient, _logger);
        listener.Start(c);
        return Task.FromResult<ILightningInvoiceListener>(listener);
    }

    private class PollingListener : ILightningInvoiceListener
    {
        private readonly ConcurrentDictionary<string, (string VerifyUrl, string Bolt11, long AmountMsat)> _urls;
        private readonly LnurlClient _client;
        private readonly ILogger? _logger;
        private readonly ConcurrentQueue<LightningInvoice> _paid = new();
        private readonly ConcurrentQueue<TaskCompletionSource<LightningInvoice>> _waiters = new();

        public PollingListener(ConcurrentDictionary<string, (string VerifyUrl, string Bolt11, long AmountMsat)> urls, LnurlClient client, ILogger? logger)
        { _urls = urls; _client = client; _logger = logger; }

        public void Start(CancellationToken ct)
        {
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
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
