using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.LnurlPayBackend.Data;
using BTCPayServer.Plugins.LnurlPayBackend.Data.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.LnurlPayBackend;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.4.1" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddMigration<ApplicationDbContext, LnurlBackendSettingsMigration>();
    }
}
