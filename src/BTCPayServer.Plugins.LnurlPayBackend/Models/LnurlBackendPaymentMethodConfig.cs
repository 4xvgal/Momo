using Newtonsoft.Json;

namespace BTCPayServer.Plugins.LnurlPayBackend.Models;

public class LnurlPayBackendMethodConfig{
    [JsonProperty("lightningAddress")]
    public string LightningAddress {get; set;}
}
