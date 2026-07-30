using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NextcloudMemories.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextcloudMemories.Api
{
    /// <summary>
    /// Thin HTTP client for the Nextcloud Memories API.
    /// </summary>
    public class MemoriesApiClient
    {
        private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
        };

        private readonly HttpClient _httpClient;
        private readonly ILogger<MemoriesApiClient> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoriesApiClient"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="logger">The logger.</param>
        public MemoriesApiClient(HttpClient httpClient, ILogger<MemoriesApiClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _httpClient.Timeout = Timeout.InfiniteTimeSpan; // per-request timeouts are handled below
        }

        private static PluginConfiguration Config =>
            Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin is not initialised.");

        /// <summary>
        /// Builds an absolute URL below /apps/memories.
        /// </summary>
        /// <param name="path">Path starting with a slash, e.g. <c>/api/days</c>.</param>
        /// <param name="query">Optional query parameters.</param>
        /// <returns>The absolute URL.</returns>
        public static string BuildUrl(string path, IReadOnlyDictionary<string, string>? query = null)
        {
            var baseUrl = (Config.ServerUrl ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
            {
                throw new InvalidOperationException("Nextcloud-URL ist nicht konfiguriert.");
            }

            var builder = new StringBuilder(baseUrl).Append("/apps/memories").Append(path);

            if (query is { Count: > 0 })
            {
                var first = true;
                foreach (var pair in query)
                {
                    if (string.IsNullOrEmpty(pair.Value))
                    {
                        continue;
                    }

                    builder.Append(first ? '?' : '&');
                    builder.Append(Uri.EscapeDataString(pair.Key))
                        .Append('=')
                        .Append(Uri.EscapeDataString(pair.Value));
                    first = false;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Creates a request with Nextcloud authentication headers applied.
        /// </summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="url">Absolute URL.</param>
        /// <returns>The request.</returns>
        public static HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            var config = Config;

            var raw = config.Username + ":" + config.AppPassword;
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
            request.Headers.TryAddWithoutValidation("OCS-APIRequest", "true");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("Jellyfin-NextcloudMemories/1.0");

            return request;
        }

        private CancellationTokenSource CreateTimeoutSource(CancellationToken cancellationToken)
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var seconds = Math.Clamp(Config.RequestTimeoutSeconds, 5, 3600);
            linked.CancelAfter(TimeSpan.FromSeconds(seconds));
            return linked;
        }

        private async Task<T?> GetJsonAsync<T>(
            string path,
            IReadOnlyDictionary<string, string>? query,
            CancellationToken cancellationToken)
        {
            var url = BuildUrl(path, query);
            using var cts = CreateTimeoutSource(cancellationToken);
            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, cts.Token).ConfigureAwait(false);
                throw new MemoriesApiException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} lieferte HTTP {1}. {2}",
                        url,
                        (int)response.StatusCode,
                        Truncate(body, 300)),
                    response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cts.Token).ConfigureAwait(false);
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string Truncate(string value, int max) =>
            string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max) + "...";

        /// <summary>
        /// Verifies that the configured credentials reach a working Memories installation.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A short human readable status message.</returns>
        public async Task<string> TestConnectionAsync(CancellationToken cancellationToken)
        {
            var days = await GetDaysAsync(cancellationToken).ConfigureAwait(false);

            long total = 0;
            foreach (var day in days)
            {
                total += day.Count;
            }

            var describeAvailable = true;
            try
            {
                await GetJsonAsync<JsonElement>("/api/describe", null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                describeAvailable = false;
                _logger.LogDebug(ex, "GET /api/describe ist auf dieser Memories-Version nicht verfuegbar.");
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "Verbindung ok. {0} Tage, {1} Dateien. /api/describe: {2}.",
                days.Count,
                total,
                describeAvailable ? "verfuegbar" : "nicht verfuegbar");
        }

        /// <summary>
        /// Gets the raw response of GET /api/describe, for diagnosing parameter names.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The JSON document.</returns>
        public async Task<JsonElement> DescribeAsync(CancellationToken cancellationToken)
        {
            return await GetJsonAsync<JsonElement>("/api/describe", null, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets all timeline day buckets.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The day list.</returns>
        public async Task<IReadOnlyList<MemoriesDay>> GetDaysAsync(CancellationToken cancellationToken)
        {
            var result = await GetJsonAsync<List<MemoriesDay>>("/api/days", null, cancellationToken)
                .ConfigureAwait(false);
            return result ?? new List<MemoriesDay>();
        }

        /// <summary>
        /// Gets all files of a single day.
        /// </summary>
        /// <param name="dayId">The day id.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The file list.</returns>
        public async Task<IReadOnlyList<MemoriesFile>> GetDayAsync(long dayId, CancellationToken cancellationToken)
        {
            var path = "/api/days/" + dayId.ToString(CultureInfo.InvariantCulture);
            var result = await GetJsonAsync<List<MemoriesFile>>(path, null, cancellationToken).ConfigureAwait(false);
            return result ?? new List<MemoriesFile>();
        }

        /// <summary>
        /// Gets the clusters of a backend, e.g. <c>albums</c>, <c>recognize</c>, <c>places</c> or <c>tags</c>.
        /// </summary>
        /// <param name="backend">The cluster backend.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The cluster list.</returns>
        public async Task<IReadOnlyList<MemoriesCluster>> GetClustersAsync(
            string backend,
            CancellationToken cancellationToken)
        {
            var result = await GetJsonAsync<List<MemoriesCluster>>("/api/clusters/" + backend, null, cancellationToken)
                .ConfigureAwait(false);
            return result ?? new List<MemoriesCluster>();
        }

        /// <summary>
        /// Gets the day buckets that belong to a single album.
        /// </summary>
        /// <param name="albumFilter">The album filter value.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The day list.</returns>
        public async Task<IReadOnlyList<MemoriesDay>> GetAlbumDaysAsync(
            string albumFilter,
            CancellationToken cancellationToken)
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Config.AlbumQueryParameter] = albumFilter
            };

            var result = await GetJsonAsync<List<MemoriesDay>>("/api/days", query, cancellationToken)
                .ConfigureAwait(false);
            return result ?? new List<MemoriesDay>();
        }

        /// <summary>
        /// Gets the files of one day inside a single album.
        /// </summary>
        /// <param name="albumFilter">The album filter value.</param>
        /// <param name="dayId">The day id.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The file list.</returns>
        public async Task<IReadOnlyList<MemoriesFile>> GetAlbumDayAsync(
            string albumFilter,
            long dayId,
            CancellationToken cancellationToken)
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Config.AlbumQueryParameter] = albumFilter
            };

            var path = "/api/days/" + dayId.ToString(CultureInfo.InvariantCulture);
            var result = await GetJsonAsync<List<MemoriesFile>>(path, query, cancellationToken).ConfigureAwait(false);
            return result ?? new List<MemoriesFile>();
        }

        /// <summary>
        /// Gets EXIF and tag information for a single file.
        /// </summary>
        /// <param name="fileId">The file id.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The info object, or <c>null</c> when unavailable.</returns>
        public async Task<MemoriesImageInfo?> GetImageInfoAsync(long fileId, CancellationToken cancellationToken)
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["basic"] = "0",
                ["tags"] = "1"
            };

            var path = "/api/image/info/" + fileId.ToString(CultureInfo.InvariantCulture);
            return await GetJsonAsync<MemoriesImageInfo>(path, query, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the URL of the preview endpoint for a file.
        /// </summary>
        /// <param name="fileId">The file id.</param>
        /// <param name="size">Edge length in pixels.</param>
        /// <returns>The URL.</returns>
        public static string GetPreviewUrl(long fileId, int size)
        {
            var value = size.ToString(CultureInfo.InvariantCulture);
            return BuildUrl(
                "/api/image/preview/" + fileId.ToString(CultureInfo.InvariantCulture),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["x"] = value,
                    ["y"] = value,
                    ["a"] = "1"
                });
        }

        /// <summary>
        /// Gets the URL that streams the original file.
        /// </summary>
        /// <param name="fileId">The file id.</param>
        /// <returns>The URL.</returns>
        public static string GetOriginalUrl(long fileId) =>
            BuildUrl("/api/stream/" + fileId.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Downloads a URL into a file. Writes to a temporary file first and moves it into place afterwards.
        /// </summary>
        /// <param name="url">The source URL.</param>
        /// <param name="destinationPath">The destination path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of bytes written.</returns>
        public async Task<long> DownloadToFileAsync(
            string url,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = destinationPath + ".part";

            using var cts = CreateTimeoutSource(cancellationToken);
            using var request = CreateRequest(HttpMethod.Get, url);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new MemoriesApiException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Download von {0} fehlgeschlagen: HTTP {1}.",
                        url,
                        (int)response.StatusCode),
                    response.StatusCode);
            }

            long written;
            await using (var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            await using (var target = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                await source.CopyToAsync(target, 81920, cts.Token).ConfigureAwait(false);
                written = target.Length;
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            return written;
        }

        /// <summary>
        /// Sends a request to Nextcloud and returns the raw response, used by the streaming proxy.
        /// </summary>
        /// <param name="request">The prepared request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The response, which the caller has to dispose.</returns>
        public Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }

    /// <summary>
    /// Raised when the Memories API returns an unexpected response.
    /// </summary>
    public class MemoriesApiException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="MemoriesApiException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="statusCode">The HTTP status code.</param>
        public MemoriesApiException(string message, HttpStatusCode statusCode)
            : base(message)
        {
            StatusCode = statusCode;
        }

        /// <summary>Gets the HTTP status code.</summary>
        public HttpStatusCode StatusCode { get; }
    }
}
