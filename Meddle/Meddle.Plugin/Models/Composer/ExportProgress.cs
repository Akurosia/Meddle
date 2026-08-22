using System.Collections.Concurrent;

namespace Meddle.Plugin.Models.Composer;

public class ExportProgress
{
    public ExportProgress(int total, string? name)
    {
        Total = total;
        Name = name;
    }

    private int progress;
    public int Progress { get { return progress; } }
    public void IncrementProgress(int amount = 1)
    {
        Interlocked.Add(ref progress, amount);
        Parent?.IncrementProgress(amount);
    }
    public int Total;
    public bool IsComplete;
    public string? Name;
    public ExportProgress? Parent;
    
    public readonly ConcurrentBag<ExportProgress> Children = [];
}
