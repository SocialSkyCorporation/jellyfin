#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers
{
    /// <summary>
    /// Subtitle controller.
    /// </summary>
    public class SubtitleController : BaseJellyfinApiController
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ISubtitleManager _subtitleManager;
        private readonly ISubtitleEncoder _subtitleEncoder;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger<SubtitleController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="SubtitleController"/> class.
        /// </summary>
        /// <param name="libraryManager">Instance of <see cref="ILibraryManager"/> interface.</param>
        /// <param name="subtitleManager">Instance of <see cref="ISubtitleManager"/> interface.</param>
        /// <param name="subtitleEncoder">Instance of <see cref="ISubtitleEncoder"/> interface.</param>
        /// <param name="mediaSourceManager">Instance of <see cref="IMediaSourceManager"/> interface.</param>
        /// <param name="providerManager">Instance of <see cref="IProviderManager"/> interface.</param>
        /// <param name="fileSystem">Instance of <see cref="IFileSystem"/> interface.</param>
        /// <param name="authContext">Instance of <see cref="IAuthorizationContext"/> interface.</param>
        /// <param name="logger">Instance of <see cref="ILogger{SubtitleController}"/> interface.</param>
        public SubtitleController(
            ILibraryManager libraryManager,
            ISubtitleManager subtitleManager,
            ISubtitleEncoder subtitleEncoder,
            IMediaSourceManager mediaSourceManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            IAuthorizationContext authContext,
            ILogger<SubtitleController> logger)
        {
            _libraryManager = libraryManager;
            _subtitleManager = subtitleManager;
            _subtitleEncoder = subtitleEncoder;
            _mediaSourceManager = mediaSourceManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _authContext = authContext;
            _logger = logger;
        }

        /// <summary>
        /// Deletes an external subtitle file.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <param name="index">The index of the subtitle file.</param>
        /// <response code="204">Subtitle deleted.</response>
        /// <response code="404">Item not found.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpDelete("/Videos/{id}/Subtitles/{index}")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<Task> DeleteSubtitle(
            [FromRoute] Guid id,
            [FromRoute] int index)
        {
            var item = _libraryManager.GetItemById(id);

            if (item == null)
            {
                return NotFound();
            }

            _subtitleManager.DeleteSubtitles(item, index);
            return NoContent();
        }

        /// <summary>
        /// Search remote subtitles.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <param name="language">The language of the subtitles.</param>
        /// <param name="isPerfectMatch">Optional. Only show subtitles which are a perfect match.</param>
        /// <response code="200">Subtitles retrieved.</response>
        /// <returns>An array of <see cref="RemoteSubtitleInfo"/>.</returns>
        [HttpGet("/Items/{id}/RemoteSearch/Subtitles/{language}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<IEnumerable<RemoteSubtitleInfo>>> SearchRemoteSubtitles(
            [FromRoute] Guid id,
            [FromRoute] string language,
            [FromQuery] bool? isPerfectMatch)
        {
            var video = (Video)_libraryManager.GetItemById(id);

            return await _subtitleManager.SearchSubtitles(video, language, isPerfectMatch, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Downloads a remote subtitle.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <param name="subtitleId">The subtitle id.</param>
        /// <response code="204">Subtitle downloaded.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("/Items/{id}/RemoteSearch/Subtitles/{subtitleId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> DownloadRemoteSubtitles(
            [FromRoute] Guid id,
            [FromRoute] string subtitleId)
        {
            var video = (Video)_libraryManager.GetItemById(id);

            try
            {
                await _subtitleManager.DownloadSubtitles(video, subtitleId, CancellationToken.None)
                    .ConfigureAwait(false);

                _providerManager.QueueRefresh(video.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading subtitles");
            }

            return NoContent();
        }

        /// <summary>
        /// Gets the remote subtitles.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <response code="200">File returned.</response>
        /// <returns>A <see cref="FileStreamResult"/> with the subtitle file.</returns>
        [HttpGet("/Providers/Subtitles/Subtitles/{id}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [Produces(MediaTypeNames.Application.Octet)]
        public async Task<ActionResult> GetRemoteSubtitles([FromRoute] string id)
        {
            var result = await _subtitleManager.GetRemoteSubtitles(id, CancellationToken.None).ConfigureAwait(false);

            return File(result.Stream, MimeTypes.GetMimeType("file." + result.Format));
        }

        /// <summary>
        /// Gets subtitles in a specified format.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <param name="mediaSourceId">The media source id.</param>
        /// <param name="index">The subtitle stream index.</param>
        /// <param name="format">The format of the returned subtitle.</param>
        /// <param name="startPositionTicks">Optional. The start position of the subtitle in ticks.</param>
        /// <param name="endPositionTicks">Optional. The end position of the subtitle in ticks.</param>
        /// <param name="copyTimestamps">Optional. Whether to copy the timestamps.</param>
        /// <param name="addVttTimeMap">Optional. Whether to add a VTT time map.</param>
        /// <response code="200">File returned.</response>
        /// <returns>A <see cref="FileContentResult"/> with the subtitle file.</returns>
        [HttpGet("/Videos/{id}/{mediaSourceId}/Subtitles/{index}/Stream.{format}")]
        [HttpGet("/Videos/{id}/{mediaSourceId}/Subtitles/{index}/{startPositionTicks}/Stream.{format}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult> GetSubtitle(
            [FromRoute, Required] Guid id,
            [FromRoute, Required] string mediaSourceId,
            [FromRoute, Required] int index,
            [FromRoute, Required] string format,
            [FromRoute] long startPositionTicks,
            [FromQuery] long? endPositionTicks,
            [FromQuery] bool copyTimestamps,
            [FromQuery] bool addVttTimeMap)
        {
            if (string.Equals(format, "js", StringComparison.OrdinalIgnoreCase))
            {
                format = "json";
            }

            if (string.IsNullOrEmpty(format))
            {
                var item = (Video)_libraryManager.GetItemById(id);

                var idString = id.ToString("N", CultureInfo.InvariantCulture);
                var mediaSource = _mediaSourceManager.GetStaticMediaSources(item, false)
                    .First(i => string.Equals(i.Id, mediaSourceId ?? idString, StringComparison.Ordinal));

                var subtitleStream = mediaSource.MediaStreams
                    .First(i => i.Type == MediaStreamType.Subtitle && i.Index == index);

                FileStream stream = new FileStream(subtitleStream.Path, FileMode.Open, FileAccess.Read);
                return File(stream, MimeTypes.GetMimeType(subtitleStream.Path));
            }

            if (string.Equals(format, "vtt", StringComparison.OrdinalIgnoreCase) && addVttTimeMap)
            {
                await using Stream stream = await EncodeSubtitles(id, mediaSourceId, index, format, startPositionTicks, endPositionTicks, copyTimestamps).ConfigureAwait(false);
                using var reader = new StreamReader(stream);

                var text = await reader.ReadToEndAsync().ConfigureAwait(false);

                text = text.Replace("WEBVTT", "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000", StringComparison.Ordinal);

                return File(Encoding.UTF8.GetBytes(text), MimeTypes.GetMimeType("file." + format));
            }

            return File(
                await EncodeSubtitles(
                    id,
                    mediaSourceId,
                    index,
                    format,
                    startPositionTicks,
                    endPositionTicks,
                    copyTimestamps).ConfigureAwait(false),
                MimeTypes.GetMimeType("file." + format));
        }

        /// <summary>
        /// Gets an HLS subtitle playlist.
        /// </summary>
        /// <param name="id">The item id.</param>
        /// <param name="index">The subtitle stream index.</param>
        /// <param name="mediaSourceId">The media source id.</param>
        /// <param name="segmentLength">The subtitle segment length.</param>
        /// <response code="200">Subtitle playlist retrieved.</response>
        /// <returns>A <see cref="FileContentResult"/> with the HLS subtitle playlist.</returns>
        [HttpGet("/Videos/{id}/{mediaSourceId}/Subtitles/{index}/subtitles.m3u8")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult> GetSubtitlePlaylist(
            [FromRoute] Guid id,
            [FromRoute] int index,
            [FromRoute] string mediaSourceId,
            [FromQuery, Required] int segmentLength)
        {
            var item = (Video)_libraryManager.GetItemById(id);

            var mediaSource = await _mediaSourceManager.GetMediaSource(item, mediaSourceId, null, false, CancellationToken.None).ConfigureAwait(false);

            var builder = new StringBuilder();

            var runtime = mediaSource.RunTimeTicks ?? -1;

            if (runtime <= 0)
            {
                throw new ArgumentException("HLS Subtitles are not supported for this media.");
            }

            var segmentLengthTicks = TimeSpan.FromSeconds(segmentLength).Ticks;
            if (segmentLengthTicks <= 0)
            {
                throw new ArgumentException("segmentLength was not given, or it was given incorrectly. (It should be bigger than 0)");
            }

            builder.AppendLine("#EXTM3U");
            builder.AppendLine("#EXT-X-TARGETDURATION:" + segmentLength.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("#EXT-X-VERSION:3");
            builder.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
            builder.AppendLine("#EXT-X-PLAYLIST-TYPE:VOD");

            long positionTicks = 0;

            var accessToken = _authContext.GetAuthorizationInfo(Request).Token;

            while (positionTicks < runtime)
            {
                var remaining = runtime - positionTicks;
                var lengthTicks = Math.Min(remaining, segmentLengthTicks);

                builder.AppendLine("#EXTINF:" + TimeSpan.FromTicks(lengthTicks).TotalSeconds.ToString(CultureInfo.InvariantCulture) + ",");

                var endPositionTicks = Math.Min(runtime, positionTicks + segmentLengthTicks);

                var url = string.Format(
                    CultureInfo.CurrentCulture,
                    "stream.vtt?CopyTimestamps=true&AddVttTimeMap=true&StartPositionTicks={0}&EndPositionTicks={1}&api_key={2}",
                    positionTicks.ToString(CultureInfo.InvariantCulture),
                    endPositionTicks.ToString(CultureInfo.InvariantCulture),
                    accessToken);

                builder.AppendLine(url);

                positionTicks += segmentLengthTicks;
            }

            builder.AppendLine("#EXT-X-ENDLIST");
            return File(Encoding.UTF8.GetBytes(builder.ToString()), MimeTypes.GetMimeType("playlist.m3u8"));
        }

        /// <summary>
        /// Encodes a subtitle in the specified format.
        /// </summary>
        /// <param name="id">The media id.</param>
        /// <param name="mediaSourceId">The source media id.</param>
        /// <param name="index">The subtitle index.</param>
        /// <param name="format">The format to convert to.</param>
        /// <param name="startPositionTicks">The start position in ticks.</param>
        /// <param name="endPositionTicks">The end position in ticks.</param>
        /// <param name="copyTimestamps">Whether to copy the timestamps.</param>
        /// <returns>A <see cref="Task{Stream}"/> with the new subtitle file.</returns>
        private Task<Stream> EncodeSubtitles(
            Guid id,
            string mediaSourceId,
            int index,
            string format,
            long startPositionTicks,
            long? endPositionTicks,
            bool copyTimestamps)
        {
            var item = _libraryManager.GetItemById(id);

            return _subtitleEncoder.GetSubtitles(
                item,
                mediaSourceId,
                index,
                format,
                startPositionTicks,
                endPositionTicks ?? 0,
                copyTimestamps,
                CancellationToken.None);
        }
    }
}
