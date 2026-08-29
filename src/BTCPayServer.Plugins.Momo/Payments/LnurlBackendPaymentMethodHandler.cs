#nullable enable

using System;
using System.Text;
using System.Threading.Tasks;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Momo.Models;

namespace BTCPayServer.Plugins.Momo.Payments;

public class LnurlBackendPaymentMethodHandler : IPaymentMethodHandler
{
    private readonly LnurlClient _lnurlClient;
    private readonly BTCPayNetwork _network;

    public PaymentMethodId PaymentMethodId { get; }
    public JsonSerializer Serializer { get; }

    ///<summary>
    /// BTC-MOMO-LNADDR — namespaced so it can never collide with core
    /// (BTC-LNURL/BTC-LN) or other plugins. Single source: Plugin.Pmi.
    ///</summary>
    public static readonly PaymentMethodId Pmi = Plugin.Pmi;

    public LnurlBackendPaymentMethodHandler(LnurlClient lnurlClient, BTCPayNetworkProvider networkProvider)
    {
        _lnurlClient = lnurlClient;
        _network = networkProvider.BTC;
        PaymentMethodId = Pmi;
        Serializer = BlobSerializer.CreateSerializer(_network.NBitcoinNetwork).Serializer;
    }

    // -- IPaymentMethodHandler required members --
    public object ParsePaymentMethodConfig(JToken config)
        => config.ToObject<LnurlBackendPaymentMethodConfig>(Serializer)
            ?? throw new FormatException("Invalid LnurlPaymentMethodConfig");

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    => details.ToObject<LnurlBackendPaymentPromptDetails>(Serializer)
        ?? throw new FormatException("Invalid LnurlBackendPaymentPromptDeails");

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    => details.ToObject<LightningLikePaymentData>(Serializer)
        ?? throw new FormatException("Invalid LightningLikePaymentData");

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Inactive = false;
        context.Prompt.Currency = _network.CryptoCode;
        context.Prompt.Divisibility = 11;
        context.Prompt.RateDivisibility = 8;
        context.Prompt.PaymentMethodFee = 0.0m;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        var store = context.Store;
        var config = (LnurlBackendPaymentMethodConfig)ParsePaymentMethodConfig(
            store.GetPaymentMethodConfigs()[PaymentMethodId]);
        if (config?.LightningAddress is null)
            throw new PaymentMethodUnavailableException(
                "LNURL-pay backend is not configured. Set a Lightning Address in store settings.");

        var amountMsat = ConvertToMsat(context.InvoiceEntity.Price, context.InvoiceEntity.Currency);

        // 1) LUD-06: GET /.well-known/lnurlp/{user}
        var payParams = await _lnurlClient.FetchLud06Params(config.LightningAddress);
        if (payParams.Tag != "payRequest")
            throw new PaymentMethodUnavailableException("LNURL endpoint does not support payRequest");

        ValidateAmountRange(amountMsat, payParams.MinSendable, payParams.MaxSendable);

        // 2) LUD-06 callback: GET {callback}?amount={msat}
        var invoiceResp = await _lnurlClient.FetchInvoice(payParams.Callback, amountMsat,
            LnurlClient.BuildComment(context.InvoiceEntity.Metadata?.ItemDesc, payParams.CommentAllowed));
        if (invoiceResp.Status == "ERROR")
            throw new PaymentMethodUnavailableException("Lightning Address provider returned an error for the payment request.");
        if (string.IsNullOrEmpty(invoiceResp.Verify))
            throw new PaymentMethodUnavailableException(
                "This Lightning Address provider does not support LUD-21 verify.");

        // 3) Parse BOLT11 (via LnurlClient to isolate Nbitcoin BOLT11 types)
        var bolt11 = LnurlClient.ParseBolt11(invoiceResp.Pr, _network.NBitcoinNetwork);

        // 4) validate amount matches what we requested
        ValidateAmountExact(amountMsat, bolt11.AmountMsat);

        // 5) validate description_hash matches LUD-06 metadata hash
        ValidateDescriptionHash(bolt11.DescriptionHash, payParams.Metadata);

        // 6) Populate PaymentPrompt
        if (bolt11.PaymentHash is null)
            throw new PaymentMethodUnavailableException("BOLT11 invoice has no payment hash.");
        var paymentHash = bolt11.PaymentHash;
        context.Prompt.Destination = invoiceResp.Pr;

        var details = new LnurlBackendPaymentPromptDetails
        {
            Bolt11 = invoiceResp.Pr,
            PaymentHash = paymentHash.ToString(),
            PaymentHashUint = paymentHash,
            VerifyUrl = invoiceResp.Verify,
            ExpiresAt = bolt11.ExpiryDate
        };
        context.Prompt.Details = JObject.FromObject(details, Serializer);

        // 7) TrackedDestinations: enables GetMonitoredInvoices lookup
        context.TrackedDestinations.Add(paymentHash.ToString());
    }

    //helpers
    internal static long ConvertToMsat(decimal invoicePrice, string currency) {
        if (!string.Equals(currency, "BTC", StringComparison.OrdinalIgnoreCase))
            throw new PaymentMethodUnavailableException($"LNURL-pay backend only supports BTC. Currency {currency} is not supported");
        return (long)(invoicePrice * Money.COIN * 1000m);
    }

    internal static void ValidateAmountRange(long amountMsat, long minSendable, long maxSendable) {
        if (amountMsat < minSendable || amountMsat > maxSendable)
            throw new PaymentMethodUnavailableException($"Amount {amountMsat} msat is outside the LNURL range [{minSendable}, {maxSendable}].");
    }

    internal static void ValidateAmountExact(long requestedMsat, long actualMsat)
    {
        if (actualMsat != requestedMsat)
            throw new PaymentMethodUnavailableException(
                $"BOLT11 amount mismatch: requested {requestedMsat} msat, got {actualMsat} msat.");
    }

    internal static void ValidateDescriptionHash(uint256? bolt11Hash, string metadataJson){
        if (bolt11Hash is null) return;
        var metadataHash = new uint256(NBitcoin.Crypto.Hashes.SHA256(Encoding.UTF8.GetBytes(metadataJson)));
        if (!bolt11Hash.Equals(metadataHash))
            throw new PaymentMethodUnavailableException(
                "BOLT11 description_hash does not match LUD-06 metadata hash.");
    }
}
