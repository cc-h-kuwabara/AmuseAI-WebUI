using Amuse.App;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amuse.Web;

public static class WebJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }
}

public sealed class WebData
{
    public Settings Settings { get; }
    public string Root { get; }
    public string? ImportHistory { get; }

    public WebData(ServerOptions options)
    {
        Root = options.DataDirectory;
        Directory.CreateDirectory(Root);
        AppRuntime.DirectoryBase = AppContext.BaseDirectory;
        AppRuntime.DirectoryData = Root;
        AppRuntime.DirectoryServer = options.HostDirectory;
        AppRuntime.ConfirmExternalAccess = _ => throw new InvalidOperationException("Enable and acknowledge external tool access in Settings before using external tools.");
        var target = Path.Combine(Root, "Settings.json");
        if (options.ImportDirectory is not null)
        {
            if (File.Exists(target)) throw new ArgumentException("Import requires a new --data-dir; existing settings will not be overwritten.");
            var source = Path.Combine(options.ImportDirectory, "Settings.json");
            var imported = ReadSettings(source);
            var history = imported.DirectoryHistory;
            ImportHistory = string.IsNullOrWhiteSpace(history) ? Path.Combine(options.ImportDirectory, "History")
                : Path.IsPathFullyQualified(history) ? history : Path.GetFullPath(Path.Combine(options.ImportDirectory, history));
            // Reuse the model directory; keep Web history, runtime and temporary files separate.
            if (!Path.IsPathFullyQualified(imported.DirectoryModel ?? ""))
                imported.DirectoryModel = Path.GetFullPath(Path.Combine(options.ImportDirectory, imported.DirectoryModel ?? "Models"));
            if (!Directory.Exists(imported.DirectoryModel)) throw new ArgumentException("The imported model directory does not exist.");
            imported.DirectoryHistory = Path.Combine(Root, "History");
            imported.DirectoryTemp = Path.Combine(Root, "Temp");
            File.Copy(source, Path.Combine(Root, "Settings.desktop.backup.json"));
            WebJson.Save(target, imported);
        }
        if (!File.Exists(target))
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Settings.default.json"), target);
        Settings = ReadSettings(target);
        LinuxPlatform.Configure(Settings);
        var template = Path.Combine(Root, "Templates.json");
        if (!File.Exists(template)) File.Copy(Path.Combine(AppContext.BaseDirectory, "Templates.json"), template);
        Settings.Initialize(Root).GetAwaiter().GetResult();
        TensorStack.Media.MediaManager.Initialize(
            ffmpegPath: OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") : "ffmpeg",
            ffprobePath: OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "ffprobe.exe") : "ffprobe",
            directoryTemp: Settings.DirectoryTemp);
    }

    private static Settings ReadSettings(string path)
    {
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json.DefaultOptions)
            ?? throw new InvalidDataException($"Invalid settings: {path}");
        if (settings.DiffusionModels is null || settings.LanguageModels is null || settings.Components is null ||
            settings.Environments is null || settings.LoraAdapterModels is null || settings.ControlNetModels is null ||
            settings.UpscaleModels is null || settings.ExtractModels is null || settings.AccessTokens is null)
            throw new InvalidDataException($"Incomplete settings: {path}. Restore a backup; the file has not been replaced.");
        return settings;
    }
}

public sealed record StoredFile(string Id, string Name, string ContentType, long Length, DateTimeOffset Created);

public sealed class FileStore
{
    private readonly string directory;
    public FileStore(WebData data)
    {
        directory = Path.Combine(data.Root, "Files");
        Directory.CreateDirectory(directory);
        if (data.ImportHistory is not null && Directory.Exists(data.ImportHistory))
        {
            foreach (var path in Directory.EnumerateFiles(data.ImportHistory).Where(IsAllowed))
                AddPathAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
    public static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
        ".bmp" => "image/bmp", ".mp4" => "video/mp4", ".webm" => "video/webm", ".avi" => "video/x-msvideo",
        ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", ".flac" => "audio/flac",
        ".txt" => "text/plain; charset=utf-8", ".json" => "application/json", _ => "application/octet-stream"
    };
    public static bool IsAllowed(string name) => Mime(name) != "application/octet-stream";
    private string Base(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid file ID.");
        return Path.Combine(directory, id);
    }
    public string PathFor(string id)
    {
        var meta = Get(id);
        return Base(id) + Path.GetExtension(meta.Name).ToLowerInvariant();
    }
    public StoredFile Get(string id) => JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(Base(id) + ".meta.json"), WebJson.Options)!;
    public StoredFile[] List() => Directory.EnumerateFiles(directory, "*.meta.json")
        .Select(p => JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(p), WebJson.Options)!)
        .OrderByDescending(x => x.Created).ToArray();
    public StoredFile[] Trash() => Directory.EnumerateFiles(directory, "*.meta.deleted")
        .Select(p => JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(p), WebJson.Options)!)
        .OrderByDescending(x => x.Created).ToArray();
    public void Delete(string id) => File.Move(Base(id) + ".meta.json", Base(id) + ".meta.deleted");
    public void Restore(string id) => File.Move(Base(id) + ".meta.deleted", Base(id) + ".meta.json");
    public async Task<StoredFile> AddAsync(Stream stream, string name, CancellationToken ct)
    {
        name = Path.GetFileName(name);
        if (!IsAllowed(name)) throw new ArgumentException("Unsupported media file extension.");
        var id = Guid.NewGuid().ToString("N");
        var path = Base(id) + Path.GetExtension(name).ToLowerInvariant();
        try
        {
            await using (var output = File.Create(path)) await stream.CopyToAsync(output, ct);
            var meta = new StoredFile(id, name, Mime(name), new FileInfo(path).Length, DateTimeOffset.UtcNow);
            WebJson.Save(Base(id) + ".meta.json", meta);
            return meta;
        }
        catch { File.Delete(path); throw; }
    }
    public async Task<StoredFile> AddPathAsync(string path, CancellationToken ct)
    {
        await using var source = File.OpenRead(path);
        return await AddAsync(source, Path.GetFileName(path), ct);
    }
}
