#nullable enable

using BTCPayServer.Payments;
using BTCPayServer.Plugins.LnurlPayBackend.Models;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.LnurlPayBackend.Payments;

///<summary>
/// Renders the bolt11 invoice as a Lightning QR in checkout.
///</summary>

public class LnurlBackendCheckoutModelExtension : ICheckoutModelExtension {
    public PaymentMethodId PaymentMethodId { get; }
    public string Image => "imlegacy/bitcoin-lightning.svg";
    public string Badge => "⚡";

    public LnurlBackendCheckoutModelExtension()
    {
        PaymentMethodId = new("BTC-LNADDR");
    }

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        var details = context.Handler.ParsePaymentPromptDetails(context.Prompt.Details)
            as LnurlBackendPaymentPromptDetails;
        if (details is null) return;

        context.Model.Address = details.Bolt11;
        context.Model.InvoiceBitcoinUrl = "lightning:" + details.Bolt11;
        context.Model.InvoiceBitcoinUrlQR = "LIGHTNING:" + details.Bolt11;
        context.Model.CheckoutBodyComponentName = "LightningCheckoutBody";
    }
}
