using Amuse.App;
using System.Diagnostics;

namespace Amuse.Web;

// The desktop's mp4v codec is not reliably playable in a browser. Use the
// bundled FFmpeg's software OpenH264 encoder for portable browser playback.
public sealed class MediaExport(WebData data, FileStore files)
{
    public async Task<StoredFile> StoreAsync(string path, JobContext job)
    {
        if (!FileStore.Mime(path).StartsWith("video/")) return await files.AddPathAsync(path, job.Token);
        var executable = OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") : "ffmpeg";
        if (OperatingSystem.IsWindows() && !File.Exists(executable)) throw new FileNotFoundException("The bundled ffmpeg.exe is required for browser video playback.");
        var output = Path.Combine(data.Settings.DirectoryTemp, Guid.NewGuid().ToString("N") + ".mp4");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", path,
            "-map", "0:v:0", "-map", "0:a?", "-c:v", OperatingSystem.IsWindows() ? "libopenh264" : "libx264", "-b:v", "4M", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-movflags", "+faststart", output }) start.ArgumentList.Add(argument);
        job.Progress("Preparing video for browser playback");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        var errors = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(job.Token);
            var error = await errors;
            await stdout;
            if (process.ExitCode != 0) throw new InvalidOperationException("Video encoding failed: " + error[^Math.Min(error.Length, 2000)..]);
            return await files.AddPathAsync(output, job.Token);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            File.Delete(output);
        }
    }
}
