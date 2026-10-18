using Amuse.App;
using Amuse.App.Common;
using Amuse.App.Services;
using Amuse.App.Views;
using Amuse.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using TensorStack.Common;
using TensorStack.Common.Tensor;
using TensorStack.Common.Pipeline;
using TensorStack.Common.Video;
using TensorStack.Media.Audio;
using TensorStack.Media.Video;
using TensorStack.WPF.Image;
using TensorStack.WPF.Services;

namespace Amuse.Web;

public sealed record GenerateRequest(string ModelKind, int ModelId, string View, int DeviceId,
    JsonElement Options, string[]? Images = null, string[]? Controls = null, string[]? Audios = null,
    string[]? Videos = null, int[]? Loras = null, int? ControlNetId = null,
    QualityMode Quality = QualityMode.Standard, MemoryMode Memory = MemoryMode.Auto, int Count = 1, string[]? Tools = null);
public sealed record ProcessRequest(string Operation, int ModelId, int DeviceId, string File,
    JsonElement Options, int Multiplier = 2);

public sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

public sealed class Engine(WebData data, FileStore files, IServiceProvider services, MediaExport export, ILogger<Engine> logger)
{
    private readonly Settings settings = data.Settings;
    private bool devicesReady;
    private readonly object deviceLock = new();
    public string? HardwareError { get; private set; }
    public DeviceModel[] Devices()
    {
        lock (deviceLock)
        {
            if (!devicesReady)
            {
                try { settings.InitializeDevices(services.GetRequiredService<IHardwareService>().GetGPUDevices()); }
                catch (Exception ex) { HardwareError = ex.Message; logger.LogWarning(ex, "GPU detection failed"); settings.Devices = []; }
                devicesReady = true;
            }
            return settings.Devices?.ToArray() ?? [];
        }
    }
    private DeviceModel Device(int id) => Devices().FirstOrDefault(x => x.Id == id)
        ?? throw new ArgumentException("Select an available GPU device. Check GPU drivers if no devices are listed.");

    public object Catalog()
    {
        var devices = Devices().Select(d => new { d.Id, d.Name, d.Memory, d.Vendor, d.QualityModes, d.SupportedBackends });
        object Describe(IGenerateModel m, string kind) => new
        {
            kind, m.Id, m.Name, m.Backend, m.MediaType, m.Pipeline, processTypes = Processes(m), m.ViewFilter,
            m.Status, m.DefaultOptions, m.MemoryProfile
        };
        return new
        {
            devices, hardwareError = HardwareError,
            models = settings.DiffusionModels.Select(m => Describe(m, "diffusion"))
                .Concat(settings.LanguageModels.Select(m => Describe(m, "language"))),
            upscalers = settings.UpscaleModels.Select(x => new { x.Id, x.Name, x.Status }),
            extractors = settings.ExtractModels.Select(x => new { x.Id, x.Name, x.Status }),
            loras = settings.LoraAdapterModels.Select(x => new { x.Id, x.Name, x.Status, x.Pipeline }),
            controlNets = settings.ControlNetModels.Select(x => new { x.Id, x.Name, x.Status, x.Pipeline }),
            processes = Enum.GetNames<ProcessType>(), schedulers = Enum.GetNames<SchedulerType>(),
            views = Enum.GetNames<View>(), environments = settings.Environments,
            tools = services.GetRequiredService<IToolService>().ToolDefinitions.Select(t => new { t.Name, t.DisplayName, t.Description })
        };
    }

    public void Validate(GenerateRequest request)
    {
        if (request.Count is < 1 or > 100) throw new ArgumentException("Count must be between 1 and 100.");
        var model = Model(request.ModelKind, request.ModelId) as IGenerateModel ?? throw new ArgumentException("Not a generation model.");
        var view = ParseView(request.View);
        if (model.ViewFilter is { Length: > 0 } && !model.ViewFilter.Contains(view))
            throw new ArgumentException("The selected model does not support this workflow.");
        if (!Enum.IsDefined(request.Quality) || !Enum.IsDefined(request.Memory)) throw new ArgumentException("Invalid quality or memory mode.");
        foreach (var id in (request.Images ?? []).Concat(request.Controls ?? []).Concat(request.Audios ?? []).Concat(request.Videos ?? [])) files.Get(id);
        if (view == View.ImageInpaint && (request.Images?.Length ?? 0) < 2)
            throw new ArgumentException("Inpainting requires a source image and a mask image, in that order.");
        if (view is View.ImageToImage or View.ImageEdit or View.PaintToImage or View.ImageToVideo or View.ImageToText && (request.Images?.Length ?? 0) == 0)
            throw new ArgumentException("This workflow requires an input image.");
        if (view is View.VideoToVideo or View.FrameToFrame && (request.Videos?.Length ?? 0) == 0)
            throw new ArgumentException("This workflow requires an input video.");
        if (view == View.AudioToText && (request.Audios?.Length ?? 0) == 0) throw new ArgumentException("Select an input audio file.");
        // Validate options before accepting a job. Device detection/loading happens in the worker.
        _ = Options(model, request.Options);
    }

