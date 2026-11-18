namespace Meetco.Core.Infrastructure;

public enum LogLevel { Debug, Info, Notice, Error }

/// <summary>
/// Tiny logging facade. The app plugs in a sink; Core never logs credentials or request bodies.
/// </summary>
public static class Log
{
    public static Action<LogLevel, string, string>? Sink { get; set; }

    public static void Debug(string category, string message) => Sink?.Invoke(LogLevel.Debug, category, message);
    public static void Info(string category, string message) => Sink?.Invoke(LogLevel.Info, category, message);
    public static void Notice(string category, string message) => Sink?.Invoke(LogLevel.Notice, category, message);
    public static void Error(string category, string message) => Sink?.Invoke(LogLevel.Error, category, message);
}
