# Jellyfin Plugin: Nextcloud Memories

Mirrors photos, albums and videos from the Nextcloud app [Memories](https://memories.gallery/) into a
Jellyfin library. Jellyfin and Nextcloud do **not** need to share a filesystem — the plugin talks to
Nextcloud over HTTP only.

> **Status:** v1.1.1, tested against Jellyfin 10.11.11. Take a backup of your Jellyfin configuration
> before the first sync.

## Changelog

**1.1.1.0**

- Code, configuration page and documentation are now in English. Mirrored folders are named
  `Timeline` and `Albums` — see the upgrade note below.
- GitHub Actions workflow declares `contents: write` so tagged builds can publish a release.

**1.1.0.0**

- The index is checkpointed every 200 files during a sync. Cancelling — or restarting the server —
  no longer discards the work done so far; the next run resumes.
- A running sync can be cancelled: new button on the configuration page and
  `POST /NextcloudMemories/Stop`.

**1.0.0.0** — Initial release.

> **Upgrading from 1.1.0.0 or earlier:** the mirror directories were renamed from `Zeitachse`/`Alben`
> to `Timeline`/`Albums`. The next sync treats the old folders as orphans, deletes them and
> re-downloads everything. If you want to avoid the re-download, rename the two directories inside
> your cache root before starting the sync.

---

## How it works

Jellyfin's channels API cannot represent photos — `ChannelMediaType.Photo` is never resolved into a
`Photo` item, so every photo would end up as a `Video` in the database. This plugin takes a different
route:

1. A **scheduled task** enumerates the timeline and the albums through the Memories API.
2. For each photo the **JPEG preview rendered by Memories** is downloaded into a local cache
   directory. The file name carries the capture date and the Nextcloud `fileid`, and the file `mtime`
   is set to the capture date.
3. Videos are written as **`.strm` files** pointing at a signed streaming proxy inside the plugin, so
   Nextcloud credentials never end up in the Jellyfin database.
4. The cache directory is registered as a regular **Home Videos & Photos** library. Thumbnails,
   slideshow, favourites and every client work without special cases.
5. A metadata provider fills in the capture date and — optionally — tags, people and places.

```
<cache directory>/
├── Timeline/
│   └── 2024/
│       └── 2024-06/
│           ├── 2024-06-01_143022_1048576.jpg
│           └── 2024-06-03_090511_1048701.strm
└── Albums/
    └── Summer 2024/
        └── 2024-06-01_143022_1048576.jpg   ← symlink to the timeline file
```

---

## Requirements

| | |
|---|---|
| Jellyfin | 10.11.0 or newer (tested with 10.11.11) |
| Nextcloud | with the Memories app installed and indexed |
| Disk space | depends on Nextcloud's `preview_max_x`: roughly 75 KB per photo at 1024 px (100,000 photos ≈ 7.5 GB), about four times that at 2048 px |
| Network | Jellyfin must be able to reach the Nextcloud URL over HTTP(S) |

---

## Installation

### Option A — release ZIP (recommended)

1. Download the latest `nextcloud-memories.zip` from the
   [releases page](https://github.com/leoguiders/jellyfin-plugin-nextcloud-memories/releases).
2. Stop Jellyfin.
3. Unpack the ZIP into `<jellyfin-config>/plugins/Nextcloud Memories_1.1.1.0/`. Typical paths:

   | Setup | Path |
   |---|---|
   | Linux (package) | `/var/lib/jellyfin/plugins/Nextcloud Memories_1.1.1.0/` |
   | Docker (linuxserver) | `/config/plugins/Nextcloud Memories_1.1.1.0/` inside the container |
   | Docker (official) | `/config/plugins/Nextcloud Memories_1.1.1.0/` inside the container |
   | Windows | `%ProgramData%\Jellyfin\Server\plugins\Nextcloud Memories_1.1.1.0\` |

   The folder must contain `Jellyfin.Plugin.NextcloudMemories.dll` **and** `meta.json`. Copy only
   those two files — the other DLLs in `publish/` ship with Jellyfin itself, and duplicates cause
   load errors. Remove any older version folder of this plugin.

4. Make sure the folder belongs to the Jellyfin user:

   ```bash
   chown -R jellyfin:jellyfin "/var/lib/jellyfin/plugins/Nextcloud Memories_1.1.1.0"
   ```

5. Start Jellyfin. "Nextcloud Memories" should appear under **Dashboard → Plugins**.

### Option B — build from source

```bash
git clone https://github.com/leoguiders/jellyfin-plugin-nextcloud-memories.git
cd jellyfin-plugin-nextcloud-memories
dotnet publish Jellyfin.Plugin.NextcloudMemories/Jellyfin.Plugin.NextcloudMemories.csproj \
  -c Release -o publish
```

Then copy `publish/Jellyfin.Plugin.NextcloudMemories.dll` and `meta.json` into the plugin folder
above and restart Jellyfin. Requires the **.NET 9 SDK**.

---

## Preparing Nextcloud

Create an **app password** instead of using your login password:

**Nextcloud → Settings → Security → Devices & sessions → "Create new app password"**

App passwords work with two-factor authentication enabled and can be revoked individually without
locking out your other devices.

---

## Configuration

**Dashboard → Plugins → Nextcloud Memories**

### Connection

| Field | Description |
|---|---|
| Nextcloud URL | e.g. `https://cloud.example.com` — without `/apps/memories` |
| User name | Nextcloud user name |
| App password | see above |

Save, then click **Test connection**. The message reports the number of days and files found.

### Library

| Field | Default | Description |
|---|---|---|
| Cache directory | `<plugin-data>/library` | Must be writable by the Jellyfin process |
| Library name | `Nextcloud Fotos` | |
| Create library automatically | on | Creates a Home Videos & Photos library on the first sync |
| Scan after sync | on | Queues a library scan after every sync |

### Content

| Field | Default | Description |
|---|---|---|
| Mirror timeline | on | Year/month folder tree |
| Mirror albums | on | One folder per Memories album |
| Only from / until date | empty | Limits the range, e.g. `2020-01-01` |
| Album selection | all | Click "Load albums", then pick individually |

### Files

| Field | Default | Description |
|---|---|---|
| Preview size | 2048 | Match this to Nextcloud's `preview_max_x` |
| Download originals | off | Expert option, see "Known limitations" |
| Videos | `.strm` | or download originals / ignore |
| Album entries | symlink | or copy, when the filesystem cannot do symlinks |
| Base URL of this server | `http://127.0.0.1:8096` | Written into `.strm` files |

### Advanced

| Field | Default | Description |
|---|---|---|
| Parallel downloads | 4 | |
| Sync interval | 12 h | 0 disables the default trigger |
| HTTP timeout | 120 s | |
| Album filter parameter | `albums` | see Troubleshooting |
| Detailed metadata | off | Tags, people, places — one extra request per photo |

Then click **Sync now**. Progress is visible under
**Dashboard → Scheduled Tasks → "Sync Nextcloud Memories"**.

---

## Docker

The cache directory has to be a persistent volume, otherwise everything is gone after a container
restart:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:10.11.11
    volumes:
      - ./config:/config
      - ./cache:/cache
      - ./memories-mirror:/memories   # <- use this as the cache directory
```

Enter `/memories` — the path **inside the container** — as the cache directory. Create the host
directory beforehand and give it to the Jellyfin user, otherwise Docker creates it as `root:root`
and the sync fails.

If Nextcloud runs on the same Docker network, the internal service name works as the Nextcloud URL
(e.g. `http://nextcloud`), which avoids the detour through the reverse proxy.

---

## Plugin endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/NextcloudMemories/TestConnection` | Connection test |
| `GET` | `/NextcloudMemories/Albums` | Album list |
| `GET` | `/NextcloudMemories/Describe` | Raw response of `GET /apps/memories/api/describe` |
| `POST` | `/NextcloudMemories/Sync` | Start a sync |
| `POST` | `/NextcloudMemories/Stop` | Cancel a running sync |
| `GET` | `/NextcloudMemories/Status` | Status |
| `GET` | `/NextcloudMemories/Stream/{fileId}?token=…` | Video proxy (anonymous, HMAC signed) |

All except `Stream` require administrator rights.

---

## Troubleshooting

**Albums stay empty or contain the whole library**

The query parameter that filters the timeline by album is undocumented in Memories and has changed
between versions. The plugin detects the failure — an album returning far more files than its item
count — skips it and logs a warning. To fix it:

1. Call `GET /NextcloudMemories/Describe` (or `https://<nextcloud>/apps/memories/api/describe`).
2. Find the correct parameter name.
3. Enter it under **Advanced → Album filter parameter**.

**Plugin settings will not save**

A known Jellyfin 10.11 issue behind reverse proxies with a base URL set. Open the dashboard through
the internal address (`http://<server>:8096`) or temporarily clear the base URL.

**Syncing is painfully slow (over 1 s per photo)**

Nextcloud is re-rendering every preview instead of serving it from cache. The most common cause is a
preview size that does not match Nextcloud's cap. Check

```bash
docker exec -u www-data nextcloud php occ config:system:get preview_max_x
```

and set the plugin's preview size to **exactly that value**. Larger values still return the capped
resolution but miss the cache. To see what actually arrives:

```bash
file /path/to/cache/Timeline/*/*/*.jpg | head -3
```

The Nextcloud *Preview Generator* app is worth installing on top — it renders all previews once in a
batch, after which the endpoint answers in milliseconds.

**Photos without thumbnails**

Usually HEIC or RAW with "Download originals" enabled. Turn the option off so Memories delivers
ready-made JPEGs that Jellyfin can always render.

**Seeking in videos is unreliable**

`.strm` playback with range requests is incompletely implemented in Jellyfin
([jellyfin#13974](https://github.com/jellyfin/jellyfin/issues/13974)). The proxy passes `Range`
through correctly, but behaviour depends on the client. The reliable option is
**Videos → download the originals**.

**Jellyfin will not start**

Since 10.11 Jellyfin refuses to start with less than 2 GB free in its data directory. Move the cache
to another drive or reduce the preview size / date range.

**Scanning takes forever**

Jellyfin scales poorly with six-figure photo counts. Limit the date range, mirror selected albums
only, or disable the timeline.

**Logs**

`Dashboard → Logs`; all plugin messages carry the context `Jellyfin.Plugin.NextcloudMemories`. To
silence the very chatty HTTP client logging, add this to `logging.json` under
`Serilog.MinimumLevel.Override`:

```json
"System.Net.Http.HttpClient": "Warning"
```

---

## Known limitations

- **Duplicate items.** A photo present in both the timeline and an album produces two `Photo` items
  in Jellyfin. That is unavoidable for a file-based library; disable one of the two branches if it
  bothers you.
- **One Nextcloud account.** The plugin runs as a single Nextcloud user. Every Jellyfin user with
  access to the library sees that user's entire collection.
- **Previews carry no EXIF.** The capture date is therefore set through both `mtime` and the metadata
  provider. Camera details are lost unless "Download originals" is enabled.
- **Deletions** in Nextcloud only surface on the next sync.
- **Faces and places** become tags on the item, not a separate navigation. Jellyfin has no
  photo-specific people or map view.
- **The Memories API is unversioned.** An update of the Nextcloud app can change field names.

---

## Alternative without a plugin

If you can set up a WebDAV mount on the Jellyfin host (`rclone mount` or `davfs2` against
`/remote.php/dav/files/<user>/Photos`), you do not need this plugin: Jellyfin reads the files
directly, without a cache and without duplicated storage. The cost is an extra service outside
Jellyfin and noticeably slower scans.

---

## Development

```bash
dotnet build Jellyfin.Plugin.NextcloudMemories/Jellyfin.Plugin.NextcloudMemories.csproj -c Debug
```

| File | Contents |
|---|---|
| `Plugin.cs` | Plugin entry point, configuration page |
| `PluginServiceRegistrator.cs` | DI registration |
| `Api/MemoriesApiClient.cs` | HTTP client for the Memories API |
| `Api/MemoriesController.cs` | Admin endpoints used by the configuration page |
| `Sync/SyncService.cs` | Diff, download, linking, cleanup |
| `Sync/LibraryIndex.cs` | Persistent state (`fileid` ↔ path ↔ etag) |
| `Streaming/` | Signed video proxy |
| `Providers/` | Metadata enrichment |
| `Tasks/MemoriesSyncTask.cs` | Scheduled task |

Pull requests welcome. Please do not add direct database access — raw SQL is rejected by Jellyfin
from 10.11 on, and the plugin database API is experimental until 10.12.

---

## License

GPL-3.0-only, matching Jellyfin. Nextcloud Memories is AGPL-3.0 and is only ever contacted over HTTP.
