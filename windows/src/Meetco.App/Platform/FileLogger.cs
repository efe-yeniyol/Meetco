using System.Diagnostics;
using System.IO;
using System.Text;
using Meetco.Core.Infrastructure;

namespace MeetcoApp.Platform;

/// <summary>
/// Thread-safe log file in %LOCALAPPDATA%\Meetco\logs. Rolls over at 2 MB (one previous file kept).
/// Core never logs credentials or request bodies.
/// </summary>
public sealed class FileLogger
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _previousPath;

    public FileLogger(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "meetco.log");
        _previousPath = Path.Combine(directory, "meetco.previous.log");
    }

    public string FilePath => _path;

    public void Write(LogLevel level, string category, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {message}";
        Debug.WriteLine(line);
#if !DEBUG
        if (level == LogLevel.Debug) return;
#endif
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Copy(_path, _previousPath, overwrite: true);
                    File.Delete(_path);
                }
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
