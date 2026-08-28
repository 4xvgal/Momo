// Models/LnurlVerifyResponse.cs
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Momo.Models;

/// <summary>
/// Response from LUD-21 verify endpoint: GET {verifyUrl}.
/// </summary>
public class LnurlVerifyResponse
{
    /// <summary>"OK" or "ERROR". Must check before reading settled.</summary>
    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("settled")]
    public bool Settled { get; set; }

    /// <summary>64-char hex preimage. Validated against payment_hash via SHA256.</summary>
    [JsonProperty("preimage")]
    public string Preimage { get; set; }

    /// <summary>Original bolt11, optional. Not used for verification.</summary>
    [JsonProperty("pr")]
    public string Pr { get; set; }
}
