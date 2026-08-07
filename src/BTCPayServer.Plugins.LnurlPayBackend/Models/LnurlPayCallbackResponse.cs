// Models/LnurlPayCallbackResponse.cs
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.LnurlPayBackend.Models;

/// <summary>
/// Response from LUD-06 payRequest callback: GET {callback}?amount={msat}.
/// Returns either {pr, verify, routes} on success or {status:"ERROR", reason:"..."}.
/// </summary>
public class LnurlPayCallbackResponse
{
    /// <summary>"OK" or "ERROR". Must check before reading pr/verify.</summary>
    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("pr")]
    public string Pr { get; set; }

    [JsonProperty("verify")]
    public string Verify { get; set; }
}
