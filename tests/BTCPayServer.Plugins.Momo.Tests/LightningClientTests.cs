using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Momo.Lightning;
using BTCPayServer.Plugins.Momo.Payments;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Momo.Tests;

// -- mock HttpMessageHandler that returns canned JSON --

internal class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(_handler(request));
}

// -- shared test data --

internal static class TestData
{
    public const string Domain = "wallet.com";
    public const string User = "johndoe";
    public const string LightningAddress = "johndoe@wallet.com";
    public const string LnurlpPath = "/.well-known/lnurlp/johndoe";
    public const string CallbackUrl = "https://wallet.com/lnurlp/johndoe/callback";
    public const string VerifyUrl = "https://wallet.com/lnurlp/johndoe/verify/abc123";

    // Valid BOLT11 from spec test vectors (parses on Network.Main)
    public const string ValidBolt11 = "lnbc1pvjluezpp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdpl2pkx2ctnv5sxxmmwwd5kgetjypeh2ursdae8g6twvus8g6rfwvs8qun0dfjkxaq8rkx3yf5tcsyz3d73gafnh3cax9rn449d9p5uxz9ezhhypd0elx87sjle52x86fux2ypatgddc6k63n7erqz25le42c4u4ecky03ylcqca784w";

    public const string PreimageHex = "50ac0f2c4a01046c54a0e5e8ef921d6b7ce402446e5b374520072788472970b7";

    public static string Lud06Json => $$"""
        {
            "callback": "{{CallbackUrl}}",
            "maxSendable": 100000000000,
            "minSendable": 1000,
            "metadata": "[[\"text/plain\",\"Pay to johndoe\"]]",
            "tag": "payRequest"
        }
        """;

    public static string CallbackJson => $$"""
        {
            "pr": "{{ValidBolt11}}",
            "verify": "{{VerifyUrl}}"
        }
        """;

    public const string VerifySettledJson = """
        {
            "status": "OK",
            "settled": true,
            "preimage": "50ac0f2c4a01046c54a0e5e8ef921d6b7ce402446e5b374520072788472970b7",
            "pr": "lnbc1pvjluez..."
        }
        """;

    public const string VerifyUnsettledJson = """
        {"status": "OK", "settled": false}
        """;
}

// ============================================================
// LnurlClient mock tests (replaces deleted E2E tests)
// ============================================================

public class LnurlClientMockedTests
{
    [Fact]
    public async Task FetchLud06Params_Success()
    {
        var handler = new FakeHttpHandler(req =>
        {
            Assert.StartsWith($"https://{TestData.Domain}{TestData.LnurlpPath}", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestData.Lud06Json, Encoding.UTF8, "application/json")
            };
        });
        var client = new LnurlClient(new HttpClient(handler));

        var result = await client.FetchLud06Params(TestData.LightningAddress);

        Assert.Equal("payRequest", result.Tag);
        Assert.Equal(TestData.CallbackUrl, result.Callback);
        Assert.Equal(1000, result.MinSendable);
    }

    [Fact]
    public async Task FetchInvoice_Success()
    {
        var handler = new FakeHttpHandler(req =>
        {
            Assert.Contains("amount=5000", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestData.CallbackJson, Encoding.UTF8, "application/json")
            };
        });
        var client = new LnurlClient(new HttpClient(handler));

        var result = await client.FetchInvoice(TestData.CallbackUrl, 5000);

        Assert.Equal(TestData.ValidBolt11, result.Pr);
        Assert.Equal(TestData.VerifyUrl, result.Verify);
    }

    [Fact]
    public async Task VerifyPayment_Settled()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(TestData.VerifySettledJson, Encoding.UTF8, "application/json")
        });
        var client = new LnurlClient(new HttpClient(handler));

        var result = await client.VerifyPayment(TestData.VerifyUrl);

        Assert.Equal("OK", result.Status);
        Assert.True(result.Settled);
        Assert.Equal(TestData.PreimageHex, result.Preimage);
    }

    [Fact]
    public async Task FetchLud06Params_NonPayRequestTag_DoesNotThrow()
    {
        var json = TestData.Lud06Json.Replace("payRequest", "withdrawRequest");
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        var client = new LnurlClient(new HttpClient(handler));

        var result = await client.FetchLud06Params(TestData.LightningAddress);
        Assert.Equal("withdrawRequest", result.Tag);
    }

    [Fact]
    public async Task FetchLud06Params_HttpError_Throws()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new LnurlClient(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.FetchLud06Params(TestData.LightningAddress));
    }
}

// ============================================================
// LightningConnectionStringHandler unit tests
// ============================================================

public class LightningConnectionStringHandlerTests
{
    [Fact]
    public void Create_Valid_ReturnsClient()
    {
        var handler = new LnurlBackendLightningConnectionStringHandler();
        var client = handler.Create(
            "type=lnurl-backend;address=johndoe@wallet.com",
            NBitcoin.Network.Main, out var error);

        Assert.NotNull(client);
        Assert.Null(error);
        Assert.Contains("type=lnurl-backend;address=johndoe@wallet.com", client!.ToString());
    }

    [Fact]
    public void Create_WrongType_ReturnsNull()
    {
        var handler = new LnurlBackendLightningConnectionStringHandler();
        var client = handler.Create(
            "type=lnd-rest;server=https://localhost:8080/",
            NBitcoin.Network.Main, out var error);

        Assert.Null(client);
        Assert.Null(error);
    }

    [Fact]
    public void Create_MissingAddress_Error()
    {
        var handler = new LnurlBackendLightningConnectionStringHandler();
        var client = handler.Create(
            "type=lnurl-backend",
            NBitcoin.Network.Main, out var error);

        Assert.Null(client);
        Assert.Contains("Lightning Address is required", error);
    }

