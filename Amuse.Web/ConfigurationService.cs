using Amuse.App;
using Amuse.App.Common;
using Amuse.App.Services;
using Amuse.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using TensorStack.Common;
using TensorStack.Common.Common;
using TensorStack.WPF.Services;

namespace Amuse.Web;

public sealed class ConfigurationService(WebData data, IServiceProvider services)
{
    private Settings Settings => data.Settings;
    private static readonly Dictionary<string, string> Sections = new()
    {
        ["diffusion"] = nameof(Settings.DiffusionModels), ["language"] = nameof(Settings.LanguageModels),
        ["upscale"] = nameof(Settings.UpscaleModels), ["extract"] = nameof(Settings.ExtractModels),
        ["lora"] = nameof(Settings.LoraAdapterModels), ["controlNet"] = nameof(Settings.ControlNetModels),
        ["component"] = nameof(Settings.Components), ["environments"] = nameof(Settings.Environments)
    };
    public object Get(string section)
    {
        if (section == "general") return new
        {
            Settings.DirectoryModel, Settings.HistoryItems, Settings.ReadBuffer, Settings.WriteBuffer,
            Settings.VideoCodec, Settings.IsDiffusionImagePreviewEnabled, Settings.IsServerDebugEnabled,
            Settings.IsLegacyDeviceDetection, Settings.DefaultDeviceId,
            Settings.IsExternalToolsEnabled, Settings.IsExternalToolsAcknowledged
        };
        if (section == "tokens") return Settings.AccessTokens.Select(x => new { x.Name, x.Domain, configured = !string.IsNullOrEmpty(x.Token) }).ToArray();
        if (section == "templates") return Settings.Templates;
        if (!Sections.TryGetValue(section, out var name)) throw new ArgumentException("Unknown configuration section.");
        return typeof(Settings).GetProperty(name)!.GetValue(Settings)!;
    }
    public async Task<string[]> SaveAsync(string section, JsonElement input, JobContext job)
    {
        job.Token.ThrowIfCancellationRequested();
        var before = JsonSerializer.Serialize(Settings, Json.DefaultOptions);
        var templatesBefore = Settings.Templates;
        try
        {
        var backup = Path.Combine(data.Root, "Settings.before-web-edit.json");
        File.Copy(Path.Combine(data.Root, "Settings.json"), backup, true);
        if (section == "tokens")
        {
            var tokens = input.Deserialize<TokenUpdate[]>(WebJson.Options) ?? throw new ArgumentException("Expected a token array.");
            foreach (var token in tokens)
            {
                var entry = Settings.AccessTokens.FirstOrDefault(x => x.Name == token.Name) ?? throw new ArgumentException("Unknown access token name.");
                if (token.Token is not null) entry.Token = token.Token;
            }
        }
        else if (section == "general")
        {
            var allowed = new HashSet<string>([nameof(Settings.HistoryItems), nameof(Settings.ReadBuffer), nameof(Settings.WriteBuffer),
                nameof(Settings.VideoCodec), nameof(Settings.IsDiffusionImagePreviewEnabled), nameof(Settings.IsServerDebugEnabled), nameof(Settings.DefaultDeviceId),
                nameof(Settings.IsLegacyDeviceDetection), nameof(Settings.IsExternalToolsEnabled), nameof(Settings.IsExternalToolsAcknowledged)], StringComparer.OrdinalIgnoreCase);
            if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a settings object.");
            foreach (var item in input.EnumerateObject())
            {
                if (item.Name.Equals(nameof(Settings.DirectoryModel), StringComparison.OrdinalIgnoreCase))
                {
                    var path = item.Value.GetString();
                    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new ArgumentException("Model directory must be an existing absolute directory.");
                    Settings.SetModelDirectory(path);
                }
                else
                {
                    if (!allowed.Contains(item.Name)) continue;
                    var property = typeof(Settings).GetProperties().Single(x => x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                    var value = item.Value.Deserialize(property.PropertyType, WebJson.Options);
                    if (value is int count && property.Name is nameof(Settings.HistoryItems) or nameof(Settings.ReadBuffer) or nameof(Settings.WriteBuffer) && count < 1)
                        throw new ArgumentException("Counts and buffers must be positive.");
                    property.SetValue(Settings, value);
                }
            }
        }
        else if (section == "templates")
        {
            var templates = input.Deserialize<TemplateSettings>(WebJson.Options) ?? throw new ArgumentException("Invalid templates.");
            Settings.Templates = templates;
            WebJson.Save(Path.Combine(data.Root, "Templates.json"), templates);
        }
        else
        {
            if (!Sections.TryGetValue(section, out var name)) throw new ArgumentException("Unknown configuration section.");
            if (input.ValueKind != JsonValueKind.Array) throw new ArgumentException("Expected an array.");
            var property = typeof(Settings).GetProperty(name)!;
            var value = input.Deserialize(property.PropertyType, WebJson.Options) ?? throw new ArgumentException("Invalid collection.");
            if (value is System.Collections.IEnumerable entries)
            {
                var ids = new HashSet<int>();
                foreach (var entry in entries)
                {
                    if (entry is null) throw new ArgumentException("Configuration entries cannot be null.");
                    if (entry is IDownloadModel model && (string.IsNullOrWhiteSpace(model.Name) || !ids.Add(model.Id)))
                        throw new ArgumentException("Models must have a name and unique IDs within their category.");
                    if (entry is EnvironmentModel environment && string.IsNullOrWhiteSpace(environment.Environment))
                        throw new ArgumentException("Each environment requires an environment directory name.");
                }
            }
            var old = property.GetValue(Settings);
            try { property.SetValue(Settings, value); Settings.ScanModels(); }
            catch { property.SetValue(Settings, old); Settings.ScanModels(); throw; }
        }
        Settings.ScanModels();
        await Settings.SaveAsync();
        return [];
        }
        catch
        {
            var original = JsonSerializer.Deserialize<Settings>(before, Json.DefaultOptions)!;
            foreach (var property in typeof(Settings).GetProperties().Where(x => x.SetMethod?.IsPublic == true && x.CanRead &&
                x.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always))
                property.SetValue(Settings, property.GetValue(original));
            Settings.Templates = templatesBefore;
            Settings.SetModelDirectory(original.DirectoryModel);
            Settings.ScanModels();
            throw;
        }
    }
    public void ValidateEnvironment(int index, string operation)
    {
        if (index < 0 || index >= Settings.Environments.Count) throw new ArgumentException("Environment not found.");
        if (operation is not ("create" or "update" or "rebuild")) throw new ArgumentException("Unsupported environment operation.");
    }
    public async Task<string[]> EnvironmentAsync(int index, string operation, JobContext job)
    {
        ValidateEnvironment(index, operation);
        var environment = Settings.Environments[index];
        Engine.EnsureHost(environment.Backend);
        var service = services.GetRequiredService<IEnvironmentService>();
        var progress = new InlineProgress<PipelineProgress>(p => job.Progress(p.Message ?? p.Key ?? "Preparing environment", p.Value, p.Maximum));
        switch (operation)
        {
            case "create": await service.CreateAsync(environment, progress, job.Token); break;
            case "update": await service.UpdateAsync(environment, progress, job.Token); break;
            case "rebuild": await service.RebuildAsync(environment, progress, job.Token); break;
        }
        return [];
    }
    public async Task<string[]> DownloadAsync(string kind, int id, JobContext job)
    {
        var model = services.GetRequiredService<Engine>().Model(kind, id);
        if (!string.IsNullOrEmpty(model.AccessToken) && !Settings.AccessTokens.Any(x => x.Name == model.AccessToken && !string.IsNullOrEmpty(x.Token)))
            throw new ArgumentException($"Set the {model.AccessToken} token in Settings before downloading.");
        var downloader = services.GetRequiredService<DownloadService>();
        var components = Settings.Components;
        var activeComponents = new HashSet<string>();
        async Task Download(CheckpointComponent checkpoint, string directory)
        {
            if (checkpoint.Type == CheckpointType.Component)
            {
                var component = components.FirstOrDefault(x => x.Key == checkpoint.Path) ?? throw new ArgumentException("Referenced component not found.");
                if (!activeComponents.Add(component.Key)) throw new ArgumentException("Circular component reference.");
                await Download(component.Checkpoint, Settings.DirectoryModel);
                activeComponents.Remove(component.Key);
            }
            else if (checkpoint.Type is CheckpointType.OnlineFile or CheckpointType.OnlineFolder && !checkpoint.IsInstalled(directory, components))
            {
                var path = CheckpointComponent.GetSafePath(directory, checkpoint.Folder, checkpoint.Path);
                // Use the same folder mapping as the batch downloader, but retain the
                // current filename even on Windows where DownloadProgress has no name.
                var files = FileHelper.GetUrlFileMapping(checkpoint.DownloadFiles ?? [], path);
                foreach (var file in files)
                {
                    job.Token.ThrowIfCancellationRequested();
                    if (File.Exists(file.Value)) continue;
                    var name = Path.GetRelativePath(path, file.Value);
                    job.Progress($"Downloading {name} — 0 B");
                    await downloader.DownloadAsync(file.Key, file.Value,
                        new InlineProgress<DownloadProgress>(progress =>
                        {
                            var size = FormatDownloadSize(progress.TotalBytes);
                            if (progress.TotalSize > 0) size += $" / {FormatDownloadSize(progress.TotalSize)}";
                            var percent = progress.TotalSize > 0
                                ? (int)Math.Clamp(progress.TotalBytes * 100.0 / progress.TotalSize, 0, 100) : 0;
                            job.Progress($"Downloading {name} — {size}", percent, progress.TotalSize > 0 ? 100 : 0);
                        }), job.Token);
                }
            }
        }
        if (model is IGenerateModel generate)
        {
            var directory = kind == "language" ? Settings.DirectoryLangaugeModel : Settings.DirectoryDiffusion;
            foreach (var checkpoint in generate.GetComponents()) await Download(checkpoint, directory);
        }
        else
        {
            var checkpoint = model switch
            {
                UpscaleModel m => m.Checkpoint, ExtractModel m => m.Checkpoint, LoraAdapterModel m => m.Checkpoint,
                ControlNetModel m => m.Checkpoint, ComponentModel m => m.Checkpoint,
                _ => throw new ArgumentException("Unknown checkpoint model.")
            };
            var directory = kind switch
            {
                "upscale" => Settings.DirectoryUpscale, "extract" => Settings.DirectoryExtract,
                "lora" => Settings.DirectoryLoraAdapter, "controlNet" => Settings.DirectoryControlNet, _ => Settings.DirectoryModel
            };
            await Download(checkpoint, directory);
        }
        Settings.ScanModels();
        await Settings.SaveAsync();
        return [];
    }
    private static string FormatDownloadSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.00", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private sealed record TokenUpdate(string Name, string? Token);
}
