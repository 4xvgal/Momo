#nullable enable
using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Momo.Payments;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Momo.Controllers;

[Route("stores/{storeId}")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanViewStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UILnurlBackendController : Controller
{
    private readonly StoreRepository _storeRepository;
    private readonly LnurlClient _lnurlClient;
    private readonly ILogger<UILnurlBackendController> _logger;
    private static readonly PaymentMethodId Pmi = Plugin.Pmi;

    private StoreData? Store => HttpContext.GetStoreData();

    public UILnurlBackendController(
        StoreRepository storeRepository,
        LnurlClient lnurlClient,
        ILogger<UILnurlBackendController> logger)
    {
        _storeRepository = storeRepository;
        _lnurlClient = lnurlClient;
        _logger = logger;
    }

    [HttpGet("lnurl-backend")]
    public async Task<IActionResult> Settings(string storeId)
    {
        if (Store is null) return NotFound();

        var rawConfig = Store.GetPaymentMethodConfig(Pmi);
        var vm = new LnurlBackendViewModel
        {
            StoreId = storeId,
            LightningAddress = rawConfig?["lightningAddress"]?.Value<string>() ?? string.Empty,
            Enabled = !Store.GetStoreBlob().GetExcludedPaymentMethods().Match(Pmi)
        };

        return View("StoreSettings", vm);
    }

    [HttpPost("lnurl-backend")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    public async Task<IActionResult> Settings(string storeId, LnurlBackendViewModel vm, string command)
    {
        if (Store is null) return NotFound();

        if (command == "save")
        {
            if (string.IsNullOrWhiteSpace(vm.LightningAddress))
            {
                ModelState.AddModelError(nameof(vm.LightningAddress), "Lightning Address is required.");
                return View("StoreSettings", vm);
            }

            // Validate LUD-06 + LUD-21
            try
            {
                var payParams = await _lnurlClient.FetchLud06Params(vm.LightningAddress.Trim());
                if (payParams.Tag != "payRequest")
                {
                    ModelState.AddModelError(nameof(vm.LightningAddress),
                        "This LNURL does not support payRequest.");
                    return View("StoreSettings", vm);
                }

                var testAmount = payParams.MinSendable;
                var invoiceResp = await _lnurlClient.FetchInvoice(payParams.Callback, testAmount);

                if (string.IsNullOrEmpty(invoiceResp.Verify))
                {
                    ModelState.AddModelError(nameof(vm.LightningAddress),
                        "This provider does not support LUD-21 verify. Choose a wallet that supports it.");
                    return View("StoreSettings", vm);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LNURL backend validation failed for {Address}", vm.LightningAddress);
                ModelState.AddModelError(nameof(vm.LightningAddress),
                    "Validation failed. See server logs for details.");
                return View("StoreSettings", vm);
            }

            // Save config
            var config = new JObject
            {
                ["lightningAddress"] = vm.LightningAddress.Trim()
            };
            Store.SetPaymentMethodConfig(Pmi, config);

            var blob = Store.GetStoreBlob();
            blob.SetExcluded(Pmi, !vm.Enabled);
            Store.SetStoreBlob(blob);

            await _storeRepository.UpdateStore(Store);

            TempData["SuccessMessage"] = $"Lightning Address {vm.LightningAddress} registered.";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        return View("StoreSettings", vm);
    }
}

public class LnurlBackendViewModel
{
    public string StoreId { get; set; } = string.Empty;
    public string LightningAddress { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