    [Fact]
    public void Create_EmptyAddress_Error()
    {
        var handler = new LnurlBackendLightningConnectionStringHandler();
        var client = handler.Create(
            "type=lnurl-backend;address=",
            NBitcoin.Network.Main, out var error);

        Assert.Null(client);
        Assert.NotNull(error);
    }
}

// ============================================================
// LightningClient mock tests
// ============================================================

public class LightningClientMockedTests
{
    /// <summary>
    /// Mock that correctly routes: LUD-06 → Lud06Json, callback → CallbackJson.
    /// Key fix: the callback URL also contains "/lnurlp/", so we check for ".well-known" to distinguish.
    /// </summary>
    private static FakeHttpHandler MakeHandler(bool failCallback = false, bool failLud06 = false)
    {
        return new FakeHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains(".well-known"))
            {
                if (failLud06)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TestData.Lud06Json, Encoding.UTF8, "application/json")
                };
            }
            if (url.Contains("amount="))
            {
                if (failCallback)
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TestData.CallbackJson, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static LnurlClient MakeClient(FakeHttpHandler h) => new(new HttpClient(h));
    private static readonly Network TestNetwork = NBitcoin.Network.Main;

    [Fact]
    public async Task CreateInvoice_Success()
    {
        var handler = MakeHandler();
        var lnurl = MakeClient(handler);
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork, lnurl);

        var invoice = await lightning.CreateInvoice(
            LightMoney.MilliSatoshis(5000), "test memo", TimeSpan.FromMinutes(5),
            CancellationToken.None);

        Assert.NotNull(invoice.BOLT11);
        Assert.Equal(TestData.ValidBolt11, invoice.BOLT11);
        Assert.NotEmpty(invoice.PaymentHash);
        Assert.Equal(LightningInvoiceStatus.Unpaid, invoice.Status);
        Assert.NotNull(invoice.ExpiresAt);
    }

    [Fact]
    public async Task CreateInvoice_InvoiceCallbackError_Throws()
    {
        var handler = MakeHandler(failCallback: true);
        var lnurl = MakeClient(handler);
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork, lnurl);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            lightning.CreateInvoice(LightMoney.MilliSatoshis(5000), "test",
                TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task CreateInvoice_Lud06Error_Throws()
    {
        var handler = MakeHandler(failLud06: true);
        var lnurl = MakeClient(handler);
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork, lnurl);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            lightning.CreateInvoice(LightMoney.MilliSatoshis(5000), "test",
                TimeSpan.FromMinutes(5), CancellationToken.None));
    }

    [Fact]
    public async Task CreateInvoice_NoVerify_Throws()
    {
        var handler = new FakeHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains(".well-known"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TestData.Lud06Json, Encoding.UTF8, "application/json")
                };
            if (url.Contains("amount="))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    // ponytail: no verify field, simulating LUD-21-unsupported provider
                    Content = new StringContent(
                        $"{{\"pr\": \"{TestData.ValidBolt11}\"}}",
                        Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var lnurl = new LnurlClient(new HttpClient(handler));
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork, lnurl);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            lightning.CreateInvoice(LightMoney.MilliSatoshis(5000), "test",
                TimeSpan.FromMinutes(5), CancellationToken.None));
        Assert.Contains("LUD-21", ex.Message);
    }

    [Fact]
    public async Task Validate_NoVerify_ReturnsError()
    {
        var handler = new FakeHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains(".well-known"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TestData.Lud06Json, Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"pr\": \"{TestData.ValidBolt11}\"}}",
                    Encoding.UTF8, "application/json")
            };
        });
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork,
            new LnurlClient(new HttpClient(handler)));

        var result = await lightning.Validate();
        Assert.NotNull(result);
        Assert.Contains("LUD-21", result!.ErrorMessage);
    }

    [Fact]
    public async Task Validate_WithVerify_ReturnsSuccess()
    {
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork,
            MakeClient(MakeHandler()));

        var result = await lightning.Validate();
        Assert.Equal(System.ComponentModel.DataAnnotations.ValidationResult.Success, result);
    }

    [Fact]
    public async Task GetInfo_NoVerify_Throws()
    {
        // mock that returns callback without verify field
        var handler = new FakeHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains(".well-known"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TestData.Lud06Json, Encoding.UTF8, "application/json")
                };
            // return callback without verify
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"pr\": \"{TestData.ValidBolt11}\"}}",
                    Encoding.UTF8, "application/json")
            };
        });
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork,
            new LnurlClient(new HttpClient(handler)));

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => lightning.GetInfo());
        Assert.Contains("LUD-21", ex.Message);
    }

    [Fact]
    public async Task GetInfo_WithVerify_ReturnsNodeInfo()
    {
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork,
            MakeClient(MakeHandler()));

        var info = await lightning.GetInfo();
        Assert.NotNull(info);
    }

    [Fact]
    public async Task Pay_Throws()
    {
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork,
            MakeClient(MakeHandler()));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            lightning.Pay("lnbc...", CancellationToken.None));
    }

    [Fact]
    public async Task Stubs_ReturnEmpty()
    {
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork,
            MakeClient(MakeHandler()));

        Assert.Empty(await lightning.ListChannels());
        Assert.Empty(await lightning.ListPayments());
        Assert.Empty(await lightning.ListInvoices());
        Assert.NotNull(await lightning.GetBalance());
    }

    [Fact]
    public async Task CancelInvoice_NoOp()
    {
        var lightning = new LnurlBackendLightningClient(TestData.LightningAddress, TestNetwork,
            MakeClient(MakeHandler()));

        var ex = await Record.ExceptionAsync(() => lightning.CancelInvoice("somehash"));
        Assert.Null(ex);
    }
}
