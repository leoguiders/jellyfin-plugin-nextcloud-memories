using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NextcloudMemories.Api;
using Jellyfin.Plugin.NextcloudMemories.Configuration;
using Jellyfin.Plugin.NextcloudMemories.Streaming;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextcloudMemories.Sync
{
    /// <summary>
    /// Outcome of one sync run.
    /// </summary>
    public class SyncResult
    {
        /// <summary>Gets or sets the number of newly downloaded files.</summary>
        public int Downloaded { get; set; }

        /// <summary>Gets or sets the number of files that were already up to date.</summary>
        public int Unchanged { get; set; }

        /// <summary>Gets or sets the number of album links created.</summary>
        public int Linked { get; set; }

        /// <summary>Gets or sets the number of removed files.</summary>
        public int Removed { get; set; }

        /// <summary>Gets or sets the number of failures.</summary>
        public int Failed { get; set; }

        /// <summary>Gets or sets the number of bytes transferred.</summary>
        public long BytesTransferred { get; set; }

        /// <summary>Gets or sets warnings that should surface on the configuration page.</summary>
        public List<string> Warnings { get; set; } = new List<string>();

        /// <summary>Gets or sets the wall clock duration.</summary>
        public TimeSpan Duration { get; set; }

        /// <inheritdoc />
        public override string ToString()
        {
            var text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} neu, {1} unveraendert, {2} verknuepft, {3} entfernt, {4} Fehler, {5:F1} MB, {6:F0}s",
                Downloaded,
                Unchanged,
                Linked,
                Removed,
                Failed,
                BytesTransferred / 1024d / 1024d,
                Duration.TotalSeconds);

            if (Warnings.Count > 0)
            {
                text += " | " + string.Join(" | ", Warnings);
            }

            return text;
        }
    }

    /// <summary>
    /// Mirrors Nextcloud Memories into a local directory tree.
    /// </summary>
    public class SyncService
    {
        private const string TimelineFolder = "Zeitachse";
        private const string AlbumFolder = "Alben";

        private readonly MemoriesApiClient _api;
        private readonly LibraryIndex _index;
        private readonly ILibraryManager _libraryManager;
        private readonly StreamTokenService _tokens;
        private readonly ILogger<SyncService> _logger;
        private readonly SemaphoreSlim _runLock = new(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncService"/> class.
        /// </summary>
        /// <param name="api">The Memories API client.</param>
        /// <param name="index">The library index.</param>
        /// <param name="libraryManager">The Jellyfin library manager.</param>
        /// <param name="tokens">The stream token service.</param>
        /// <param name="logger">The logger.</param>
        public SyncService(
            MemoriesApiClient api,
            LibraryIndex index,
            ILibraryManager libraryManager,
            StreamTokenService tokens,
            ILogger<SyncService> logger)
        {
            _api = api;
            _index = index;
            _libraryManager = libraryManager;
            _tokens = tokens;
            _logger = logger;
        }

        /// <summary>
        /// Gets a value indicating whether a sync is currently running.
        /// </summary>
        public bool IsRunning { get; private set; }

        private sealed class DesiredFile
        {
            public MemoriesFile File { get; init; } = null!;

            public string RelativePath { get; init; } = string.Empty;

            public string? AlbumName { get; init; }

            public string? LinkTargetRelativePath { get; init; }

            public bool IsLink => LinkTargetRelativePath is not null;
        }

        /// <summary>
        /// Runs a full sync.
        /// </summary>
        /// <param name="progress">Progress sink, reporting 0..100.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result.</returns>
        public async Task<SyncResult> RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Es laeuft bereits eine Synchronisierung.");
            }

            IsRunning = true;
            var started = DateTime.UtcNow;
            var result = new SyncResult();

            try
            {
                var config = Plugin.Instance?.Configuration
                             ?? throw new InvalidOperationException("Plugin is not initialised.");

                Validate(config);

                var cacheRoot = Plugin.Instance!.ResolveCacheRoot();
                Directory.CreateDirectory(cacheRoot);

                await _index.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

                progress?.Report(1);

                var desired = await CollectDesiredAsync(config, result, progress, cancellationToken)
                    .ConfigureAwait(false);

                progress?.Report(15);

                // Safety net: never wipe an existing mirror because the API returned nothing.
                if (desired.Count == 0 && _index.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Die Memories-API hat keine Dateien geliefert, obwohl bereits "
                        + _index.Count.ToString(CultureInfo.InvariantCulture)
                        + " Dateien gespiegelt sind. Der Sync wird abgebrochen, damit der Cache nicht "
                        + "geloescht wird. Bitte Verbindung und Filter pruefen.");
                }

                result.Removed = RemoveOrphans(cacheRoot, desired.Keys, result);

                progress?.Report(20);

                var entries = await MaterialiseAsync(config, cacheRoot, desired, result, progress, cancellationToken)
                    .ConfigureAwait(false);

                PruneEmptyDirectories(cacheRoot);

                await _index.ReplaceAsync(entries, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);

                progress?.Report(97);

                if (config.AutoCreateLibrary)
                {
                    await EnsureLibraryAsync(config, cacheRoot, result).ConfigureAwait(false);
                }

                if (config.ScanAfterSync)
                {
                    _logger.LogInformation("Starte Jellyfin-Bibliotheksscan.");
                    _libraryManager.QueueLibraryScan();
                }

                result.Duration = DateTime.UtcNow - started;

                config.LastSyncUtc = started.ToString("O", CultureInfo.InvariantCulture);
                config.LastSyncResult = result.ToString();
                Plugin.Instance!.SaveConfiguration();

                progress?.Report(100);
                _logger.LogInformation("Memories-Sync abgeschlossen: {Result}", result);
                return result;
            }
            finally
            {
                IsRunning = false;
                _runLock.Release();
            }
        }

        private static void Validate(PluginConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config.ServerUrl))
            {
                throw new InvalidOperationException("Nextcloud-URL fehlt.");
            }

            if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.AppPassword))
            {
                throw new InvalidOperationException("Benutzername oder App-Passwort fehlt.");
            }

            if (!config.EnableTimeline && !config.EnableAlbums)
            {
                throw new InvalidOperationException("Weder Zeitachse noch Alben sind aktiviert.");
            }
        }

        private async Task<Dictionary<string, DesiredFile>> CollectDesiredAsync(
            PluginConfiguration config,
            SyncResult result,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            var desired = new Dictionary<string, DesiredFile>(StringComparer.OrdinalIgnoreCase);
            var timelinePaths = new Dictionary<long, string>();

            var from = ParseDate(config.DateFrom);
            var to = ParseDate(config.DateTo);

            if (config.EnableTimeline)
            {
                var days = await _api.GetDaysAsync(cancellationToken).ConfigureAwait(false);
                var filtered = days
                    .Where(d => InRange(d.ToDate(), from, to))
                    .OrderByDescending(d => d.DayId)
                    .ToList();

                _logger.LogInformation("Zeitachse: {Count} Tage im gewaehlten Zeitraum.", filtered.Count);

                var done = 0;
                foreach (var day in filtered)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var files = await _api.GetDayAsync(day.DayId, cancellationToken).ConfigureAwait(false);
                    foreach (var file in files)
                    {
                        if (file.IsVideo && config.VideoMode == VideoMode.Skip)
                        {
                            continue;
                        }

                        var relative = BuildTimelinePath(config, file);
                        desired[relative] = new DesiredFile { File = file, RelativePath = relative };
                        timelinePaths[file.FileId] = relative;
                    }

                    done++;
                    progress?.Report(1 + (9d * done / Math.Max(filtered.Count, 1)));
                }
            }

            if (config.EnableAlbums)
            {
                await CollectAlbumsAsync(config, desired, timelinePaths, result, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            _logger.LogInformation("Soll-Zustand: {Count} Dateien.", desired.Count);
            return desired;
        }

        private async Task CollectAlbumsAsync(
            PluginConfiguration config,
            Dictionary<string, DesiredFile> desired,
            Dictionary<long, string> timelinePaths,
            SyncResult result,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<MemoriesCluster> albums;
            try
            {
                albums = await _api.GetClustersAsync("albums", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Alben konnten nicht geladen werden.");
                result.Warnings.Add("Alben konnten nicht geladen werden: " + ex.Message);
                return;
            }

            var selected = config.SelectedAlbums;
            if (selected.Length > 0)
            {
                var set = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
                albums = albums.Where(a => set.Contains(a.GetFilterValue())).ToList();
            }

            _logger.LogInformation("Alben: {Count} ausgewaehlt.", albums.Count);

            var done = 0;
            foreach (var album in albums)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filter = album.GetFilterValue();
                var albumName = Sanitize(album.Name ?? filter);
                if (string.IsNullOrEmpty(albumName))
                {
                    continue;
                }

                var files = new List<MemoriesFile>();
                try
                {
                    var days = await _api.GetAlbumDaysAsync(filter, cancellationToken).ConfigureAwait(false);
                    foreach (var day in days)
                    {
                        var dayFiles = await _api.GetAlbumDayAsync(filter, day.DayId, cancellationToken)
                            .ConfigureAwait(false);
                        files.AddRange(dayFiles);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Album {Album} konnte nicht geladen werden.", albumName);
                    result.Warnings.Add($"Album '{albumName}' uebersprungen: {ex.Message}");
                    continue;
                }

                // Guard against a wrong album query parameter: if filtering silently does not
                // apply, the endpoint returns the entire timeline for every album.
                if (album.Count > 0 && files.Count > (album.Count * 2) + 20)
                {
                    var message = string.Format(
                        CultureInfo.InvariantCulture,
                        "Album '{0}' liefert {1} Dateien, erwartet waren {2}. Der Filter-Parameter '{3}' passt "
                        + "vermutlich nicht zu dieser Memories-Version. Album wird uebersprungen. "
                        + "Pruefe GET /apps/memories/api/describe.",
                        albumName,
                        files.Count,
                        album.Count,
                        config.AlbumQueryParameter);

                    _logger.LogError("{Message}", message);
                    result.Warnings.Add(message);
                    continue;
                }

                foreach (var file in files)
                {
                    if (file.IsVideo && config.VideoMode == VideoMode.Skip)
                    {
                        continue;
                    }

                    var relative = BuildAlbumPath(config, albumName, file);
                    timelinePaths.TryGetValue(file.FileId, out var target);

                    desired[relative] = new DesiredFile
                    {
                        File = file,
                        RelativePath = relative,
                        AlbumName = albumName,
                        LinkTargetRelativePath = target
                    };
                }

                done++;
                progress?.Report(10 + (5d * done / Math.Max(albums.Count, 1)));
            }
        }

        private int RemoveOrphans(string cacheRoot, IEnumerable<string> desiredPaths, SyncResult result)
        {
            var keep = new HashSet<string>(desiredPaths, StringComparer.OrdinalIgnoreCase);
            var removed = 0;

            // Materialise the listing first: deleting while enumerating is not safe on all platforms.
            var onDisk = Directory.GetFiles(cacheRoot, "*", SearchOption.AllDirectories);

            foreach (var absolute in onDisk)
            {
                var relative = ToRelative(cacheRoot, absolute);

                if (absolute.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || !keep.Contains(relative))
                {
                    try
                    {
                        File.Delete(absolute);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Konnte {Path} nicht loeschen.", absolute);
                        result.Failed++;
                    }
                }
            }

            if (removed > 0)
            {
                _logger.LogInformation("{Count} verwaiste Dateien entfernt.", removed);
            }

            return removed;
        }

        private async Task<List<IndexEntry>> MaterialiseAsync(
            PluginConfiguration config,
            string cacheRoot,
            Dictionary<string, DesiredFile> desired,
            SyncResult result,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            var known = _index.GetByPath();
            var entries = new ConcurrentBag<IndexEntry>();
            var previewSize = config.DownloadOriginals ? 0 : config.PreviewSize;

            var primaries = desired.Values.Where(d => !d.IsLink).ToList();
            var links = desired.Values.Where(d => d.IsLink).ToList();

            var processed = 0;
            var total = Math.Max(primaries.Count + links.Count, 1);

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(config.ParallelDownloads, 1, 16),
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(primaries, options, async (item, token) =>
            {
                try
                {
                    var absolute = Path.Combine(cacheRoot, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    var isStrm = item.File.IsVideo && config.VideoMode == VideoMode.Strm;

                    var upToDate = known.TryGetValue(item.RelativePath, out var previous)
                                   && File.Exists(absolute)
                                   && string.Equals(previous.Etag, item.File.Etag, StringComparison.Ordinal)
                                   && previous.PreviewSize == (isStrm ? -1 : previewSize);

                    if (!upToDate)
                    {
                        if (isStrm)
                        {
                            await WriteStrmAsync(config, absolute, item.File.FileId, token).ConfigureAwait(false);
                        }
                        else
                        {
                            var url = item.File.IsVideo || config.DownloadOriginals
                                ? MemoriesApiClient.GetOriginalUrl(item.File.FileId)
                                : MemoriesApiClient.GetPreviewUrl(item.File.FileId, config.PreviewSize);

                            var bytes = await _api.DownloadToFileAsync(url, absolute, token).ConfigureAwait(false);
                            lock (result)
                            {
                                result.BytesTransferred += bytes;
                            }
                        }

                        SetTimestamp(absolute, item.File.GetCaptureTimeUtc());
                        lock (result)
                        {
                            result.Downloaded++;
                        }
                    }
                    else
                    {
                        lock (result)
                        {
                            result.Unchanged++;
                        }
                    }

                    entries.Add(new IndexEntry
                    {
                        FileId = item.File.FileId,
                        RelativePath = item.RelativePath,
                        Etag = item.File.Etag,
                        IsVideo = item.File.IsVideo,
                        CaptureUnix = new DateTimeOffset(item.File.GetCaptureTimeUtc()).ToUnixTimeSeconds(),
                        PreviewSize = isStrm ? -1 : previewSize,
                        IsAlbumEntry = item.AlbumName is not null,
                        AlbumName = item.AlbumName
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Datei {FileId} konnte nicht gespiegelt werden.", item.File.FileId);
                    lock (result)
                    {
                        result.Failed++;
                    }
                }
                finally
                {
                    var current = Interlocked.Increment(ref processed);
                    progress?.Report(20 + (75d * current / total));
                }
            }).ConfigureAwait(false);

            foreach (var item in links)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var absolute = Path.Combine(cacheRoot, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    var target = Path.Combine(
                        cacheRoot,
                        item.LinkTargetRelativePath!.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(target))
                    {
                        continue;
                    }

                    if (!File.Exists(absolute))
                    {
                        CreateLink(config, absolute, target);
                        result.Linked++;
                    }

                    entries.Add(new IndexEntry
                    {
                        FileId = item.File.FileId,
                        RelativePath = item.RelativePath,
                        Etag = item.File.Etag,
                        IsVideo = item.File.IsVideo,
                        CaptureUnix = new DateTimeOffset(item.File.GetCaptureTimeUtc()).ToUnixTimeSeconds(),
                        PreviewSize = previewSize,
                        IsAlbumEntry = true,
                        AlbumName = item.AlbumName
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Album-Verknuepfung {Path} fehlgeschlagen.", item.RelativePath);
                    result.Failed++;
                }
                finally
                {
                    var current = Interlocked.Increment(ref processed);
                    progress?.Report(20 + (75d * current / total));
                }
            }

            return entries.ToList();
        }

        private void CreateLink(PluginConfiguration config, string linkPath, string targetPath)
        {
            var directory = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (config.LinkMode == LinkMode.Symlink)
            {
                try
                {
                    File.CreateSymbolicLink(linkPath, targetPath);
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Symlink nicht moeglich, kopiere stattdessen.");
                }
            }

            File.Copy(targetPath, linkPath, overwrite: true);
        }

        private async Task WriteStrmAsync(
            PluginConfiguration config,
            string absolutePath,
            long fileId,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var token = _tokens.CreateToken(fileId);
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/NextcloudMemories/Stream/{1}?token={2}",
                (config.JellyfinBaseUrl ?? string.Empty).TrimEnd('/'),
                fileId,
                token);

            await File.WriteAllTextAsync(absolutePath, url, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
        }

        private void SetTimestamp(string path, DateTime captureUtc)
        {
            try
            {
                if (captureUtc > new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                {
                    File.SetLastWriteTimeUtc(path, captureUtc);
                    File.SetCreationTimeUtc(path, captureUtc);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Zeitstempel fuer {Path} konnte nicht gesetzt werden.", path);
            }
        }

        private void PruneEmptyDirectories(string root)
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length)
                         .ToList())
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Leeres Verzeichnis {Path} konnte nicht entfernt werden.", directory);
                }
            }
        }

        private async Task EnsureLibraryAsync(PluginConfiguration config, string cacheRoot, SyncResult result)
        {
            try
            {
                var existing = _libraryManager.GetVirtualFolders();
                foreach (var folder in existing)
                {
                    if (folder.Locations is null)
                    {
                        continue;
                    }

                    foreach (var location in folder.Locations)
                    {
                        if (string.Equals(
                                Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar),
                                cacheRoot.TrimEnd(Path.DirectorySeparatorChar),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                }

                var options = new LibraryOptions
                {
                    EnablePhotos = true,
                    EnableRealtimeMonitor = false,
                    EnableChapterImageExtraction = false,
                    SaveLocalMetadata = false,
                    MetadataSavers = Array.Empty<string>(),
                    PathInfos = new[] { new MediaPathInfo(cacheRoot) }
                };

                _logger.LogInformation(
                    "Lege Bibliothek '{Name}' fuer {Path} an.",
                    config.LibraryName,
                    cacheRoot);

                await _libraryManager
                    .AddVirtualFolder(config.LibraryName, CollectionTypeOptions.homevideos, options, false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bibliothek konnte nicht automatisch angelegt werden.");
                result.Warnings.Add(
                    "Bibliothek konnte nicht automatisch angelegt werden, bitte manuell anlegen: " + ex.Message);
            }
        }

        private static string BuildTimelinePath(PluginConfiguration config, MemoriesFile file)
        {
            var captured = file.GetCaptureTimeUtc();
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1:yyyy}/{1:yyyy-MM}/{2}",
                TimelineFolder,
                captured,
                BuildFileName(config, file));
        }

        private static string BuildAlbumPath(PluginConfiguration config, string albumName, MemoriesFile file)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1}/{2}",
                AlbumFolder,
                albumName,
                BuildFileName(config, file));
        }

        private static string BuildFileName(PluginConfiguration config, MemoriesFile file)
        {
            var captured = file.GetCaptureTimeUtc();
            var stamp = captured.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            var originalExtension = file.GetOriginalExtension();

            string extension;
            if (file.IsVideo)
            {
                extension = config.VideoMode == VideoMode.Strm
                    ? ".strm"
                    : (originalExtension.Length > 0 ? originalExtension : ".mp4");
            }
            else
            {
                extension = config.DownloadOriginals && originalExtension.Length > 0
                    ? originalExtension
                    : ".jpg";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}_{1}{2}",
                stamp,
                file.FileId,
                extension);
        }

        private static string ToRelative(string root, string absolute)
        {
            return Path.GetRelativePath(root, absolute).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);
            var invalid = Path.GetInvalidFileNameChars();

            foreach (var character in value)
            {
                builder.Append(Array.IndexOf(invalid, character) >= 0 || character == '/' || character == '\\'
                    ? '_'
                    : character);
            }

            return builder.ToString().Trim().Trim('.');
        }

        private static DateTime? ParseDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : null;
        }

        private static bool InRange(DateTime value, DateTime? from, DateTime? to)
        {
            if (from.HasValue && value < from.Value.Date)
            {
                return false;
            }

            if (to.HasValue && value > to.Value.Date.AddDays(1))
            {
                return false;
            }

            return true;
        }
    }
}
