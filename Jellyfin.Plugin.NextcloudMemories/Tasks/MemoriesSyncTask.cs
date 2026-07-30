using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NextcloudMemories.Sync;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NextcloudMemories.Tasks
{
    /// <summary>
    /// Scheduled task that mirrors Nextcloud Memories into the local cache.
    /// </summary>
    public class MemoriesSyncTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly SyncService _sync;
        private readonly ILogger<MemoriesSyncTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoriesSyncTask"/> class.
        /// </summary>
        /// <param name="sync">The sync service.</param>
        /// <param name="logger">The logger.</param>
        public MemoriesSyncTask(SyncService sync, ILogger<MemoriesSyncTask> logger)
        {
            _sync = sync;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "Sync Nextcloud Memories";

        /// <inheritdoc />
        public string Key => "NextcloudMemoriesSync";

        /// <inheritdoc />
        public string Description =>
            "Fetches new and changed photos, albums and videos from Nextcloud Memories into the local cache directory.";

        /// <inheritdoc />
        public string Category => "Nextcloud Memories";

        /// <inheritdoc />
        public bool IsHidden => false;

        /// <inheritdoc />
        public bool IsEnabled => true;

        /// <inheritdoc />
        public bool IsLogged => true;

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config is null
                || string.IsNullOrWhiteSpace(config.ServerUrl)
                || string.IsNullOrWhiteSpace(config.Username)
                || string.IsNullOrWhiteSpace(config.AppPassword))
            {
                _logger.LogInformation("Nextcloud Memories is not configured, skipping the sync.");
                progress.Report(100);
                return;
            }

            await _sync.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var hours = Plugin.Instance?.Configuration.SyncIntervalHours ?? 12;
            if (hours <= 0)
            {
                yield break;
            }

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(hours).Ticks
            };
        }
    }
}
