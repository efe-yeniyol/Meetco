using System.IO;
using System.Text.Json;
using System.Windows;

namespace MeetcoApp.Platform;

/// <summary>Saved positions of the floating windows (in device-independent pixels).</summary>
public sealed class SavedWindowState
{
    public double? ControlBarLeft { get; set; }
    public double? ControlBarTop { get; set; }
    public double? PanelLeft { get; set; }
    public double? PanelTop { get; set; }
    public double? PanelWidth { get; set; }
    public double? PanelHeight { get; set; }
}

/// <summary>Reads and writes %APPDATA%\Meetco\window.json.</summary>
public sealed class WindowStateStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public SavedWindowState Load()
    {
        try
        {
            if (File.Exists(filePath)) return JsonSerializer.Deserialize<SavedWindowState>(File.ReadAllText(filePath), Options) ?? new SavedWindowState();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return new SavedWindowState();
    }

    public void Save(SavedWindowState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>True when a point (top-left of a window) is on some screen, so the window can be restored there.</summary>
    public static bool IsOnScreen(double left, double top)
    {
        var x = SystemParameters.VirtualScreenLeft;
        var y = SystemParameters.VirtualScreenTop;
        return left >= x - 40 && top >= y - 10
            && left < x + SystemParameters.VirtualScreenWidth - 60
            && top < y + SystemParameters.VirtualScreenHeight - 30;
    }
}
