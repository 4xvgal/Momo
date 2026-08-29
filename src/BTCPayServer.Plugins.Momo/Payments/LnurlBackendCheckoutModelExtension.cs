#nullable enable

using BTCPayServer.Payments;
using BTCPayServer.Plugins.Momo.Models;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Momo.Payments;

///<summary>
/// Renders the bolt11 invoice as a Lightning QR in checkout.
///</summary>

public class LnurlBackendCheckoutModelExtension : ICheckoutModelExtension {
    public PaymentMethodId PaymentMethodId { get; }
    public string Image => "imlegacy/bitcoin-lightning.svg";
    public string Badge => "⚡";

    public LnurlBackendCheckoutModelExtension()
    {
        PaymentMethodId = Plugin.Pmi;
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
