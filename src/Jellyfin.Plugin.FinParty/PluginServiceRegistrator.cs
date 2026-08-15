using Jellyfin.Plugin.FinParty.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FinParty;

/// <summary>
/// Registers FinParty's services with Jellyfin's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SyncPlayReflector>();
        serviceCollection.AddSingleton<LatencyTracker>();
        serviceCollection.AddSingleton<PartyTuner>();
        serviceCollection.AddSingleton<NetworkDoctor>();

        // The tuner is the whole plugin: a background loop that keeps every live SyncPlay group
        // from stalling over a VPN.
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<PartyTuner>());
    }
}
