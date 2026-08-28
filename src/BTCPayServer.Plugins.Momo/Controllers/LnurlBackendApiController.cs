using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Momo.Models;
using BTCPayServer.Plugins.Momo.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Momo.Controllers;

[Route("api/lnurl-backend")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class LnurlBackendApiController : Controller
{
    private readonly LnurlClient _lnurlClient;
    private readonly ILogger<LnurlBackendApiController> _logger;

    public LnurlBackendApiController(LnurlClient lnurlClient, ILogger<LnurlBackendApiController> logger)
    {
        _lnurlClient = lnurlClient;
        _logger = logger;
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] TestRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(new { error = "Invalid request." });

        if (string.IsNullOrWhiteSpace(req.Address))
            return BadRequest(new { error = "Lightning Address is required." });

        try
        {
            var payParams = await _lnurlClient.FetchLud06Params(req.Address.Trim());
            if (payParams.Tag != "payRequest")
                return Ok(new { status = "error", error = "This LNURL does not support payRequest." });

            var testAmount = payParams.MinSendable;
            var invoice = await _lnurlClient.FetchInvoice(payParams.Callback, testAmount);

            if (string.IsNullOrEmpty(invoice.Verify))
                return Ok(new { status = "error", error = "This provider does not support LUD-21 verify. Choose a wallet that supports it." });

            return Ok(new
            {
                status = "ok",
                message = $"Connected. LUD-06/LUD-21 supported. Min: {payParams.MinSendable / 1000} sat, Max: {payParams.MaxSendable / 1000} sat."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LNURL backend test failed for {Address}", req.Address);
            return Ok(new { status = "error", error = "Connection failed. See server logs for details." });
        }
    }
}

public class TestRequest
{
    [MaxLength(254)]
    public string Address { get; set; }
}
