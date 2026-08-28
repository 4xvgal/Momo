#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LnurlPayBackend.Models;
using NBitcoin;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.LnurlPayBackend.Payments;

///<summary>
/// HTTP client for LUD-06 /.well-known/lnurlp lookup, payRequest callback,
/// and LUD-21 verify polling. Enforces HTTPS, blocks SSRF, limits response size.
/// </summary>

public class LnurlClient
{
    private readonly HttpClient _httpClient;
    private readonly bool _allowHttp;
    private const int MaxResponseBytes = 8192;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <param name="allowHttp">Dev-only: allow plain HTTP (e.g. a regtest instance on
    /// localhost). Never enable in production — SSRF guard and HTTPS enforcement
    /// are what keep the plugin safe.</param>
    public LnurlClient(HttpClient httpClient, bool allowHttp = false)
    {
        _httpClient = httpClient;
        _allowHttp = allowHttp;
        _httpClient.Timeout = DefaultTimeout;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BTCPayServer-LnurlPayBackend/0.1");
    }

    ///<summary>
    ///GET /.well-known/lnurlp/{user} -> {callback, minSendable, maxSendable, metadata, tag: "payRequest"}
    ///</summary>
    public async Task<Lud06Params> FetchLud06Params(string lightningAddress, CancellationToken ct = default)
    {
        var (user, domain) = ParseLightningAddress(lightningAddress);
        var scheme = _allowHttp ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
        var url = $"{scheme}://{domain}/.well-known/lnurlp/{Uri.EscapeDataString(user)}";
        var json = await GetStringAsync(url, ct);
        return JsonConvert.DeserializeObject<Lud06Params>(json)
            ?? throw new InvalidOperationException("Empty LUD-06 response");
    }

    /// <summary>
    /// GET {callback}?amount={msat} → {status, pr, verify}
    /// </summary>
    public async Task<LnurlPayCallbackResponse> FetchInvoice(string callbackUrl, long amountMsat, string? comment = null, CancellationToken ct = default)
    {
        var url = $"{callbackUrl}{(callbackUrl.Contains('?') ? '&' : '?')}amount={amountMsat}";
        if (!string.IsNullOrEmpty(comment))
            url += $"&comment={Uri.EscapeDataString(comment)}"; //lud-12 comment(description)
        var json = await GetStringAsync(url, ct);
        return JsonConvert.DeserializeObject<LnurlPayCallbackResponse>(json)
            ?? throw new InvalidOperationException("Empty callback response");
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        ValidateUrl(url);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        // 8KB cap, enough for BOLT11 + verify URL + metadata
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > MaxResponseBytes)
            throw new InvalidOperationException("Response exceeds 8KB limit");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxResponseBytes)
                throw new InvalidOperationException("Response exceeds 8KB limit");
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
    /// <summary>
    /// GET {verifyUrl} -> {status, settled, preimage}
    /// </summary>
    public async Task<LnurlVerifyResponse> VerifyPayment(string verifyUrl, CancellationToken ct = default)
    {
        var json = await GetStringAsync(verifyUrl, ct);
        return JsonConvert.DeserializeObject<LnurlVerifyResponse>(json)
            ?? throw new InvalidOperationException("Empty verify reponse");
    }
    internal void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid URL: {url}");

        // Dev mode (allowHttp) trusts the configured backend entirely:
        // no HTTPS enforcement, no private-IP blocking (regtest/localhost testing).
        // The ConnectCallback in LnurlHttpHandlerFactory has the same switch.
        if (_allowHttp)
            return;

