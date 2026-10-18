using Amuse.App;
using Amuse.App.Services;
using TensorStack.Common;
using TensorStack.Common.Audio;
using TensorStack.Common.Video;
using TensorStack.Media.Audio;
using TensorStack.Media.Video;
using TensorStack.WPF.Image;

namespace Amuse.Web;

public sealed record ClipRequest(string File, double Start = 0, double Duration = 3, bool IncludeAudio = true);
public sealed record CompositionRequest(ClipRequest[] Clips, int Width = 768, int Height = 512,
    float FrameRate = 24, string? Audio = null, string? Overlay = null);

public sealed class CompositionService(FileStore files, IMediaService media, MediaExport export)
{
    public void Validate(CompositionRequest request)
    {
        if (request.Clips is null || request.Clips.Length is < 1 or > 200) throw new ArgumentException("Provide between 1 and 200 clips.");
        if (request.Width is < 64 or > 4096 || request.Height is < 64 or > 4096 || request.Width % 2 != 0 || request.Height % 2 != 0 ||
            !float.IsFinite(request.FrameRate) || request.FrameRate is < 1 or > 120) throw new ArgumentException("Use even dimensions between 64 and 4096 and a frame rate between 1 and 120.");
        foreach (var clip in request.Clips)
        {
            if (!double.IsFinite(clip.Start) || !double.IsFinite(clip.Duration) || clip.Start < 0 || clip.Duration < .1 || clip.Duration > 3600)
                throw new ArgumentException("Clip start must be positive; duration must be between 0.1 and 3600 seconds.");
            var file = files.Get(clip.File);
            if (!file.ContentType.StartsWith("image/") && !file.ContentType.StartsWith("video/")) throw new ArgumentException("Only images and videos can be used as clips.");
        }
        if (request.Clips.Sum(x => x.Duration) > 3600) throw new ArgumentException("The composed video must be at most one hour long.");
        if (request.Audio is not null && !files.Get(request.Audio).ContentType.StartsWith("audio/")) throw new ArgumentException("Select an audio file for the soundtrack.");
        if (request.Overlay is not null && !files.Get(request.Overlay).ContentType.StartsWith("image/")) throw new ArgumentException("Select an image for the overlay.");
    }
    public async Task<string[]> ExportAsync(CompositionRequest request, JobContext job)
    {
        Validate(request);
        var output = media.GetTempFile(MediaType.Video);
        var duration = request.Clips.Sum(c => Math.Round(c.Duration * request.FrameRate) / request.FrameRate);
        var timeline = new AudioTimeline(TimeSpan.FromSeconds(duration));
        var position = 0d;
        foreach (var clip in request.Clips)
        {
            var clipDuration = Math.Round(clip.Duration * request.FrameRate) / request.FrameRate;
            if (request.Audio is null && clip.IncludeAudio && files.Get(clip.File).ContentType.StartsWith("video/"))
            {
                var source = files.PathFor(clip.File);
                if (await AudioManager.LoadInfoAsync(source) is not null)
                    timeline.Segments.Add(new AudioSegment(source, TimeSpan.FromSeconds(clip.Start), TimeSpan.FromSeconds(clipDuration), TimeSpan.FromSeconds(position)));
            }
            position += clipDuration;
        }
        if (request.Audio is not null)
            timeline.Segments.Add(new AudioSegment(files.PathFor(request.Audio), TimeSpan.Zero, TimeSpan.FromSeconds(duration), TimeSpan.Zero));
        await VideoManager.WriteVideoStreamAsync(output, Frames(request, job), cancellationToken: job.Token);
        if (timeline.Segments.Count > 0)
            await media.SaveWithAudioAsync(await VideoInputStream.CreateAsync(output), timeline, job.Token);
        return [(await export.StoreAsync(output, job)).Id];
    }
    private async IAsyncEnumerable<VideoFrame> Frames(CompositionRequest request, JobContext job)
    {
        var index = 0;
        var total = request.Clips.Sum(c => (int)Math.Round(c.Duration * request.FrameRate));
        var overlay = request.Overlay is null ? null : (await ImageInput.CreateAsync(files.PathFor(request.Overlay))).ResizeImage(request.Width, request.Height, ResizeMode.Crop);
        foreach (var clip in request.Clips)
        {
            var count = (int)Math.Round(clip.Duration * request.FrameRate);
            if (files.Get(clip.File).ContentType.StartsWith("image/"))
            {
                var image = (await ImageInput.CreateAsync(files.PathFor(clip.File))).ResizeImage(request.Width, request.Height, ResizeMode.Crop);
                if (overlay is not null) image.OverlayImage(overlay);
                for (var i = 0; i < count; i++)
                {
                    job.Token.ThrowIfCancellationRequested();
                    yield return new VideoFrame(index++, image, request.FrameRate);
                    job.Progress("Encoding video", index, total);
                }
            }
            else
            {
                var stream = await VideoInputStream.CreateAsync(files.PathFor(clip.File));
                var skip = (int)Math.Round(clip.Start * request.FrameRate);
                var seen = 0;
                var written = 0;
                await foreach (var frame in stream.GetAsync(request.Width, request.Height, request.FrameRate, ResizeMode.Crop, cancellationToken: job.Token))
                {
                    if (seen++ < skip) continue;
                    if (written++ >= count) break;
                    if (overlay is not null) frame.Frame.OverlayImage(overlay);
                    yield return new VideoFrame(index++, frame.Frame, request.FrameRate);
                    job.Progress("Encoding video", index, total);
                }
                if (written < count) throw new ArgumentException("A clip extends beyond the source video. Shorten its duration.");
            }
        }
    }
}
