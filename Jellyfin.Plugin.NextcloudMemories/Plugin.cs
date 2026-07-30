using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.NextcloudMemories.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.NextcloudMemories
{
    /// <summary>
    /// The Nextcloud Memories plugin.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Application paths.</param>
        /// <param name="xmlSerializer">XML serializer.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            // Note: DataFolderPath is only populated after the plugin loader calls SetAttributes,
            // so no defaults that depend on it may be computed here.
            Instance = this;
        }

        /// <summary>
        /// Returns the configured cache root, falling back to a directory below the plugin data folder.
        /// The fallback is persisted on first use.
        /// </summary>
        /// <returns>An absolute path.</returns>
        public string ResolveCacheRoot()
        {
            if (!string.IsNullOrWhiteSpace(Configuration.CacheRoot))
            {
                return Path.GetFullPath(Configuration.CacheRoot);
            }

            var fallback = Path.Combine(DataFolderPath, "library");
            Configuration.CacheRoot = fallback;
            SaveConfiguration();
            return fallback;
        }

        /// <summary>
        /// Gets the current plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public override string Name => "Nextcloud Memories";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("b4a3f1d2-7c58-4a6e-9f21-6d0c8e3b57aa");

        /// <inheritdoc />
        public override string Description =>
            "Spiegelt Fotos, Alben und Videos aus Nextcloud Memories in eine Jellyfin-Bibliothek.";

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
}
