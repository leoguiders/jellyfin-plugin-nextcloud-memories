using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NextcloudMemories.Api;
using Jellyfin.Plugin.NextcloudMemories.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextcloudMemories.Providers
{
    /// <summary>
    /// Shared logic for enriching mirrored items with Memories metadata.
    /// </summary>
    /// <typeparam name="TItem">The Jellyfin item type.</typeparam>
    public abstract class MemoriesMetadataProviderBase<TItem> : ICustomMetadataProvider<TItem>, IHasOrder
        where TItem : BaseItem
    {
        /// <summary>The provider id key written into <see cref="BaseItem.ProviderIds"/>.</summary>
        public const string ProviderKey = "NextcloudMemories";

        private readonly LibraryIndex _index;
        private readonly MemoriesApiClient _api;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoriesMetadataProviderBase{TItem}"/> class.
        /// </summary>
        /// <param name="index">The library index.</param>
        /// <param name="api">The API client.</param>
        /// <param name="logger">The logger.</param>
        protected MemoriesMetadataProviderBase(LibraryIndex index, MemoriesApiClient api, ILogger logger)
        {
            _index = index;
            _api = api;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Nextcloud Memories";

        /// <inheritdoc />
        public int Order => 100;

        /// <inheritdoc />
        public async Task<ItemUpdateType> FetchAsync(
            TItem item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null || string.IsNullOrWhiteSpace(config.CacheRoot) || string.IsNullOrEmpty(item.Path))
            {
                return ItemUpdateType.None;
            }

            if (!IsInsideCache(item.Path, config.CacheRoot))
            {
                return ItemUpdateType.None;
            }

            if (!TryParseFileId(item.Path, out var fileId))
            {
                return ItemUpdateType.None;
            }

            await _index.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var entry = _index.FindByFileId(fileId);

            var changed = false;

            if (!string.Equals(item.GetProviderId(ProviderKey), fileId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                item.SetProviderId(ProviderKey, fileId.ToString(CultureInfo.InvariantCulture));
                changed = true;
            }

            var captured = entry is not null && entry.CaptureUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(entry.CaptureUnix).UtcDateTime
                : (DateTime?)null;

            if (captured.HasValue && item.PremiereDate != captured)
            {
                item.PremiereDate = captured;
                item.ProductionYear = captured.Value.Year;
                changed = true;
            }

            if (config.FetchDetailedMetadata)
            {
                changed |= await ApplyDetailedMetadataAsync(item, fileId, cancellationToken).ConfigureAwait(false);
            }

            return changed ? ItemUpdateType.MetadataImport : ItemUpdateType.None;
        }

        private async Task<bool> ApplyDetailedMetadataAsync(
            TItem item,
            long fileId,
            CancellationToken cancellationToken)
        {
            try
            {
                var info = await _api.GetImageInfoAsync(fileId, cancellationToken).ConfigureAwait(false);
                if (info is null)
                {
                    return false;
                }

                var tags = new List<string>(item.Tags ?? Array.Empty<string>());
                var before = tags.Count;

                foreach (var tag in ExtractNames(info.Tags))
                {
                    if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(tag);
                    }
                }

                foreach (var person in ExtractNames(info.People))
                {
                    if (!tags.Contains(person, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(person);
                    }
                }

                if (!string.IsNullOrWhiteSpace(info.Address)
                    && !tags.Contains(info.Address!, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add(info.Address!);
                }

                if (tags.Count != before)
                {
                    item.Tags = tags.ToArray();
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Detailmetadaten fuer {FileId} konnten nicht geladen werden.", fileId);
            }

            return false;
        }

        private static IEnumerable<string> ExtractNames(JsonElement? element)
        {
            if (element is null || element.Value.ValueKind == JsonValueKind.Null)
            {
                yield break;
            }

            var value = element.Value;

            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var text = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            yield return text!;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(property.Name))
                    {
                        yield return property.Name;
                    }
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in value.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.String)
                    {
                        var text = child.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            yield return text!;
                        }
                    }
                    else if (child.ValueKind == JsonValueKind.Object
                             && child.TryGetProperty("name", out var nameElement)
                             && nameElement.ValueKind == JsonValueKind.String)
                    {
                        var text = nameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            yield return text!;
                        }
                    }
                }
            }
        }

        private static bool IsInsideCache(string path, string cacheRoot)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var root = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar);
                return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Extracts the Nextcloud file id from a mirrored file name (<c>yyyy-MM-dd_HHmmss_&lt;fileid&gt;.ext</c>).
        /// </summary>
        /// <param name="path">The file path.</param>
        /// <param name="fileId">The parsed file id.</param>
        /// <returns><c>true</c> when the name matched.</returns>
        public static bool TryParseFileId(string path, out long fileId)
        {
            fileId = 0;

            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            var separator = name.LastIndexOf('_');
            if (separator < 0 || separator == name.Length - 1)
            {
                return false;
            }

            return long.TryParse(
                name.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out fileId);
        }
    }

    /// <summary>
    /// Enriches mirrored photos.
    /// </summary>
    public class MemoriesPhotoMetadataProvider : MemoriesMetadataProviderBase<Photo>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MemoriesPhotoMetadataProvider"/> class.
        /// </summary>
        /// <param name="index">The library index.</param>
        /// <param name="api">The API client.</param>
        /// <param name="logger">The logger.</param>
        public MemoriesPhotoMetadataProvider(
            LibraryIndex index,
            MemoriesApiClient api,
            ILogger<MemoriesPhotoMetadataProvider> logger)
            : base(index, api, logger)
        {
        }
    }

    /// <summary>
    /// Enriches mirrored videos.
    /// </summary>
    public class MemoriesVideoMetadataProvider : MemoriesMetadataProviderBase<Video>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MemoriesVideoMetadataProvider"/> class.
        /// </summary>
        /// <param name="index">The library index.</param>
        /// <param name="api">The API client.</param>
        /// <param name="logger">The logger.</param>
        public MemoriesVideoMetadataProvider(
            LibraryIndex index,
            MemoriesApiClient api,
            ILogger<MemoriesVideoMetadataProvider> logger)
            : base(index, api, logger)
        {
        }
    }
}
