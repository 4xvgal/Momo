using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.LnurlPayBackend.Data;
using BTCPayServer.Plugins.LnurlPayBackend.Data.Migrations;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.LnurlPayBackend.Lightning;
using BTCPayServer.Plugins.LnurlPayBackend.Payments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net.Http;

namespace BTCPayServer.Plugins.LnurlPayBackend;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.3.9" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddMigration<ApplicationDbContext, LnurlBackendSettingsMigration>();
        services.AddMigration<ApplicationDbContext, LnurlBackendInvoicesMigration>();
        services.AddHttpClient<LnurlClient>()
            .ConfigurePrimaryHttpMessageHandler(() => LnurlHttpHandlerFactory.Create(allowLoopback:false));
        services.AddSingleton<IPaymentMethodHandler, LnurlBackendPaymentMethodHandler>();
        services.AddSingleton<IHostedService, LnurlVerifyListener>();
        services.AddSingleton<ICheckoutModelExtension, LnurlBackendCheckoutModelExtension>();
        services.AddSingleton<ILightningConnectionStringHandler, LnurlBackendLightningConnectionStringHandler>();
        services.AddSingleton<LnurlBackendInvoiceRepository>();

        // Lightning setup tab integration — partials are compiled into the plugin
        // assembly by the Razor SDK, so they are registered by name (not path)
        services.AddUIExtension("ln-payment-method-setup-tabhead", "LnurlLightningSetupTabHead");
        services.AddUIExtension("ln-payment-method-setup-tab", "LnurlLightningSetupTab");
        services.AddUIExtension("store-category-nav", "NavExtension");
    }
}
