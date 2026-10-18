using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Amuse.Web;

public sealed record JobState(string Id, string Kind, string Status, DateTimeOffset Created,
    string Message = "", int Value = 0, int Maximum = 0, string[]? Files = null, string? Error = null,
    long PreviewVersion = 0, string? StreamText = null);

public sealed class JobContext(string id, JobQueue queue, CancellationToken token)
{
    public CancellationToken Token => token;
    public string Id => id;
    public Task PreviewAsync(Func<string, Task> write) => queue.PreviewAsync(id, write);
    public void Text(string text) => queue.Update(id, j => j with { StreamText = text });
    public void Progress(string message, int value = 0, int maximum = 0) => queue.Update(id, j => j with
        { Message = message, Value = value, Maximum = maximum });
}

public sealed class JobQueue : BackgroundService
{
    private sealed record Work(string Id, Func<JobContext, Task<string[]>> Execute);
    private readonly Channel<Work> channel = Channel.CreateBounded<Work>(new BoundedChannelOptions(64) { SingleReader = true });
    private readonly ConcurrentDictionary<string, JobState> states = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> cancellations = new();
    private readonly string directory;
    private readonly ILogger<JobQueue> logger;
    private readonly object gate = new();
    public JobQueue(WebData data, ILogger<JobQueue> logger)
    {
        this.logger = logger;
        directory = Path.Combine(data.Root, "Jobs");
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
            var state = JsonSerializer.Deserialize<JobState>(File.ReadAllText(path), WebJson.Options)!;
            if (state is null || !Guid.TryParseExact(state.Id, "N", out _) || Path.GetFileNameWithoutExtension(path) != state.Id)
                throw new JsonException("Invalid job manifest ID.");
            if (state.Status is "queued" or "running" or "canceling")
                state = state with { Status = "interrupted", Message = "Server stopped before this job completed. Submit again to retry." };
            states[state.Id] = state;
            WebJson.Save(path, state);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Quarantining corrupt job manifest {File}", Path.GetFileName(path));
                File.Move(path, path + ".corrupt", true);
            }
        }
    }
    public JobState[] List() => states.Values.OrderByDescending(j => j.Created).ToArray();
    public string PreviewPath(string id)
    {
        _ = Get(id);
        return Path.Combine(directory, id + ".png");
    }
    internal async Task PreviewAsync(string id, Func<string, Task> write)
    {
        var path = PreviewPath(id);
        var temporary = path + ".tmp.png";
        try
        {
            await write(temporary);
            File.Move(temporary, path, true);
            Update(id, j => j with { PreviewVersion = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }
        finally { File.Delete(temporary); }
    }
    public JobState Get(string id) => states.TryGetValue(id, out var state) ? state : throw new KeyNotFoundException("Job not found.");
    public bool IsBusy => states.Values.Any(x => x.Status is "queued" or "running" or "canceling");
    public JobState Enqueue(string kind, Func<JobContext, Task<string[]>> execute)
    {
        lock (gate)
        {
            var state = new JobState(Guid.NewGuid().ToString("N"), kind, "queued", DateTimeOffset.UtcNow);
            states[state.Id] = state;
            cancellations[state.Id] = new CancellationTokenSource();
            WebJson.Save(Path.Combine(directory, state.Id + ".json"), state);
            if (!channel.Writer.TryWrite(new(state.Id, execute)))
            {
                Update(state.Id, j => j with { Status = "failed", Error = "Queue is full." });
                cancellations.TryRemove(state.Id, out var cts); cts?.Dispose();
                throw new ArgumentException("Queue is full. Wait for pending jobs to finish.");
            }
            return state;
        }
    }
    internal void Update(string id, Func<JobState, JobState> change)
    {
        lock (gate)
        {
            var state = change(Get(id));
            states[id] = state;
            WebJson.Save(Path.Combine(directory, id + ".json"), state);
        }
    }
    public void Cancel(string id)
    {
        lock (gate)
        {
            var state = Get(id);
            if (state.Status is not ("queued" or "running" or "canceling")) return;
            Update(id, j => j with { Status = state.Status == "queued" ? "canceled" : "canceling" });
            if (cancellations.TryGetValue(id, out var cts)) cts.Cancel();
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in channel.Reader.ReadAllAsync(stoppingToken))
            {
                using var cts = cancellations[work.Id];
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stoppingToken);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    Update(work.Id, j => j with { Status = "running" });
                    var result = await work.Execute(new(work.Id, this, linked.Token));
                    linked.Token.ThrowIfCancellationRequested();
                    Update(work.Id, j => j with { Status = "completed", Files = result, Message = "Completed" });
                }
                catch (OperationCanceledException) { Update(work.Id, j => j with { Status = "canceled", Message = "Canceled" }); }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Job {Id} failed", work.Id);
                    Update(work.Id, j => j with { Status = "failed", Error = ex.Message });
                }
                finally { cancellations.TryRemove(work.Id, out _); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
