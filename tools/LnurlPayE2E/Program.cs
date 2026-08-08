using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.LnurlPayBackend.Payments;
using NBitcoin;
using NBitcoin.DataEncoders;

var address = args.Length > 0 ? args[0] : null;
if (string.IsNullOrEmpty(address))
{
    Console.Error.WriteLine("Usage: dotnet run -- <lightning-address>");
    Console.Error.WriteLine("Example: dotnet run -- you@getalby.com");
    return;
}
Console.WriteLine($"LNURL Pay E2E — testing with: {address}");

// Use the hardened handler: single DNS resolution + IP validation (SSRF guard)
// exercised against a real mainnet LNURL server (covers TLS/SNI/Host header).
var client = new LnurlClient(new HttpClient(LnurlHttpHandlerFactory.Create()));

// 1. Fetch LUD-06 params
var payParams = await client.FetchLud06Params(address);
Console.WriteLine($" tag: {payParams.Tag}");
Console.WriteLine($" range: {payParams.MinSendable} — {payParams.MaxSendable} msat");

// 2. Get invoice (minimum amount)
var invoice = await client.FetchInvoice(payParams.Callback, payParams.MinSendable);
Console.WriteLine($" bolt11: {invoice.Pr[..30]}...");
Console.WriteLine($" verify: {invoice.Verify}");

// 3. Parse BOLT11
var bolt11 = LnurlClient.ParseBolt11(invoice.Pr, Network.Main);
Console.WriteLine($" payment_hash: {bolt11.PaymentHash}");
Console.WriteLine($" amount: {bolt11.AmountMsat} msat");

// 4. Print bolt11 for payment
Console.WriteLine();
Console.WriteLine("=== COPY & PAY THIS INVOICE ===");
Console.WriteLine(invoice.Pr);
Console.WriteLine("===============================");
Console.WriteLine($"Amount: {payParams.MinSendable / 1000m} sat");
Console.WriteLine();

// 5. Poll verify until settled or timeout
Console.WriteLine("Press Enter after paying, or wait 2 min...");
var cts = new CancellationTokenSource();
_ = Task.Run(() => { Console.ReadLine(); cts.Cancel(); });

var start = DateTime.UtcNow;
while (!cts.IsCancellationRequested &&
       DateTime.UtcNow - start < TimeSpan.FromMinutes(2))
{
    var verify = await client.VerifyPayment(invoice.Verify, cts.Token);
    if (cts.IsCancellationRequested) break;

    Console.Write($"[{DateTime.UtcNow:HH:mm:ss}] status={verify.Status}, settled={verify.Settled}");

    if (verify.Settled && verify.Preimage is not null)
    {
        var preimageBytes = Encoders.Hex.DecodeData(verify.Preimage);
        var computed = NBitcoin.Crypto.Hashes.SHA256(preimageBytes);
        var valid = computed.AsSpan().SequenceEqual(bolt11.PaymentHash!.ToBytes(false));
        Console.WriteLine($", preimage={verify.Preimage[..16]}..., valid={valid}");
        Console.WriteLine(valid ? "✅ PAYMENT VERIFIED" : "❌ PREIMAGE MISMATCH");
        return;
    }

    Console.WriteLine();
    await Task.Delay(3000, cts.Token);
}

Console.WriteLine(cts.IsCancellationRequested ? "Cancelled." : "Timeout.");