    private static View ParseView(string name)
    {
        if (!Enum.TryParse<View>(name, out var view) || !GenerationViews.Contains(view))
            throw new ArgumentException("Unsupported generation workflow.");
        return view;
    }
    public static readonly View[] GenerationViews = [View.TextToImage, View.ImageToImage, View.ImageEdit, View.ImageInpaint,
        View.PaintToImage, View.TextToVideo, View.ImageToVideo, View.VideoToVideo, View.FrameToFrame,
        View.TextToAudio, View.TextToMusic, View.AudioToText, View.TextInstruct, View.TextConverse, View.ImageToText];
    private static ProcessType Process(View view) => view switch
    {
        View.PaintToImage or View.FrameToFrame => ProcessType.ImageToImage,
        View.TextInstruct or View.TextConverse => ProcessType.TextToText,
        View.TextToMusic => ProcessType.TextToAudio,
        _ when Enum.TryParse<ProcessType>(view.ToString(), out var value) => value,
        _ => throw new ArgumentException("The workflow has no matching generation process.")
    };
    private static ProcessType[] Processes(IGenerateModel model) => model is DiffusionModel d ? d.ProcessTypes : ((LanguageModel)model).ProcessTypes;
    public IDownloadModel Model(string kind, int id) => (kind switch
    {
        "diffusion" => (IDownloadModel?)settings.DiffusionModels.FirstOrDefault(x => x.Id == id),
        "language" => settings.LanguageModels.FirstOrDefault(x => x.Id == id),
        "upscale" => settings.UpscaleModels.FirstOrDefault(x => x.Id == id),
        "extract" => settings.ExtractModels.FirstOrDefault(x => x.Id == id),
        "lora" => settings.LoraAdapterModels.FirstOrDefault(x => x.Id == id),
        "controlNet" => settings.ControlNetModels.FirstOrDefault(x => x.Id == id),
        "component" => settings.Components.FirstOrDefault(x => x.Id == id),
        _ => throw new ArgumentException("Unknown model kind.")
    }) ?? throw new ArgumentException("Model not found.");

