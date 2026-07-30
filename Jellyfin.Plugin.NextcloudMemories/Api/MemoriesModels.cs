using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.NextcloudMemories.Api
{
    /// <summary>
    /// One day bucket of the Memories timeline.
    /// </summary>
    public class MemoriesDay
    {
        /// <summary>Gets or sets the day id (days since the unix epoch).</summary>
        [JsonPropertyName("dayid")]
        public long DayId { get; set; }

        /// <summary>Gets or sets the number of files in that day.</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; }

        /// <summary>Gets the UTC date this day bucket represents.</summary>
        /// <returns>The date.</returns>
        public DateTime ToDate() => DateTime.UnixEpoch.AddDays(DayId);
    }

    /// <summary>
    /// A single photo or video returned by the Memories timeline endpoints.
    /// </summary>
    public class MemoriesFile
    {
        /// <summary>Gets or sets the Nextcloud file id.</summary>
        [JsonPropertyName("fileid")]
        public long FileId { get; set; }

        /// <summary>Gets or sets the etag; changes whenever the file changes.</summary>
        [JsonPropertyName("etag")]
        public string? Etag { get; set; }

        /// <summary>Gets or sets the base file name including extension.</summary>
        [JsonPropertyName("basename")]
        public string? BaseName { get; set; }

        /// <summary>Gets or sets the file name as reported by some Memories versions.</summary>
        [JsonPropertyName("filename")]
        public string? FileName { get; set; }

        /// <summary>Gets or sets the mime type, when reported.</summary>
        [JsonPropertyName("mimetype")]
        public string? MimeType { get; set; }

        /// <summary>Gets or sets the day id this file belongs to.</summary>
        [JsonPropertyName("dayid")]
        public long DayId { get; set; }

        /// <summary>Gets or sets the capture timestamp as unix seconds, when reported.</summary>
        [JsonPropertyName("datetaken")]
        public long? DateTaken { get; set; }

        /// <summary>Gets or sets an alternative capture timestamp field used by some versions.</summary>
        [JsonPropertyName("epoch")]
        public long? Epoch { get; set; }

        /// <summary>Gets or sets a flag indicating whether this entry is a video.</summary>
        [JsonPropertyName("isvideo")]
        [JsonConverter(typeof(FlexibleBooleanConverter))]
        public bool IsVideo { get; set; }

        /// <summary>Gets or sets the video duration in seconds.</summary>
        [JsonPropertyName("video_duration")]
        public double? VideoDuration { get; set; }

        /// <summary>Gets or sets the image width.</summary>
        [JsonPropertyName("w")]
        public int? Width { get; set; }

        /// <summary>Gets or sets the image height.</summary>
        [JsonPropertyName("h")]
        public int? Height { get; set; }

        /// <summary>
        /// Gets the best available capture timestamp, falling back to the day bucket.
        /// </summary>
        /// <returns>The capture time in UTC.</returns>
        public DateTime GetCaptureTimeUtc()
        {
            var epoch = DateTaken ?? Epoch;
            if (epoch.HasValue && epoch.Value > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch.Value).UtcDateTime;
            }

            return DateTime.UnixEpoch.AddDays(DayId);
        }

        /// <summary>
        /// Gets the original file extension, including the leading dot, or an empty string.
        /// </summary>
        /// <returns>The extension.</returns>
        public string GetOriginalExtension()
        {
            var name = BaseName ?? FileName;
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var dot = name.LastIndexOf('.');
            return dot < 0 ? string.Empty : name.Substring(dot);
        }
    }

    /// <summary>
    /// A Memories cluster (album, person, place or tag).
    /// </summary>
    public class MemoriesCluster
    {
        /// <summary>Gets or sets the cluster id.</summary>
        [JsonPropertyName("cluster_id")]
        public string? ClusterId { get; set; }

        /// <summary>Gets or sets the display name.</summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>Gets or sets the owning user, for albums.</summary>
        [JsonPropertyName("user")]
        public string? User { get; set; }

        /// <summary>Gets or sets the number of items in the cluster.</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; }

        /// <summary>
        /// Gets the identifier used when filtering timeline endpoints by this cluster.
        /// </summary>
        /// <returns>The filter value.</returns>
        public string GetFilterValue()
        {
            if (!string.IsNullOrWhiteSpace(ClusterId))
            {
                return ClusterId!;
            }

            if (!string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Name))
            {
                return User + "/" + Name;
            }

            return Name ?? string.Empty;
        }
    }

    /// <summary>
    /// Subset of GET /api/image/info/{id} that the plugin uses.
    /// </summary>
    public class MemoriesImageInfo
    {
        /// <summary>Gets or sets the file id.</summary>
        [JsonPropertyName("fileid")]
        public long FileId { get; set; }

        /// <summary>Gets or sets the base name.</summary>
        [JsonPropertyName("basename")]
        public string? BaseName { get; set; }

        /// <summary>Gets or sets the capture timestamp in unix seconds.</summary>
        [JsonPropertyName("datetaken")]
        public long? DateTaken { get; set; }

        /// <summary>Gets or sets the raw EXIF payload.</summary>
        [JsonPropertyName("exif")]
        public JsonElement? Exif { get; set; }

        /// <summary>Gets or sets the assigned tags.</summary>
        [JsonPropertyName("tags")]
        public JsonElement? Tags { get; set; }

        /// <summary>Gets or sets the recognized people.</summary>
        [JsonPropertyName("people")]
        public JsonElement? People { get; set; }

        /// <summary>Gets or sets the resolved address.</summary>
        [JsonPropertyName("address")]
        public string? Address { get; set; }
    }

    /// <summary>
    /// Accepts booleans that are serialized as <c>true</c>, <c>1</c> or <c>"1"</c>.
    /// </summary>
    public sealed class FlexibleBooleanConverter : JsonConverter<bool>
    {
        /// <inheritdoc />
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    return false;
                case JsonTokenType.Number:
                    return reader.TryGetInt64(out var number) && number != 0;
                case JsonTokenType.String:
                    var text = reader.GetString();
                    return !string.IsNullOrEmpty(text)
                           && !string.Equals(text, "0", StringComparison.Ordinal)
                           && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WriteBooleanValue(value);
        }
    }
}