        // HTTPS only
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"HTTPS required: {url}");

        // Block private / loopback / link-local (SSRF)
        var host = uri.DnsSafeHost;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IsPrivateOrLoopback(ip))
                throw new ArgumentException($"Blocked IP range: {host}");
            return;
        }

        // For hostnames, resolve and check each IP. This only blocks obvious SSRF at validation
        // time; a malicious DNS server can still return a public IP here and a private IP at
        // connection time (DNS rebinding). Full defense requires connecting to the validated IP
        // directly while pinning the Host header.
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            foreach (var addr in addresses)
            {
                if (IsPrivateOrLoopback(addr))
                    throw new ArgumentException($"DNS resolved to private IP: {host} → {addr}");
            }
        }
        catch (SocketException ex)
        {
            throw new ArgumentException($"DNS resolution failed: {host}", ex);
        }
    }

    internal static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal) return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            // ::1
            if (ip.Equals(IPAddress.IPv6Loopback)) return true;
            // fc00::/7 (ULA)
            if (bytes[0] >= 0xfc) return true;
            return false;
        }

        // IPv4 private ranges
        var ipv4 = ip.GetAddressBytes();
        if (ipv4[0] == 0) return true;          // 0.0.0.0/8
        if (ipv4[0] == 10) return true;
        if (ipv4[0] == 172 && ipv4[1] >= 16 && ipv4[1] <= 31) return true;
        if (ipv4[0] == 192 && ipv4[1] == 168) return true;
        if (ipv4[0] == 169 && ipv4[1] == 254) return true; // link-local

        return false;
    }

    /// <summary>
    /// LUD-12: truncate comment to commentAllowed chars. Returns null when the
    /// provider does not accept comments (commentAllowed <= 0). Truncation over
    /// throwing — a cosmetic field must not fail invoice creation.
    /// </summary>
    public static string? BuildComment(string? comment, long commentAllowed)
        => commentAllowed > 0 && !string.IsNullOrEmpty(comment)
            ? comment[..Math.Min(comment.Length, (int)commentAllowed)]
            : null;

    internal static (string user, string domain) ParseLightningAddress(string address)
    {
        var parts = address.Split('@');
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
            throw new ArgumentException($"Invalid Lightning Address: {address}");
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Parse a BOLT11 invoice string and return the fields needed by the handler.
    /// Isolated here because BOLT11PaymentRequest may not resolve in every project.
    /// </summary>
    public static ParsedBolt11 ParseBolt11(string bolt11, Network nbitcoinNetwork)
    {
        var parsed = BOLT11PaymentRequest.Parse(bolt11, nbitcoinNetwork);
        return new ParsedBolt11
        {
            PaymentHash = parsed.PaymentHash,
            AmountMsat = parsed.MinimumAmount.MilliSatoshi,
            ExpiryDate = parsed.ExpiryDate,
            DescriptionHash = parsed.DescriptionHash,
            Raw = bolt11
        };
    }

    // -- internal static JSON parse helpers (for unit testing) --

    internal static Lud06Params ParseLud06Response(string json)
        => JsonConvert.DeserializeObject<Lud06Params>(json)
           ?? throw new FormatException("Invalid LUD-06 response");

    internal static LnurlPayCallbackResponse ParseCallbackResponse(string json)
        => JsonConvert.DeserializeObject<LnurlPayCallbackResponse>(json)
           ?? throw new FormatException("Invalid callback response");

    internal static LnurlVerifyResponse ParseVerifyResponse(string json)
        => JsonConvert.DeserializeObject<LnurlVerifyResponse>(json)
           ?? throw new FormatException("Invalid verify response");
}

/// <summary>
/// Fields extracted from a BOLT11 invoice. Used to avoid direct NBitcoin BOLT11
/// dependency in the handler.
/// </summary>
public class ParsedBolt11
{
    public uint256? PaymentHash { get; set; }
    public long AmountMsat { get; set; }
    public DateTimeOffset ExpiryDate { get; set; }
    public uint256? DescriptionHash { get; set; }
    public string Raw { get; set; } = string.Empty;
}

/// <summary>
/// Parsed LUD-06 /.well-known/lnurlp/{user} response.
/// </summary>
public class Lud06Params
{
    [JsonProperty("callback")]
    public string Callback { get; set; } = string.Empty;

    [JsonProperty("minSendable")]
    public long MinSendable { get; set; }

    [JsonProperty("maxSendable")]
    public long MaxSendable { get; set; }

    [JsonProperty("metadata")]
    public string Metadata { get; set; } = string.Empty;

    [JsonProperty("commentAllowed")]
    public long CommentAllowed { get; set; }

    [JsonProperty("tag")]
    public string Tag { get; set; } = string.Empty;
}
