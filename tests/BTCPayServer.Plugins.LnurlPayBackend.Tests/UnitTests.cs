using System;
using System.Net;
using System.Text;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.LnurlPayBackend.Payments;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using Xunit;

namespace BTCPayServer.Plugins.LnurlPayBackend.Tests;

public class LnurlClientTests
{
    // ============================
    // A — Pure functions
    // ============================

    [Fact]
    public void ParseLightningAddress_Valid()
    {
        var (user, domain) = LnurlClient.ParseLightningAddress("johndoe@wallet.com");
        Assert.Equal("johndoe", user);
        Assert.Equal("wallet.com", domain);
    }

    [Fact]
    public void ParseLightningAddress_NoAtSign_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LnurlClient.ParseLightningAddress("notanaddress"));
    }

    [Fact]
    public void ParseLightningAddress_MultipleAtSigns_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LnurlClient.ParseLightningAddress("a@b@c.com"));
    }

    [Fact]
    public void IsPrivateOrLoopback_Loopback_True()
    {
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("127.0.0.1")));
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("::1")));
    }

    [Fact]
    public void IsPrivateOrLoopback_PrivateRanges_True()
    {
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("10.0.0.1")));
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("172.16.0.1")));
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("192.168.1.1")));
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("169.254.1.1")));
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("0.0.0.0")));
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("0.255.255.255")));
    }

    [Fact]
    public void IsPrivateOrLoopback_IPv4MappedIPv6_BlocksMappedIPv4()
    {
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("::ffff:10.0.0.1")));
        Assert.True(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("::ffff:192.168.1.1")));
        Assert.False(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("::ffff:1.1.1.1")));
    }

    [Fact]
    public void IsPrivateOrLoopback_Public_False()
    {
        Assert.False(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("1.1.1.1")));
        Assert.False(LnurlClient.IsPrivateOrLoopback(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void ValidateUrl_Https_Ok()
    {
        // Should not throw
        LnurlClient.ValidateUrl("https://wallet.com/.well-known/lnurlp/johndoe");
    }

    [Fact]
    public void ValidateUrl_Http_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LnurlClient.ValidateUrl("http://wallet.com/.well-known/lnurlp/johndoe"));
    }

    [Fact]
    public void ValidateUrl_Loopback_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LnurlClient.ValidateUrl("https://127.0.0.1/.well-known/lnurlp/johndoe"));
    }

    // ============================
    // B — JSON parsing
    // ============================

    [Fact]
    public void ParseLud06Response_Valid()
    {
        var json = @"{
            ""callback"": ""https://wallet.com/lnurlp/johndoe/callback"",
            ""maxSendable"": 100000000000,
            ""minSendable"": 1000,
            ""metadata"": ""[[\""text/plain\"",\""Pay to johndoe\""]]"",
            ""tag"": ""payRequest""
        }";

        var result = LnurlClient.ParseLud06Response(json);

        Assert.Equal("https://wallet.com/lnurlp/johndoe/callback", result.Callback);
        Assert.Equal(100000000000, result.MaxSendable);
        Assert.Equal(1000, result.MinSendable);
        Assert.Contains("Pay to johndoe", result.Metadata);
        Assert.Equal("payRequest", result.Tag);
    }

    [Fact]
    public void ParseLud06Response_InvalidJson_Throws()
    {
        Assert.Throws<Newtonsoft.Json.JsonReaderException>(() =>
            LnurlClient.ParseLud06Response("not json"));
    }

    [Fact]
    public void ParseCallbackResponse_Success()
    {
        var json = @"{
            ""pr"": ""lnbc10n1pjohndoe..."",
            ""verify"": ""https://wallet.com/lnurlp/johndoe/verify/abc123""
        }";

        var result = LnurlClient.ParseCallbackResponse(json);

        Assert.Equal("lnbc10n1pjohndoe...", result.Pr);
        Assert.Equal("https://wallet.com/lnurlp/johndoe/verify/abc123", result.Verify);
        // Status not in JSON → defaults to null, LUD-06 doesn't require it on success
    }

    [Fact]
    public void ParseCallbackResponse_Error()
    {
        var json = @"{""status"": ""ERROR"", ""reason"": ""Amount out of range""}";

        var result = LnurlClient.ParseCallbackResponse(json);

        Assert.Equal("ERROR", result.Status);
        Assert.Null(result.Pr);
        Assert.Null(result.Verify);
    }

    [Fact]
    public void ParseVerifyResponse_Settled()
    {
        var json = @"{
            ""status"": ""OK"",
            ""settled"": true,
            ""preimage"": ""50ac0f2c4a01046c54a0e5e8ef921d6b7ce402446e5b374520072788472970b7"",
            ""pr"": ""lnbc10n1pjohndoe...""
        }";

        var result = LnurlClient.ParseVerifyResponse(json);

        Assert.Equal("OK", result.Status);
        Assert.True(result.Settled);
        Assert.Equal("50ac0f2c4a01046c54a0e5e8ef921d6b7ce402446e5b374520072788472970b7", result.Preimage);
    }

    [Fact]
    public void ParseVerifyResponse_NotSettled()
    {
        var json = @"{""status"": ""OK"", ""settled"": false}";

        var result = LnurlClient.ParseVerifyResponse(json);

        Assert.Equal("OK", result.Status);
        Assert.False(result.Settled);
        Assert.Null(result.Preimage);
    }

    [Fact]
    public void ParseVerifyResponse_Error()
    {
        var json = @"{""status"": ""ERROR"", ""reason"": ""Payment not found""}";

        var result = LnurlClient.ParseVerifyResponse(json);

        Assert.Equal("ERROR", result.Status);
        Assert.False(result.Settled);
    }
}

