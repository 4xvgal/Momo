using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Momo.Models;

/// <summary>
/// Stored in StoreData.PaymentMethodConfigs[PMI].
/// Parsed by IPaymentMethodHandler.ParsePaymentMethodConfig.
/// </summary>
public class LnurlBackendPaymentMethodConfig
{
    [JsonProperty("lightningAddress")]
    public string LightningAddress { get; set; }
}
