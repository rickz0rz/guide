using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Guide.Core.Logging;

public class ContainedLineLogger : ILogger
{
    // The listings load on another thread, so a lock protects the lines.
    private readonly Lock _lock = new();
    private readonly List<string> _lines = [];

    public long LinesLogged => Interlocked.Read(ref _linesLogged);

    public IReadOnlyList<string> GetLines()
    {
        lock (_lock)
        {
            return _lines.ToArray();
        }
    }

    private readonly int _maximumLineCount;

    private long _linesLogged;

    public ContainedLineLogger(int maximumLineCount = 50)
    {
        _maximumLineCount = maximumLineCount;
    }

    private void AddLine(string line)
    {
        lock (_lock)
        {
            while (_lines.Count >= _maximumLineCount)
            {
                _lines.RemoveAt(0);
            }
            _lines.Add(line);
            _linesLogged++;
        }
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        AddLine(formatter(state, exception));
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        // throw new NotImplementedException();
        return true;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        // throw new NotImplementedException();
        return null;
    }
}