public class ValidatePreimageTests
{
    // Known preimage/payment_hash pair
    // preimage: 50ac0f2c4a01046c54a0e5e8ef921d6b7ce402446e5b374520072788472970b7
    // SHA256:   e3c8... (computed from the preimage)
    private const string ValidPreimage = "50ac0f2c4a01046c54a0e5e8ef921d6b7ce402446e5b374520072788472970b7";
    private const string WrongPreimage = "00ac0f2c4a01046c54a0e5e8ef921d6b7ce402446e5b374520072788472970b7";

    private static uint256 ExpectedHash
    {
        get
        {
            var bytes = Encoders.Hex.DecodeData(ValidPreimage);
            var sha256 = NBitcoin.Crypto.Hashes.SHA256(bytes);
            Array.Reverse(sha256); // uint256 expects little-endian
            return new uint256(sha256);
        }
    }

    [Fact]
    public void ValidatePreimage_Correct_ReturnsTrue()
    {
        Assert.True(LnurlVerifyListener.ValidatePreimage(ValidPreimage, ExpectedHash));
    }

    [Fact]
    public void ValidatePreimage_Wrong_ReturnsFalse()
    {
        var wrongHash = new uint256(NBitcoin.Crypto.Hashes.SHA256(Encoders.Hex.DecodeData(WrongPreimage)));
        Assert.False(LnurlVerifyListener.ValidatePreimage(WrongPreimage, ExpectedHash));
    }
}

public class HandlerValidationTests
{
    [Fact]
    public void ConvertToMsat_OneBtc()
    {
        var msat = LnurlBackendPaymentMethodHandler.ConvertToMsat(1.0m, "BTC");
        Assert.Equal(Money.COIN * 1000L, msat);
    }

    [Fact]
    public void ConvertToMsat_SmallAmount()
    {
        var msat = LnurlBackendPaymentMethodHandler.ConvertToMsat(0.00000001m, "BTC");
                // 1 sat = 1000 millisatoshis
        const long expectedMsat = 1_000L;
        Assert.Equal(expectedMsat, msat);
    }

    [Fact]
    public void ConvertToMsat_NonBtc_Throws()
    {
        Assert.Throws<PaymentMethodUnavailableException>(() =>
            LnurlBackendPaymentMethodHandler.ConvertToMsat(1.0m, "USD"));
    }

    [Fact]
    public void ValidateAmountRange_InRange_DoesNotThrow()
    {
        // 1000 ≤ 5000 ≤ 100000000000
        var ex = Record.Exception(() =>
            LnurlBackendPaymentMethodHandler.ValidateAmountRange(5000, 1000, 100000000000));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateAmountRange_BelowMin_Throws()
    {
        Assert.Throws<PaymentMethodUnavailableException>(() =>
            LnurlBackendPaymentMethodHandler.ValidateAmountRange(500, 1000, 100000000000));
    }

    [Fact]
    public void ValidateAmountRange_AboveMax_Throws()
    {
        Assert.Throws<PaymentMethodUnavailableException>(() =>
            LnurlBackendPaymentMethodHandler.ValidateAmountRange(999999999999, 1000, 100000000000));
    }

    [Fact]
    public void ValidateAmountExact_Match_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            LnurlBackendPaymentMethodHandler.ValidateAmountExact(5000, 5000));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateAmountExact_Mismatch_Throws()
    {
        Assert.Throws<PaymentMethodUnavailableException>(() =>
            LnurlBackendPaymentMethodHandler.ValidateAmountExact(5000, 6000));
    }

    [Fact]
    public void ValidateDescriptionHash_NullHash_Returns()
    {
        // No h-tag in bolt11 → nothing to check, should not throw
        var ex = Record.Exception(() =>
            LnurlBackendPaymentMethodHandler.ValidateDescriptionHash(null, "[[\"text/plain\",\"hi\"]]"));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateDescriptionHash_Valid()
    {
        var metadata = "[[\"text/plain\",\"Pay to johndoe\"]]";
        var expectedHash = new uint256(
            NBitcoin.Crypto.Hashes.SHA256(Encoding.UTF8.GetBytes(metadata)));

        var ex = Record.Exception(() =>
            LnurlBackendPaymentMethodHandler.ValidateDescriptionHash(expectedHash, metadata));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateDescriptionHash_Invalid_Throws()
    {
        var metadata = "[[\"text/plain\",\"Pay to johndoe\"]]";
        var wrongHash = new uint256(
            Hashes.SHA256(Encoding.UTF8.GetBytes("different metadata")));

        Assert.Throws<PaymentMethodUnavailableException>(() =>
            LnurlBackendPaymentMethodHandler.ValidateDescriptionHash(wrongHash, metadata));
    }
}
