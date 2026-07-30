using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextcloudMemories.Sync
{
    /// <summary>
    /// One mirrored file.
    /// </summary>
    public class IndexEntry
    {
        /// <summary>Gets or sets the Nextcloud file id.</summary>
        public long FileId { get; set; }

        /// <summary>Gets or sets the path relative to the cache root, using forward slashes.</summary>
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>Gets or sets the etag the local copy was produced from.</summary>
        public string? Etag { get; set; }

        /// <summary>Gets or sets a value indicating whether this entry is a video.</summary>
        public bool IsVideo { get; set; }

        /// <summary>Gets or sets the capture time in unix seconds.</summary>
        public long CaptureUnix { get; set; }

        /// <summary>Gets or sets the preview size the copy was produced with. 0 = original, -1 = .strm.</summary>
        public int PreviewSize { get; set; }

        /// <summary>Gets or sets a value indicating whether this entry is an album link to a timeline file.</summary>
        public bool IsAlbumEntry { get; set; }

        /// <summary>Gets or sets the album name, when this is an album entry.</summary>
        public string? AlbumName { get; set; }
    }

    /// <summary>
    /// Serialized index state.
    /// </summary>
    public class IndexData
    {
        /// <summary>Gets or sets the schema version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Gets or sets the time of the last successful sync.</summary>
        public DateTime LastSyncUtc { get; set; }

        /// <summary>Gets or sets all mirrored entries.</summary>
        public List<IndexEntry> Entries { get; set; } = new List<IndexEntry>();
    }

    /// <summary>
    /// Persists the mapping between Nextcloud file ids and mirrored files.
    /// </summary>
    public class LibraryIndex
    {
        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

        private readonly ILogger<LibraryIndex> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private IndexData _data = new IndexData();
        private Dictionary<string, IndexEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<long, IndexEntry> _byFileId = new();
        private bool _loaded;

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryIndex"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public LibraryIndex(ILogger<LibraryIndex> logger)
        {
            _logger = logger;
        }

        private static string IndexPath =>
            Path.Combine(
                Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("Plugin not initialised."),
                "index.json");

        /// <summary>
        /// Gets the time of the last successful sync.
        /// </summary>
        public DateTime LastSyncUtc => _data.LastSyncUtc;

        /// <summary>
        /// Gets the number of mirrored entries.
        /// </summary>
        public int Count => _data.Entries.Count;

        /// <summary>
        /// Loads the index from disk if that has not happened yet.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (_loaded)
            {
                return;
            }

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_loaded)
                {
                    return;
                }

                var path = IndexPath;
                if (File.Exists(path))
                {
                    try
                    {
                        await using var stream = File.OpenRead(path);
                        var data = await JsonSerializer
                            .DeserializeAsync<IndexData>(stream, _jsonOptions, cancellationToken)
                            .ConfigureAwait(false);
                        if (data is not null)
                        {
                            _data = data;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not read the index, starting with an empty one.");
                        _data = new IndexData();
                    }
                }

                Reindex();
                _loaded = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        private void Reindex()
        {
            _byPath = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
            _byFileId = new Dictionary<long, IndexEntry>();

            foreach (var entry in _data.Entries)
            {
                _byPath[entry.RelativePath] = entry;

                if (!entry.IsAlbumEntry || !_byFileId.ContainsKey(entry.FileId))
                {
                    _byFileId[entry.FileId] = entry;
                }
            }
        }

        /// <summary>
        /// Replaces the whole index and writes it to disk. Entries missing from
        /// <paramref name="entries"/> are dropped, so this is only safe after a complete run.
        /// </summary>
        /// <param name="entries">The new entries.</param>
        /// <param name="lastSyncUtc">The sync timestamp.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task ReplaceAsync(
            IReadOnlyCollection<IndexEntry> entries,
            DateTime lastSyncUtc,
            CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _data = new IndexData
                {
                    Version = 1,
                    LastSyncUtc = lastSyncUtc,
                    Entries = new List<IndexEntry>(entries)
                };

                Reindex();
                _loaded = true;

                await SaveUnlockedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Merges entries into the existing index and persists it. Used for checkpoints during a
        /// running sync so that an interrupted run does not throw away the work done so far.
        /// </summary>
        /// <param name="entries">The entries to add or update.</param>
        /// <returns>A task.</returns>
        public async Task MergeAsync(IReadOnlyCollection<IndexEntry> entries)
        {
            if (entries.Count == 0)
            {
                return;
            }

            // No cancellation token here on purpose: a checkpoint that runs while the sync is being
            // cancelled still has to acquire the lock and finish writing.
            await _lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                // Build a new dictionary instead of mutating the live one: GetByPath() reads it
                // without holding the lock.
                var merged = new Dictionary<string, IndexEntry>(_byPath, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    merged[entry.RelativePath] = entry;
                }

                _data.Entries = merged.Values.ToList();
                Reindex();
                _loaded = true;

                await SaveUnlockedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task SaveUnlockedAsync(CancellationToken cancellationToken)
        {
            var path = IndexPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, _data, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }

        /// <summary>
        /// Gets a snapshot of the current entries keyed by relative path.
        /// </summary>
        /// <returns>The snapshot.</returns>
        public IReadOnlyDictionary<string, IndexEntry> GetByPath() =>
            new Dictionary<string, IndexEntry>(_byPath, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Looks up an entry by Nextcloud file id.
        /// </summary>
        /// <param name="fileId">The file id.</param>
        /// <returns>The entry, or <c>null</c>.</returns>
        public IndexEntry? FindByFileId(long fileId) =>
            _byFileId.TryGetValue(fileId, out var entry) ? entry : null;
    }
}
