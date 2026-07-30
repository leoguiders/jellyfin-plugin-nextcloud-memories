using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NextcloudMemories.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextcloudMemories.Streaming
{
    /// <summary>
    /// Streams original Memories files through Jellyfin so that generated .strm files
    /// never have to contain Nextcloud credentials.
    /// </summary>
    [ApiController]
    [Route("NextcloudMemories")]
    public class MemoriesStreamController : ControllerBase
    {
        private static readonly string[] _forwardedResponseHeaders =
        {
            "Content-Type",
            "Content-Range",
            "Accept-Ranges",
            "Last-Modified",
            "ETag"
        };

        private readonly MemoriesApiClient _api;
        private readonly StreamTokenService _tokens;
        private readonly ILogger<MemoriesStreamController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoriesStreamController"/> class.
        /// </summary>
        /// <param name="api">The Memories API client.</param>
        /// <param name="tokens">The token service.</param>
        /// <param name="logger">The logger.</param>
        public MemoriesStreamController(
            MemoriesApiClient api,
            StreamTokenService tokens,
            ILogger<MemoriesStreamController> logger)
        {
            _api = api;
            _tokens = tokens;
            _logger = logger;
        }

        /// <summary>
        /// Proxies a single Nextcloud file, passing HTTP range requests through in both directions.
        /// </summary>
        /// <param name="fileId">The Nextcloud file id.</param>
        /// <param name="token">The HMAC token generated during sync.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The proxied response.</returns>
        [HttpGet("Stream/{fileId}")]
        [HttpHead("Stream/{fileId}")]
        [AllowAnonymous]
        public async Task<ActionResult> GetStream(
            [FromRoute] long fileId,
            [FromQuery] string? token,
            CancellationToken cancellationToken)
        {
            if (!_tokens.Validate(fileId, token))
            {
                _logger.LogWarning("Ungueltiges Stream-Token fuer Datei {FileId}.", fileId);
                return Unauthorized();
            }

            var method = HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get;
            using var upstreamRequest = MemoriesApiClient.CreateRequest(
                method,
                MemoriesApiClient.GetOriginalUrl(fileId));

            if (Request.Headers.TryGetValue("Range", out var range) && !string.IsNullOrEmpty(range))
            {
                upstreamRequest.Headers.TryAddWithoutValidation("Range", range.ToString());
            }

            using var upstream = await _api.SendRawAsync(upstreamRequest, cancellationToken).ConfigureAwait(false);

            Response.StatusCode = (int)upstream.StatusCode;

            foreach (var header in _forwardedResponseHeaders)
            {
                if (upstream.Content.Headers.TryGetValues(header, out var contentValues))
                {
                    Response.Headers[header] = string.Join(", ", contentValues);
                }
                else if (upstream.Headers.TryGetValues(header, out var values))
                {
                    Response.Headers[header] = string.Join(", ", values);
                }
            }

            if (upstream.Content.Headers.ContentLength.HasValue)
            {
                Response.ContentLength = upstream.Content.Headers.ContentLength.Value;
            }

            if (!Response.Headers.ContainsKey("Accept-Ranges"))
            {
                Response.Headers["Accept-Ranges"] = "bytes";
            }

            if (method == HttpMethod.Head)
            {
                return new EmptyResult();
            }

            try
            {
                await using var source = await upstream.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await source.CopyToAsync(Response.Body, 81920, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected mid-stream. Nothing to do.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stream fuer Datei {FileId} abgebrochen.", fileId);
            }

            return new EmptyResult();
        }
    }
}
