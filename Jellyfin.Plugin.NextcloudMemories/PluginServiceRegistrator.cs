using System;
using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.NextcloudMemories.Api;
using Jellyfin.Plugin.NextcloudMemories.Streaming;
using Jellyfin.Plugin.NextcloudMemories.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.NextcloudMemories
{
    /// <summary>
    /// Registers the plugin services with Jellyfin's DI container.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection
                .AddHttpClient<MemoriesApiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    MaxConnectionsPerServer = 16,
                    AllowAutoRedirect = true
                });

            serviceCollection.AddSingleton<LibraryIndex>();
            serviceCollection.AddSingleton<StreamTokenService>();
            serviceCollection.AddSingleton<SyncService>();
        }
    }
}
