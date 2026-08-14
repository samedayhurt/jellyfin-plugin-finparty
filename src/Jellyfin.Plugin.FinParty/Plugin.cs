using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.FinParty.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FinParty;

/// <summary>
/// FinParty — SyncPlay watch parties that survive Tailscale, WireGuard and friends.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "FinParty";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("d5aefefe-1dac-4925-859f-70f70972a0d9");

    /// <inheritdoc />
    public override string Description =>
        "Watch parties for the whole family. Fixes SyncPlay over Tailscale, WireGuard and other high-latency links, " +
        "and adds a phone-friendly party remote that works with any Jellyfin client.";

    /// <summary>
    /// Gets the effective configuration, falling back to defaults when the plugin
    /// has not finished loading.
    /// </summary>
    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
