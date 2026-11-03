using System.Collections.Concurrent;

namespace XWolf.NanoWolf.Testing;

/// <summary>Action log kept in memory, to check what was written.</summary>
public sealed class ListLog : IActionLog
{
    public ConcurrentQueue<ActionLogEntry> Entries { get; } = new();

    public Task WriteAsync(ActionLogEntry entry, CancellationToken cancel)
    {
        Entries.Enqueue(entry);
        return Task.CompletedTask;
    }
}
