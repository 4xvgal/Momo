using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Momo.Data;
using BTCPayServer.Plugins.Momo.Data.Migrations;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Momo.Lightning;
using BTCPayServer.Plugins.Momo.Payments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using System.Net.Http;

namespace BTCPayServer.Plugins.Momo;

public class Plugin : BaseBTCPayServerPlugin
{
    public static readonly PaymentMethodId Pmi = new("BTC-MOMO-LNADDR");

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.3.9" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddMigration<ApplicationDbContext, LnurlBackendSettingsMigration>();
        services.AddMigration<ApplicationDbContext, LnurlBackendInvoicesMigration>();
        // Dev-only: "LnurlBackendAllowHttp": true in appsettings.dev.json lets a local
        // regtest instance (http://localhost) be used as the LNURL backend. It opens
        // plain HTTP AND loopback — never enable in production.
        services.AddSingleton(sp =>
        {
            var devMode = sp.GetRequiredService<IConfiguration>().GetValue<bool>("LnurlBackendAllowHttp");
            return new LnurlClient(
                new HttpClient(LnurlHttpHandlerFactory.Create(allowLoopback: devMode)),
                allowHttp: devMode);
        });
        services.AddSingleton<IPaymentMethodHandler, LnurlBackendPaymentMethodHandler>();
        services.AddSingleton<IHostedService, LnurlVerifyListener>();
        services.AddSingleton<ICheckoutModelExtension, LnurlBackendCheckoutModelExtension>();
        services.AddSingleton<ILightningConnectionStringHandler, LnurlBackendLightningConnectionStringHandler>();
        services.AddSingleton<LnurlBackendInvoiceRepository>();

        // Lightning setup tab integration — partials are compiled into the plugin
        // assembly by the Razor SDK, so they are registered by name (not path)
        services.AddUIExtension("ln-payment-method-setup-tabhead", "LnurlLightningSetupTabHead");
        services.AddUIExtension("ln-payment-method-setup-tab", "LnurlLightningSetupTab");
        // Sidebar entry: Wallets accordion only
        services.AddUIExtension("store-wallets-nav", "NavExtension");
    }
}
