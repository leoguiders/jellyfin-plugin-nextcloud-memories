using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NextcloudMemories.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextcloudMemories.Api
{
    /// <summary>
    /// Response of the connection test.
    /// </summary>
    public class TestConnectionResponse
    {
        /// <summary>Gets or sets a value indicating whether the connection succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Gets or sets a human readable message.</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// One album as shown on the configuration page.
    /// </summary>
    public class AlbumSummary
    {
        /// <summary>Gets or sets the filter value used internally.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets the display name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the item count.</summary>
        public int Count { get; set; }
    }

    /// <summary>
    /// Current plugin status.
    /// </summary>
    public class StatusResponse
    {
        /// <summary>Gets or sets a value indicating whether a sync is running.</summary>
        public bool IsRunning { get; set; }

        /// <summary>Gets or sets the number of mirrored files.</summary>
        public int MirroredFiles { get; set; }

        /// <summary>Gets or sets the last sync timestamp.</summary>
        public string LastSyncUtc { get; set; } = string.Empty;

        /// <summary>Gets or sets the last sync result.</summary>
        public string LastSyncResult { get; set; } = string.Empty;

        /// <summary>Gets or sets the cache root.</summary>
        public string CacheRoot { get; set; } = string.Empty;
    }

    /// <summary>
    /// Administrative endpoints used by the plugin configuration page.
    /// </summary>
    [ApiController]
    [Route("NextcloudMemories")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public class MemoriesController : ControllerBase
    {
        private readonly MemoriesApiClient _api;
        private readonly SyncService _sync;
        private readonly LibraryIndex _index;
        private readonly ILogger<MemoriesController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoriesController"/> class.
        /// </summary>
        /// <param name="api">The API client.</param>
        /// <param name="sync">The sync service.</param>
        /// <param name="index">The library index.</param>
        /// <param name="logger">The logger.</param>
        public MemoriesController(
            MemoriesApiClient api,
            SyncService sync,
            LibraryIndex index,
            ILogger<MemoriesController> logger)
        {
            _api = api;
            _sync = sync;
            _index = index;
            _logger = logger;
        }

        /// <summary>
        /// Verifies the configured Nextcloud connection.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result.</returns>
        [HttpPost("TestConnection")]
        public async Task<ActionResult<TestConnectionResponse>> TestConnection(CancellationToken cancellationToken)
        {
            try
            {
                var message = await _api.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
                return new TestConnectionResponse { Success = true, Message = message };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connection test failed.");
                return new TestConnectionResponse { Success = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// Lists the albums available in Memories.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The albums.</returns>
        [HttpGet("Albums")]
        public async Task<ActionResult<IReadOnlyList<AlbumSummary>>> GetAlbums(CancellationToken cancellationToken)
        {
            var clusters = await _api.GetClustersAsync("albums", cancellationToken).ConfigureAwait(false);

            return clusters
                .Select(c => new AlbumSummary
                {
                    Id = c.GetFilterValue(),
                    Name = c.Name ?? c.GetFilterValue(),
                    Count = c.Count
                })
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Returns the raw GET /api/describe payload, for diagnosing API differences.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The payload.</returns>
        [HttpGet("Describe")]
        public async Task<ActionResult<JsonElement>> Describe(CancellationToken cancellationToken)
        {
            return await _api.DescribeAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts a sync in the background.
        /// </summary>
        /// <returns>Accepted, or conflict when a sync is already running.</returns>
        [HttpPost("Sync")]
        public ActionResult StartSync()
        {
            if (_sync.IsRunning)
            {
                return Conflict("A sync is already running.");
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await _sync.RunAsync(null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Manually started sync failed.");
                }
            });

            return Accepted();
        }

        /// <summary>
        /// Requests cancellation of a running sync. Already mirrored files are kept and recorded
        /// in the index, so the next run resumes instead of starting over.
        /// </summary>
        /// <returns>Accepted, or NotFound when no sync is running.</returns>
        [HttpPost("Stop")]
        public ActionResult StopSync()
        {
            if (!_sync.RequestStop())
            {
                return NotFound("No sync is currently running.");
            }

            return Accepted();
        }

        /// <summary>
        /// Returns the current status.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The status.</returns>
        [HttpGet("Status")]
        public async Task<ActionResult<StatusResponse>> GetStatus(CancellationToken cancellationToken)
        {
            await _index.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var config = Plugin.Instance?.Configuration;

            return new StatusResponse
            {
                IsRunning = _sync.IsRunning,
                MirroredFiles = _index.Count,
                LastSyncUtc = config?.LastSyncUtc ?? string.Empty,
                LastSyncResult = config?.LastSyncResult ?? string.Empty,
                CacheRoot = config?.CacheRoot ?? string.Empty
            };
        }
    }
}