    private static GenerateInputOptions Options(IGenerateModel model, JsonElement input)
    {
        var defaults = model.DefaultOptions ?? new GenerateDefaultOptions();
        var merged = JsonSerializer.SerializeToNode(defaults, WebJson.Options)!.AsObject();
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Options must be a JSON object.");
        foreach (var p in input.EnumerateObject()) merged[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        var options = merged.Deserialize<GenerateInputOptions>(WebJson.Options)!;
        options.SchedulerOptions ??= defaults.Schedulers?.Copy()?.FirstOrDefault()
            ?? new SchedulerInputOptions { Scheduler = defaults.Scheduler };
        options.LoraOptions ??= [];
        options.Conversation ??= [];
        // Explicitly selected tools can be enabled later; never inherit desktop tool execution permissions.
        options.IsToolCallsEnabled = false;
        if (options.Width is < 0 or > 16384 || options.Height is < 0 or > 16384 || options.Steps is < 0 or > 1000 ||
            options.Frames is < 0 or > 100000 || options.MaxLength is < 0 or > 1000000)
            throw new ArgumentException("Generation options exceed supported limits.");
        return options;
    }

    public async Task<string[]> GenerateAsync(GenerateRequest request, JobContext job)
    {
        Validate(request);
        var model = (IGenerateModel)Model(request.ModelKind, request.ModelId);
        var view = ParseView(request.View);
        var device = Device(request.DeviceId);
        if (device.SupportedBackends?.Contains(model.Backend) == false) throw new ArgumentException("The selected execution device does not support this backend.");
        var process = Process(view);
        if (request.ControlNetId is not null && Processes(model)?.Contains(ProcessType.ImageControlNet) == true) process = ProcessType.ImageControlNet;
        if (Processes(model)?.Contains(process) == false) throw new ArgumentException("Model does not support this process.");
        var pipeline = new PipelineModel
        {
            Device = device, DiffusionModel = model as DiffusionModel, LanguageModel = model as LanguageModel,
            ViewType = view, ProcessType = process, QualityMode = request.Quality, MemoryMode = request.Memory,
            LoraAdapterModel = (request.Loras ?? []).Select(id => (LoraAdapterModel)Model("lora", id)).ToArray(),
            ControlNetModel = request.ControlNetId is int cid ? (ControlNetModel)Model("controlNet", cid) : null
        };
        var generate = services.GetRequiredService<IGenerateService>();
        var previewGate = new object();
        var lastPreview = DateTimeOffset.MinValue;
        var lastText = DateTimeOffset.MinValue;
        var textBuffer = new StringBuilder();
        var previewTask = Task.CompletedTask;
        var progress = new InlineProgress<PipelineProgress>(p =>
        {
            job.Progress(p.Subkey == "Token" ? "Generating text" : p.Message ?? p.Key ?? "Working", p.Value, p.Maximum);
            lock (previewGate)
            {
                if (p.Subkey == "Token" && textBuffer.Length < 1000000)
                {
                    textBuffer.Append(p.Message);
                    if (DateTimeOffset.UtcNow - lastText > TimeSpan.FromMilliseconds(400))
                    { lastText = DateTimeOffset.UtcNow; job.Text(textBuffer.ToString()); }
                }
                if (p.Subkey == "Step" && p.Tensors?.Count > 0 && previewTask.IsCompleted &&
                    DateTimeOffset.UtcNow - lastPreview > TimeSpan.FromSeconds(2))
                {
                    lastPreview = DateTimeOffset.UtcNow;
                    previewTask = PreviewAsync(p);
                }
            }
        });
        async Task PreviewAsync(PipelineProgress p)
        {
            try
            {
                var image = await generate.GeneratePreviewAsync(p);
                if (image is not null && !job.Token.IsCancellationRequested)
                    await job.PreviewAsync(path => image.SaveAsync(path));
            }
            catch (Exception ex) { logger.LogDebug(ex, "Preview is unavailable"); }
        }
        job.Token.ThrowIfCancellationRequested();
        try
        {
            EnsureHost(model.Backend);
            job.Progress("Loading model. Create its environment first if requested.");
            await Cancellable(generate.LoadAsync(pipeline, progress), generate, job.Token);
            var results = new List<string>();
            for (var i = 0; i < request.Count; i++)
            {
                job.Token.ThrowIfCancellationRequested();
                var options = Options(model, request.Options);
                options.SelectedTools = (request.Tools ?? []).ToList();
                options.IsToolCallsEnabled = options.SelectedTools.Count > 0;
                if (request.Count > 1 && options.Seed > 0) options.Seed = checked(options.Seed + i);
                foreach (var id in request.Images ?? []) options.InputImages.Add(await ImageInput.CreateAsync(files.PathFor(id)));
                foreach (var id in request.Controls ?? []) options.InputControlImages.Add(await ImageInput.CreateAsync(files.PathFor(id)));
                foreach (var id in request.Audios ?? []) options.InputAudios.Add(await AudioInputStream.CreateAsync(files.PathFor(id)));
                foreach (var id in view == View.FrameToFrame ? [] : request.Videos ?? [])
                {
                    var stream = await VideoInputStream.CreateAsync(files.PathFor(id));
                    var frames = new List<ImageTensor>();
                    await foreach (var frame in stream.GetAsync(options.Width, options.Height, options.FrameRate, ResizeMode.Crop, cancellationToken: job.Token))
                    {
                        if (options.Frames > 0 && frames.Count >= options.Frames) break;
                        frames.Add(frame.Frame);
                    }
                    options.InputVideos.Add(new VideoSequence([.. frames], options.FrameRate));
                }
                foreach (var lora in pipeline.LoraAdapterModel)
                    if (!options.LoraOptions.Any(x => x.Key == lora.Key)) options.LoraOptions.Add(new() { Key = lora.Key, Name = lora.Name, Strength = 1 });
                string result;
                if (view == View.TextConverse)
                {
                    if (options.Conversation.Count == 0 && !string.IsNullOrWhiteSpace(options.Instruction))
                        options.Conversation.Add(new() { Role = ConversationRole.System, Content = options.Instruction });
                    options.Conversation.Add(new() { Role = ConversationRole.User, Content = options.Prompt,
                        ImageIndex = (request.Images ?? []).Select((id, n) => (id, n)).ToDictionary(x => x.n, x => x.id) });
                    var contextImages = options.Conversation.Where(m => m.ImageIndex is not null)
                        .SelectMany(m => m.ImageIndex.Values).Distinct().ToList();
                    options.InputImages.Clear();
                    foreach (var id in contextImages) options.InputImages.Add(await ImageInput.CreateAsync(files.PathFor(id)));
                    foreach (var message in options.Conversation.Where(m => m.ImageIndex is not null))
                        message.ImageIndex = message.ImageIndex.Values.ToDictionary(id => contextImages.IndexOf(id), id => id);
                    options.Prompt = null;
                    options.Instruction = null;
                }
                if (view == View.FrameToFrame)
                {
                    var source = await VideoInputStream.CreateAsync(files.PathFor(request.Videos![0]));
                    var output = services.GetRequiredService<IMediaService>().GetTempFile(MediaType.Video);
                    await services.GetRequiredService<IMediaService>().SaveWithAudioAsync(Frames(source, generate, options, job), source.SourceFile, output, job.Token);
                    result = output;
                }
                else if (view is View.TextInstruct or View.TextConverse or View.ImageToText or View.AudioToText)
                {
                    var text = await Cancellable(generate.GenerateTextAsync(options), generate, job.Token);
                    result = Path.Combine(settings.DirectoryTemp, Guid.NewGuid().ToString("N") + ".txt");
                    await File.WriteAllTextAsync(result, string.Join("\n\n", text.Results.Select(x => x.Text)), job.Token);
                    if (view == View.TextConverse)
                    {
                        options.Conversation.Add(new() { Role = ConversationRole.Assistant, Content = Utils.GetResponseText(text.Result.Text) });
                        await using var conversation = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(options.Conversation, WebJson.Options));
                        results.Add((await files.AddAsync(conversation, $"{job.Id}.conversation.json", job.Token)).Id);
                    }
                }
                else if (model.MediaType == MediaType.Image)
                {
                    var image = await Cancellable(generate.GenerateImageAsync(options), generate, job.Token);
                    var input = await image.ToImageInputAsync();
                    result = Path.Combine(settings.DirectoryTemp, Guid.NewGuid().ToString("N") + ".png");
                    await input.SaveAsync(result);
                }
                else if (model.MediaType == MediaType.Video) result = (await Cancellable(generate.GenerateVideoAsync(options), generate, job.Token)).SourceFile;
                else if (model.MediaType == MediaType.Audio) result = (await Cancellable(generate.GenerateAudioAsync(options), generate, job.Token)).SourceFile;
                else throw new ArgumentException("Unknown output media type.");
                var stored = await export.StoreAsync(result, job);
                results.Add(stored.Id);
                await using var recipe = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { request, actualOptions = options, model = model.Name }, WebJson.Options));
                results.Add((await files.AddAsync(recipe, $"{stored.Id}.recipe.json", job.Token)).Id);
                job.Progress($"Saved {i + 1}/{request.Count}", i + 1, request.Count);
            }
            return results.ToArray();
        }
        finally
        {
            try { await previewTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            await generate.StopAsync();
        }
    }

    private static async IAsyncEnumerable<VideoFrame> Frames(VideoInputStream source, IGenerateService generate, GenerateInputOptions options, JobContext job)
    {
        await foreach (var frame in source.GetAsync(cancellationToken: job.Token))
        {
            job.Token.ThrowIfCancellationRequested();
            var image = await Cancellable(generate.GenerateImageAsync(options with { InputImages = [frame.Frame] }), generate, job.Token);
            yield return new VideoFrame(frame.Index, image, frame.SourceFrameRate);
            job.Progress("Processing video frames", frame.Index + 1, source.FrameCount);
        }
    }

    private static async Task Cancellable(Task operation, IGenerateService service, CancellationToken ct)
    {
        try { await operation.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Let the load/run observe cancellation before disposing its owner.
            // Disposing first could clear the load CTS and orphan a late host.
            try { await service.CancelAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { await operation.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            await service.StopAsync();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            throw;
        }
    }
    private static async Task<T> Cancellable<T>(Task<T> operation, IGenerateService service, CancellationToken ct)
    {
        await Cancellable((Task)operation, service, ct);
        return await operation;
    }

    public async Task<string[]> ProcessAsync(ProcessRequest request, JobContext job)
    {
        var path = files.PathFor(request.File);
        var device = Device(request.DeviceId);
        var type = files.Get(request.File).ContentType;
        var video = type.StartsWith("video/");
        if (!video && !type.StartsWith("image/")) throw new ArgumentException("Select an image or video.");
        job.Token.ThrowIfCancellationRequested();
        string result;
        var progress = new InlineProgress<RunProgress>(_ => job.Progress("Processing media"));
        if (request.Operation == "upscale")
        {
            var service = services.GetRequiredService<IUpscaleService>();
            using var cancel = job.Token.Register(() => _ = service.CancelAsync());
            try
            {
                await service.LoadAsync(new() { Device = device, UpscaleModel = (UpscaleModel)Model("upscale", request.ModelId) });
                var options = request.Options.ValueKind == JsonValueKind.Object
                    ? request.Options.Deserialize<UpscaleInputOptions>(WebJson.Options)! : service.DefaultOptions;
                if (video) result = (await service.ExecuteAsync(new UpscaleVideoRequest { VideoStream = await VideoInputStream.CreateAsync(path), Options = options }, progress)).SourceFile;
                else result = await SaveImage(await service.ExecuteAsync(new UpscaleImageRequest { Image = await ImageInput.CreateAsync(path), Options = options }, progress));
            }
            finally { await service.UnloadAsync(); }
        }
        else if (request.Operation == "extract")
        {
            var service = services.GetRequiredService<IExtractService>();
            using var cancel = job.Token.Register(() => _ = service.CancelAsync());
            try
            {
                await service.LoadAsync(new() { Device = device, ExtractModel = (ExtractModel)Model("extract", request.ModelId) });
                var options = request.Options.ValueKind == JsonValueKind.Object
                    ? request.Options.Deserialize<ExtractInputOptions>(WebJson.Options)! : service.DefaultOptions;
                if (video) result = (await service.ExecuteAsync(new ExtractVideoRequest { VideoStream = await VideoInputStream.CreateAsync(path), Options = options }, progress)).SourceFile;
                else result = await SaveImage(await service.ExecuteAsync(new ExtractImageRequest { Image = await ImageInput.CreateAsync(path), Options = options }, progress));
            }
            finally { await service.UnloadAsync(); }
        }
        else if (request.Operation == "interpolate")
        {
            if (!video || request.Multiplier is < 2 or > 16) throw new ArgumentException("Select a video and multiplier between 2 and 16.");
            var service = services.GetRequiredService<IInterpolationService>();
            using var cancel = job.Token.Register(() => _ = service.CancelAsync());
            try
            {
                await service.LoadAsync(device);
                var source = await VideoInputStream.CreateAsync(path);
                result = (await service.ExecuteAsync(new() { VideoStream = source, Multiplier = request.Multiplier,
                    Frames = source.FrameCount, FrameRate = source.FrameRate }, progress)).SourceFile;
            }
            finally { await service.UnloadAsync(); }
        }
        else throw new ArgumentException("Unknown media operation.");
        job.Token.ThrowIfCancellationRequested();
        return [(await export.StoreAsync(result, job)).Id];
    }
    private async Task<string> SaveImage(ImageTensor tensor)
    {
        var path = Path.Combine(settings.DirectoryTemp, Guid.NewGuid().ToString("N") + ".png");
        await (await tensor.ToImageInputAsync()).SaveAsync(path);
        return path;
    }
    public static void EnsureHost(BackendType backend)
    {
        if (OperatingSystem.IsLinux() && backend == BackendType.HuggingFace)
            throw new PlatformNotSupportedException("The bundled HuggingFace Python host is Windows-only. On Linux, select a StableDiffusion.cpp or ONNX model.");
        var file = backend switch { BackendType.OnnxRuntime => "AmuseHost.OnnxRuntime.exe", BackendType.HuggingFace => "AmuseHost.HuggingFace.exe", _ => "AmuseHost.StableDiffusionCpp.exe" };
        if (!OperatingSystem.IsWindows()) file = Path.GetFileNameWithoutExtension(file);
        if (!File.Exists(Path.Combine(AppRuntime.DirectoryServer, file)))
            throw new FileNotFoundException($"Missing AI host: {file}. Build the host projects or use --host-dir to select an Amuse distribution.");
    }
}
