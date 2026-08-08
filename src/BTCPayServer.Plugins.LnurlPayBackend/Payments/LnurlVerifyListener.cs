#nullable enable

using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Data;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.LnurlPayBackend.Models;
using BTCPayServer.Services.Invoices;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin;
using BTCPayServer.Client.Models;

namespace BTCPayServer.Plugins.LnurlPayBackend.Payments;

public class LnurlVerifyListener : IHostedService {
    private readonly PaymentMethodId _pmi = new("BTC-LNADDR");
    private readonly SemaphoreSlim _concurrency = new(50);
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly LnurlClient _lnurlClient;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentService _paymentService;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly EventAggregator _eventAggregator;
    private readonly BTCPayNetwork _network;
    private readonly ILogger<LnurlVerifyListener> _logger;

    public LnurlVerifyListener(
        LnurlClient lnurlClient,
        InvoiceRepository invoiceRepository,
        PaymentService paymentService,
        PaymentMethodHandlerDictionary handlers,
        EventAggregator eventAggregator,
        BTCPayNetworkProvider networkProvider,
        ILogger<LnurlVerifyListener> logger)
    {
        _lnurlClient = lnurlClient;
        _invoiceRepository = invoiceRepository;
        _paymentService = paymentService;
        _handlers = handlers;
        _eventAggregator = eventAggregator;
        _network = networkProvider.BTC;
        _logger = logger;
    }
    public Task StartAsync(CancellationToken ct) {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = PollLoop(_cts.Token);
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken ct)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.WaitAsync(ct);
            }
            catch (OperationCanceledException) { }
            _pollTask = null;
        }
    }
    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {

            InvoiceEntity[] invoices;
            try
            {
                invoices = await _invoiceRepository.GetMonitoredInvoices(_pmi, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // transient DB failure must not kill the listener
                _logger.LogError(ex, "Failed to fetch monitored invoices");
                invoices = [];
            }
            var handler = _handlers[_pmi];

            foreach (var invoice in invoices)
            {
                LnurlBackendPaymentPromptDetails? details = null;
                await _concurrency.WaitAsync(ct);
                try
                {
                    var prompt = invoice.GetPaymentPrompt(_pmi);
                    if (prompt?.Details is null) continue;

                    details = handler.ParsePaymentPromptDetails(prompt.Details)
                        as LnurlBackendPaymentPromptDetails;
                    if (details is null) continue;

                    //expiry check
                    if (DateTimeOffset.UtcNow > details.ExpiresAt)
                    {
                        await MarkExpired(invoice, prompt);
                        continue;
                    }

                    var result = await _lnurlClient.VerifyPayment(details.VerifyUrl, ct);
                    //check status
                    if (result.Status != "OK")
                    {
                        _logger.LogWarning("Verify returned status={Status} for {Hash}",
                                   result.Status, details.PaymentHash);
                        continue;
                    }
                    //check settled
                    if (!result.Settled)
                    {
                        continue;
                    }
                    //check preimage
                    if (!ValidatePreimage(result.Preimage, details.PaymentHashUint)){
                        LogSuspicious(details.PaymentHash);
                        continue;
                    }

                    await MakeSettled(invoice, prompt, details, result.Preimage, handler);

                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Verify poll failed for {PaymentHash}", details?.PaymentHash ?? "unknown");
                }
                finally
                {
                    _concurrency.Release();
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task MakeSettled
    (InvoiceEntity invoice,
        PaymentPrompt prompt,
        LnurlBackendPaymentPromptDetails details,
        string preimageHex,
        IPaymentMethodHandler handler)
    {
        //hex string -> uint 256
        var preimageUint = new uint256(Encoders.Hex.DecodeData(preimageHex));

        var paymentData = new PaymentData
        {
            Id = details.PaymentHash,
            Created = DateTimeOffset.UtcNow,
            Status = PaymentStatus.Settled,
            Currency = _network.CryptoCode,
            InvoiceDataId = invoice.Id,
            Amount = prompt.Calculate().TotalDue,
        }.Set(invoice, handler, new LightningLikePaymentData
        {
            PaymentHash = details.PaymentHashUint,
            Preimage = preimageUint
        });
        var payment = await _paymentService.AddPayment(paymentData, [details.Bolt11]);

        if (payment is null) return;

        //saving preimage into prompt details
        var promptDetails = (LnurlBackendPaymentPromptDetails)
        handler.ParsePaymentPromptDetails(prompt.Details);

        if(promptDetails.Preimage is null){
            promptDetails.Preimage = preimageHex;
            await _invoiceRepository.UpdatePaymentDetails(invoice.Id, handler, promptDetails);
        }

        _eventAggregator.Publish(
            new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment)
            { Payment = payment });
    }

    private async Task MarkExpired(InvoiceEntity invoice, PaymentPrompt prompt) {
        prompt.Inactive = true;
        await _invoiceRepository.UpdatePrompt(invoice.Id, prompt);
    }

    public static bool ValidatePreimage(string preimageHex, uint256 expectedHash){
        byte[] preimageBytes = Encoders.Hex.DecodeData(preimageHex);
        byte[] hashBytes = NBitcoin.Crypto.Hashes.SHA256(preimageBytes);
        // Compare as big-endian bytes (matching LightningListener pattern)
        return hashBytes.AsSpan().SequenceEqual(expectedHash.ToBytes(false));
    }
    private void LogSuspicious(string paymentHash)
    {
        _logger.LogError(
            "Preimage does not match payment_hash. Possible spoofed verify response. PaymentHash={Hash}",
            paymentHash);
    }
}
