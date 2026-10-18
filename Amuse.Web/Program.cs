using Amuse.App;
using Amuse.App.Services;
using Amuse.Web;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using System.Net;
using TensorStack.WPF.Services;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine(ServerOptions.Usage);
    return 0;
}
try
{
    var options = ServerOptions.Parse(args);
    // Existing AI hosts use fixed named-pipe names. Until those are namespaced,
    // reserve the same instance marker as the desktop app to prevent cross-talk.
    using var instance = new Mutex(false, "Global\\TensorStack_Amuse", out var isNew);
    if (!isNew) throw new InvalidOperationException("Amuse desktop or another Web server is running. Close it before starting this server.");
    Directory.CreateDirectory(Path.Combine(options.DataDirectory, "Logs"));
    Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
        .WriteTo.File(Path.Combine(options.DataDirectory, "Logs", "web-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
        .CreateLogger();
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = [], ContentRootPath = AppContext.BaseDirectory,
        WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
    });
    builder.WebHost.ConfigureKestrel(server =>
    {
        // Configuration-defined Kestrel endpoints must not add a LAN listener.
        server.Configure(new ConfigurationBuilder().Build());
        server.ListenLocalhost(options.Port);
        server.Limits.MaxRequestBodySize = 512L * 1024 * 1024;
    });
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));
    builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 512L * 1024 * 1024);
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    builder.Logging.AddSerilog(Log.Logger, dispose: true);
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<WebData>();
    builder.Services.AddSingleton(sp => sp.GetRequiredService<WebData>().Settings);
    builder.Services.AddSingleton<FileStore>();
    builder.Services.AddSingleton<JobQueue>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<JobQueue>());
    builder.Services.AddSingleton<IHardwareService, HardwareService>();
    builder.Services.AddSingleton<IMediaService, MediaService>();
    builder.Services.AddSingleton<IEnvironmentService, EnvironmentService>();
    builder.Services.AddSingleton<IHttpService, HttpService>();
    builder.Services.AddSingleton<IToolService, ToolService>();
    builder.Services.AddSingleton<IPreviewService, PreviewService>();
    builder.Services.AddSingleton<IGenerateService, GenerateService>();
    builder.Services.AddSingleton<IUpscaleService, UpscaleService>();
    builder.Services.AddSingleton<IExtractService, ExtractService>();
    builder.Services.AddSingleton<IInterpolationService, InterpolationService>();
    builder.Services.AddSingleton<DownloadService>();
    builder.Services.AddSingleton<Engine>();
    builder.Services.AddSingleton<ConfigurationService>();
    builder.Services.AddSingleton<CompositionService>();
    builder.Services.AddSingleton<MediaExport>();
    await using var app = builder.Build();
    var data = app.Services.GetRequiredService<WebData>();
    _ = app.Services.GetRequiredService<FileStore>();
    var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    app.Use(async (context, next) =>
    {
        var host = context.Request.Host;
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            host.Host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1") || (host.Port ?? 80) != options.Port)
        { context.Response.StatusCode = 403; return; }
        var origin = context.Request.Headers.Origin.ToString();
        var validOrigin = origin.Length == 0 || (Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
            originUri.Scheme == "http" && originUri.UserInfo.Length == 0 &&
            originUri.Host.Equals(host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) && originUri.Port == options.Port);
        if (!validOrigin || context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
        { context.Response.StatusCode = 403; return; }
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; media-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'";
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Method is not ("GET" or "HEAD"))
            {
                var supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-Amuse-Token"].ToString());
                if (!CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(csrf)))
                { context.Response.StatusCode = 403; return; }
            }
        }
        try { await next(); }
        catch (Exception ex) when (!context.Response.HasStarted && ex is not OperationCanceledException)
        {
            var status = ex switch
            {
                FileNotFoundException or KeyNotFoundException => 404,
                ArgumentException or JsonException or InvalidDataException or BadHttpRequestException => 400,
                InvalidOperationException => 409,
                _ => 500
            };
            if (status == 500) app.Logger.LogError(ex, "Request failed");
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { error = status == 500 ? "Internal error. See server logs." : ex.Message });
        }
    });
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapGet("/api/session", () => new { token = csrf });
    app.MapGet("/api/health", () => new { status = "ok", port = options.Port, localOnly = true, version = AppRuntime.AppVersionDisplay });
    app.MapPost("/api/server/stop", (IHostApplicationLifetime lifetime) =>
    {
        lifetime.StopApplication();
        return Results.Accepted();
    });
    app.MapGet("/api/catalog", (Engine engine) => engine.Catalog());
    app.MapGet("/api/jobs", (JobQueue queue) => queue.List());
    app.MapGet("/api/jobs/{id}", (string id, JobQueue queue) => queue.Get(id));
    app.MapGet("/api/jobs/{id}/preview", (string id, JobQueue queue) => Results.File(queue.PreviewPath(id), "image/png"));
    app.MapPost("/api/jobs/{id}/cancel", (string id, JobQueue queue) => { queue.Cancel(id); return Results.Ok(); });
    app.MapPost("/api/generate", (GenerateRequest request, Engine engine, JobQueue queue) =>
    {
        engine.Validate(request);
        var job = queue.Enqueue(request.View, context => engine.GenerateAsync(request, context));
        return Results.Accepted($"/api/jobs/{job.Id}", job);
    });
    app.MapPost("/api/process", (ProcessRequest request, Engine engine, FileStore files, JobQueue queue) =>
    {
        files.Get(request.File);
        if (request.Operation is not ("upscale" or "extract" or "interpolate")) throw new ArgumentException("Unknown operation.");
        var job = queue.Enqueue(request.Operation, context => engine.ProcessAsync(request, context));
        return Results.Accepted($"/api/jobs/{job.Id}", job);
    });
    app.MapGet("/api/files", (FileStore files) => files.List());
    app.MapGet("/api/trash", (FileStore files) => files.Trash());
    app.MapDelete("/api/files/{id}", (string id, FileStore files) => { files.Delete(id); return Results.Ok(); });
    app.MapPost("/api/trash/{id}/restore", (string id, FileStore files) => { files.Restore(id); return Results.Ok(); });
    app.MapPost("/api/compose", (CompositionRequest request, CompositionService composition, JobQueue queue) =>
    {
        composition.Validate(request);
        var job = queue.Enqueue("VideoCompose", context => composition.ExportAsync(request, context));
        return Results.Accepted($"/api/jobs/{job.Id}", job);
    });
    app.MapGet("/api/files/{id}", (string id, bool? download, FileStore files) =>
    {
        var meta = files.Get(id);
        return Results.File(files.PathFor(id), meta.ContentType, fileDownloadName: download == true ? meta.Name : null, enableRangeProcessing: true);
    });
    app.MapPost("/api/files", async (HttpRequest request, FileStore files, CancellationToken ct) =>
    {
        if (!request.HasFormContentType) throw new ArgumentException("Expected multipart form data.");
        var form = await request.ReadFormAsync(ct);
        if (form.Files.Count == 0) throw new ArgumentException("Select a file.");
        var results = new List<StoredFile>();
        foreach (var file in form.Files)
        {
            await using var stream = file.OpenReadStream();
            results.Add(await files.AddAsync(stream, file.FileName, ct));
        }
        return Results.Ok(results);
    }).DisableAntiforgery(); // Session header plus strict Host/Origin validation above covers this endpoint too.
    app.MapGet("/api/config/{section}", (string section, ConfigurationService config) => config.Get(section));
    app.MapPut("/api/config/{section}", (string section, JsonElement value, ConfigurationService config, JobQueue queue) =>
    {
        var job = queue.Enqueue($"settings:{section}", context => config.SaveAsync(section, value, context));
        return Results.Accepted($"/api/jobs/{job.Id}", job);
    });
    app.MapPost("/api/environments/{index:int}/{operation}", (int index, string operation, ConfigurationService config, JobQueue queue) =>
    {
        config.ValidateEnvironment(index, operation);
        var job = queue.Enqueue($"environment:{operation}", context => config.EnvironmentAsync(index, operation, context));
        return Results.Accepted($"/api/jobs/{job.Id}", job);
    });
    app.MapPost("/api/models/{kind}/{id:int}/download", (string kind, int id, Engine engine, ConfigurationService config, JobQueue queue) =>
    {
        engine.Model(kind, id);
        var job = queue.Enqueue("download", context => config.DownloadAsync(kind, id, context));
        return Results.Accepted($"/api/jobs/{job.Id}", job);
    });
    Console.WriteLine($"Amuse Web: http://localhost:{options.Port}\nData: {options.DataDirectory}\nStop: Ctrl+C");
    await app.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Amuse Web could not start: {ex.Message}");
    Console.Error.WriteLine(ServerOptions.Usage);
    return 1;
}
