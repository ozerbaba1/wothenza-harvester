using System.Collections.Concurrent;

namespace WothenzaHarvester;

public sealed class LogBus
{
    private readonly ConcurrentQueue<string> _lines = new();
    private const int Cap = 800;

    public void Add(string message)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss} {message}";
        _lines.Enqueue(line);
        while (_lines.Count > Cap && _lines.TryDequeue(out _)) { }
        Console.WriteLine(line);
    }

    public IReadOnlyList<string> Recent(int take = 200) =>
        _lines.Reverse().Take(take).Reverse().ToArray();
}
