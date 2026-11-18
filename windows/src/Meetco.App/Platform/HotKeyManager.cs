using System.Windows.Interop;
using Meetco.Core.Infrastructure;

namespace MeetcoApp.Platform;

/// <summary>Global shortcuts. All use Ctrl + Alt + a letter, like Control + Option on the Mac.</summary>
public enum ShortcutAction
{
    ToggleListening = 1,
    WhatShouldISay,
    Recap,
    TogglePanel,
    ToggleCompact,
    ToggleEverything,
}

public static class ShortcutCatalog
{
    public static char Key(this ShortcutAction action) => action switch
    {
        ShortcutAction.ToggleListening => 'L',
        ShortcutAction.WhatShouldISay => 'A',
        ShortcutAction.Recap => 'R',
        ShortcutAction.TogglePanel => 'M',
        ShortcutAction.ToggleCompact => 'C',
        _ => 'H',
    };

    public static string Title(this ShortcutAction action) => action switch
    {
        ShortcutAction.ToggleListening => "Start / stop listening",
        ShortcutAction.WhatShouldISay => "What should I say?",
        ShortcutAction.Recap => "Recap so far",
        ShortcutAction.TogglePanel => "Show / hide panel",
        ShortcutAction.ToggleCompact => "Toggle compact mode",
        _ => "Show / hide Meetco",
    };

    public static string DisplayString(this ShortcutAction action) => "Ctrl+Alt+" + action.Key();

    public static IReadOnlyList<ShortcutAction> All { get; } = Enum.GetValues<ShortcutAction>();
}

/// <summary>Registers system-wide hotkeys on a hidden message-only window. UI thread only.</summary>
public sealed class HotKeyManager : IDisposable
{
    private HwndSource? _source;
    private readonly HashSet<int> _registered = [];

    public event Action<ShortcutAction>? ActionInvoked;

    /// <summary>Shortcuts that another app already owns (registration failed).</summary>
    public IReadOnlyList<ShortcutAction> Conflicts { get; private set; } = [];

    public void RegisterAll()
    {
        EnsureWindow();
        if (_source is null || _registered.Count > 0) return;
        var conflicts = new List<ShortcutAction>();
        foreach (var action in ShortcutCatalog.All)
        {
            var id = (int)action;
            var ok = NativeMethods.RegisterHotKey(_source.Handle, id,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, action.Key());
            if (ok) _registered.Add(id);
            else conflicts.Add(action);
        }
        Conflicts = conflicts;
        if (conflicts.Count > 0)
        {
            Log.Notice("hotkeys", "Shortcuts already in use by another app: " + string.Join(", ", conflicts.Select(c => c.DisplayString())));
        }
    }

    public void UnregisterAll()
    {
        if (_source is null) return;
        foreach (var id in _registered) NativeMethods.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private void EnsureWindow()
    {
        if (_source is not null) return;
        var parameters = new HwndSourceParameters("MeetcoHotKeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            // HWND_MESSAGE: a message-only window that is never shown.
            ParentWindow = new IntPtr(-3),
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (Enum.IsDefined(typeof(ShortcutAction), id))
            {
                handled = true;
                ActionInvoked?.Invoke((ShortcutAction)id);
            }
        }
        return IntPtr.Zero;
    }
}
