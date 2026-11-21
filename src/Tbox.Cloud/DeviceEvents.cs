namespace Tbox.Cloud;

internal sealed class DeviceEvents
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, HashSet<TaskCompletionSource>> watchers = [];

    internal async Task WatchAsync(Guid id, HttpContext context, DeviceSessions sessions, CancellationToken ct)
    {
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (!watchers.TryGetValue(id, out var listeners)) watchers[id] = listeners = [];
            listeners.Add(revoked);
        }
        try
        {
            context.Response.ContentType = "text/event-stream";
            do
            {
                if (await sessions.AuthenticateAsync(context, ct) is null) break;
                await context.Response.WriteAsync(": online\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(TimeSpan.FromSeconds(20), heartbeat.Token);
                if (await Task.WhenAny(revoked.Task, delay) == revoked.Task)
                {
                    await heartbeat.CancelAsync();
                    await context.Response.WriteAsync("event: revoked\ndata: {}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                    return;
                }
            } while (!ct.IsCancellationRequested);
        }
        finally
        {
            lock (gate)
            {
                var listeners = watchers[id];
                listeners.Remove(revoked);
                if (listeners.Count == 0) watchers.Remove(id);
            }
        }
    }

    internal void Revoke(Guid id)
    {
        lock (gate)
            if (watchers.TryGetValue(id, out var listeners))
                foreach (var listener in listeners) listener.TrySetResult();
    }
}
