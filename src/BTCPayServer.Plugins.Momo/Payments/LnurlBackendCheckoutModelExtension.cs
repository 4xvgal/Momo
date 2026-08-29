#nullable enable

using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Momo.Models;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Momo.Payments;

///<summary>
/// Renders the bolt11 invoice as a Lightning QR in checkout.
///</summary>

public class LnurlBackendCheckoutModelExtension(DisplayFormatter displayFormatter) : ICheckoutModelExtension {
    public PaymentMethodId PaymentMethodId { get; } = Plugin.Pmi;
    public string Image => "imlegacy/bitcoin-lightning.svg";
    public string Badge => "⚡";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler.ParsePaymentPromptDetails(context.Prompt.Details)
            is not LnurlBackendPaymentPromptDetails details) return;

        context.Model.Address = details.Bolt11;
        context.Model.InvoiceBitcoinUrl = "lightning:" + details.Bolt11;
        context.Model.InvoiceBitcoinUrlQR = "LIGHTNING:" + details.Bolt11;
        context.Model.CheckoutBodyComponentName = "LightningCheckoutBody";

        // Match core Lightning/LNURL behavior: show sats when the store's
        // "Display Lightning amounts in sats" setting is enabled.
        if (context.StoreBlob.LightningAmountInSatoshi && context.Model.PaymentMethodCurrency == "BTC")
        {
            BitcoinCheckoutModelExtension.PreparePaymentModelForAmountInSats(context.Model, context.Prompt.Rate, displayFormatter);
        }
    }
}
