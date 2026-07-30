using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.NextcloudMemories.Configuration
{
    /// <summary>
    /// How album folders reference the primary file of the timeline tree.
    /// </summary>
    public enum LinkMode
    {
        /// <summary>Create a symbolic link (default, no extra disk usage).</summary>
        Symlink = 0,

        /// <summary>Copy the file (uses disk space, but works everywhere).</summary>
        Copy = 1
    }

    /// <summary>
    /// How videos are represented in the mirrored library.
    /// </summary>
    public enum VideoMode
    {
        /// <summary>Write a .strm file pointing at the plugin's streaming proxy.</summary>
        Strm = 0,

        /// <summary>Download the original video into the cache.</summary>
        Download = 1,

        /// <summary>Ignore videos entirely.</summary>
        Skip = 2
    }

    /// <summary>
    /// Plugin configuration.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Base URL of the Nextcloud instance, e.g. https://cloud.example.com.</summary>
        public string ServerUrl { get; set; } = string.Empty;

        /// <summary>Nextcloud user name.</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>Nextcloud app password (Settings -> Security -> Devices &amp; sessions).</summary>
        public string AppPassword { get; set; } = string.Empty;

        /// <summary>Root directory of the mirrored library. Must be writable by the Jellyfin process.</summary>
        public string CacheRoot { get; set; } = string.Empty;

        /// <summary>Name of the Jellyfin library that gets created.</summary>
        public string LibraryName { get; set; } = "Nextcloud Fotos";

        /// <summary>Create the Jellyfin library automatically on first sync.</summary>
        public bool AutoCreateLibrary { get; set; } = true;

        /// <summary>Edge length in pixels for downloaded previews.</summary>
        public int PreviewSize { get; set; } = 2048;

        /// <summary>Download original files instead of Memories previews. Experts only (HEIC/RAW may not render).</summary>
        public bool DownloadOriginals { get; set; }

        /// <summary>Mirror the timeline as Year/Month folders.</summary>
        public bool EnableTimeline { get; set; } = true;

        /// <summary>Mirror Memories albums as folders.</summary>
        public bool EnableAlbums { get; set; } = true;

        /// <summary>Album cluster ids to sync. Empty means "all albums".</summary>
        public string[] SelectedAlbums { get; set; } = Array.Empty<string>();

        /// <summary>Only sync photos taken on or after this date (ISO yyyy-MM-dd). Empty means no lower bound.</summary>
        public string DateFrom { get; set; } = string.Empty;

        /// <summary>Only sync photos taken on or before this date (ISO yyyy-MM-dd). Empty means no upper bound.</summary>
        public string DateTo { get; set; } = string.Empty;

        /// <summary>How videos are handled.</summary>
        public VideoMode VideoMode { get; set; } = VideoMode.Strm;

        /// <summary>How album entries reference the timeline file.</summary>
        public LinkMode LinkMode { get; set; } = LinkMode.Symlink;

        /// <summary>
        /// Base URL under which this Jellyfin server reaches itself. Used inside generated .strm files.
        /// </summary>
        public string JellyfinBaseUrl { get; set; } = "http://127.0.0.1:8096";

        /// <summary>Secret used to sign streaming tokens. Generated on first use. Changing it invalidates all .strm files.</summary>
        public string StreamSecret { get; set; } = string.Empty;

        /// <summary>Number of parallel downloads.</summary>
        public int ParallelDownloads { get; set; } = 4;

        /// <summary>Interval of the scheduled sync task in hours. 0 disables the default trigger.</summary>
        public int SyncIntervalHours { get; set; } = 12;

        /// <summary>Trigger a Jellyfin library scan after every successful sync.</summary>
        public bool ScanAfterSync { get; set; } = true;

        /// <summary>
        /// Fetch tags, people and places per item during library scans. One extra HTTP request per photo,
        /// so this is off by default for large libraries.
        /// </summary>
        public bool FetchDetailedMetadata { get; set; }

        /// <summary>HTTP timeout for a single request, in seconds.</summary>
        public int RequestTimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// Query parameter name used to filter the timeline endpoints by album.
        /// Differs between Memories versions; check GET /apps/memories/api/describe.
        /// </summary>
        public string AlbumQueryParameter { get; set; } = "albums";

        /// <summary>Timestamp of the last successful sync (round-trip ISO string). Empty if never.</summary>
        public string LastSyncUtc { get; set; } = string.Empty;

        /// <summary>Human readable result of the last sync, shown on the configuration page.</summary>
        public string LastSyncResult { get; set; } = string.Empty;
    }
}
