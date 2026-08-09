// LUD-21 dev LNURL-pay server for regtest testing.
//
// Serves LUD-06 payRequest + LUD-21 verify against a regtest lnd
// (no TLS, no macaroons — see BTCPayServer.Tests docker-compose).
//
// Env overrides:
//   LUD21_PORT      (default 5001)
//   LUD21_LND_URL   (default http://127.0.0.1:18080)
//   LUD21_BASE_URL  (default http://127.0.0.1:{PORT})
//
// Register "test@127.0.0.1:5001" in the plugin (dev mode allowHttp must be on).
// Pay with the customer lnd: lncli payinvoice --force <bolt11>

using System.Net;
using System.Text;
using System.Text.Json;

var port = int.Parse(Environment.GetEnvironmentVariable("LUD21_PORT") ?? "5001");
var lndUrl = Environment.GetEnvironmentVariable("LUD21_LND_URL") ?? "http://127.0.0.1:18080";
var baseUrl = Environment.GetEnvironmentVariable("LUD21_BASE_URL") ?? $"http://127.0.0.1:{port}";

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
using var listener = new HttpListener();
listener.Prefixes.Add($"{baseUrl}/");
listener.Start();
Console.WriteLine($"LUD-21 dev LNURL-pay server on {baseUrl} (lnd: {lndUrl})");

var settledLogged = new HashSet<string>();

while (true)
{
    var ctx = await listener.GetContextAsync();
    _ = Task.Run(() => HandleAsync(ctx));
}

async Task HandleAsync(HttpListenerContext ctx)
{
    try
    {
        var path = ctx.Request.Url!.AbsolutePath;
        if (ctx.Request.HttpMethod == "GET" && path.StartsWith("/.well-known/lnurlp/"))
        {
            // LUD-06: payRequest params
            var user = path["/.well-known/lnurlp/".Length..];
            if (user.Length == 0) throw new InvalidOperationException("missing username");
            await JsonAsync(ctx, new
            {
                callback = $"{baseUrl}/callback/{user}",
                minSendable = 1000L,
                maxSendable = 100_000_000_000L,
                metadata = Metadata(user),
                tag = "payRequest"
            });
        }
        else if (ctx.Request.HttpMethod == "GET" && path.StartsWith("/callback/"))
        {
            // LUD-06 callback: create a lnd invoice for the requested amount
            var user = path["/callback/".Length..];
            var amount = long.Parse(ctx.Request.QueryString["amount"]
                ?? throw new InvalidOperationException("missing amount"));
            var metadata = Metadata(user);

            var createBody = JsonSerializer.Serialize(new { value = (amount + 999) / 1000, memo = metadata, expiry = 3600L });
            var created = await http.PostAsync($"{lndUrl}/v1/invoices",
                new StringContent(createBody, Encoding.UTF8, "application/json"));
            created.EnsureSuccessStatusCode();
            var lndInvoice = JsonSerializer.Deserialize<LndInvoice>(await created.Content.ReadAsStringAsync())
                ?? throw new InvalidOperationException("empty lnd response");
            var hashHex = Convert.ToHexString(Convert.FromBase64String(lndInvoice.RHash!)).ToLowerInvariant();
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] INVOICE CREATED: {amount} msat, hash={hashHex}, bolt11={lndInvoice.PaymentRequest?[..40]}...");

            await JsonAsync(ctx, new
            {
                pr = lndInvoice.PaymentRequest,
                verify = $"{baseUrl}/verify/{hashHex}"
            });
        }
        else if (ctx.Request.HttpMethod == "GET" && path.StartsWith("/verify/"))
        {
            // LUD-21: settlement status + preimage
            var hash = path["/verify/".Length..];
            var lookup = await http.GetAsync($"{lndUrl}/v1/invoice/{hash}");
            lookup.EnsureSuccessStatusCode();
            var inv = JsonSerializer.Deserialize<LndInvoiceStatus>(await lookup.Content.ReadAsStringAsync())
                ?? throw new InvalidOperationException("empty lnd response");

            // Log every poll so it's visible whether/at what rate the plugin polls
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] VERIFY POLL: {hash[..16]}..., settled={inv.Settled}");

            if (inv.Settled && settledLogged.Add(hash))
            {
                Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] PAYMENT DETECTED: {hash}, preimage={HexFromBase64(inv.RPreimage)}");
            }

            await JsonAsync(ctx, new
            {
                status = "OK",
                settled = inv.Settled,
                preimage = inv.Settled ? HexFromBase64(inv.RPreimage) : null
            });
        }
        else
        {
            ctx.Response.StatusCode = 404;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {ex.Message}");
        await JsonAsync(ctx, new { status = "ERROR", reason = ex.Message }, 500);
    }
    finally
    {
        ctx.Response.Close();
    }
}

string Metadata(string user) =>
    JsonSerializer.Serialize(new[]
    {
        new[] { "text/identifier", $"{user}@{baseUrl[(baseUrl.IndexOf("://") + 3)..]}" },
        new[] { "text/plain", "LUD-21 dev server" }
    });

static string HexFromBase64(string? b64) =>
    b64 is null ? "" : Convert.ToHexString(Convert.FromBase64String(b64)).ToLowerInvariant();

static async Task JsonAsync(HttpListenerContext ctx, object body, int status = 200)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.OutputStream.WriteAsync(bytes);
}

record LndInvoice(
    [property: System.Text.Json.Serialization.JsonPropertyName("r_hash")] string? RHash,
    [property: System.Text.Json.Serialization.JsonPropertyName("payment_request")] string? PaymentRequest);
record LndInvoiceStatus(
    [property: System.Text.Json.Serialization.JsonPropertyName("settled")] bool Settled,
    [property: System.Text.Json.Serialization.JsonPropertyName("r_preimage")] string? RPreimage);
