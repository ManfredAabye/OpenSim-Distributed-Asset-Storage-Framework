namespace Microsoft.Extensions.Logging;

public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
    None
}

public readonly struct EventId
{
    public EventId(int id, string? name = null)
    {
        Id = id;
        Name = name;
    }

    public int Id { get; }
    public string? Name { get; }
}

public interface ILogger
{
    bool IsEnabled(LogLevel logLevel);
}

public interface ILogger<out TCategoryName> : ILogger
{
}

public interface ILoggerFactory
{
    ILogger CreateLogger(string categoryName);
}

public sealed class NullLoggerFactory : ILoggerFactory
{
    public static readonly NullLoggerFactory Instance = new();

    private NullLoggerFactory()
    {
    }

    public ILogger CreateLogger(string categoryName)
    {
        return NullLogger.Instance;
    }
}

public sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();

    private NullLogger()
    {
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return false;
    }
}

public sealed class NullLogger<TCategoryName> : ILogger<TCategoryName>
{
    public bool IsEnabled(LogLevel logLevel)
    {
        return false;
    }
}

public static class LoggerFactoryExtensions
{
    public static ILogger<T> CreateLogger<T>(this ILoggerFactory factory)
    {
        return new NullLogger<T>();
    }
}

public static class LoggerMessage
{
    public static Action<ILogger, T1, Exception?> Define<T1>(LogLevel logLevel, EventId eventId, string formatString)
    {
        return static (_, _, _) => { };
    }
}

public static class LoggerExtensions
{
    public static void LogDebug(this ILogger logger, string message, params object?[] args)
    {
    }

    public static void LogDebug(this ILogger logger, Exception exception, string message, params object?[] args)
    {
    }
}
