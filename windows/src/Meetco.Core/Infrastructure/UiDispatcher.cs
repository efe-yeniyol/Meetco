namespace Meetco.Core.Infrastructure;

/// <summary>
/// Marshals work onto the thread that created a Core controller (the UI thread in the app).
/// Core's observable classes are single-threaded by contract: their public members must be
/// called on that thread, and background callbacks (audio, sockets) are posted back through this.
/// </summary>
public sealed class UiDispatcher
{
    private readonly SynchronizationContext? _context;

    public UiDispatcher(SynchronizationContext? context = null)
    {
        _context = context ?? SynchronizationContext.Current;
    }

    public void Post(Action action)
    {
        if (_context is null) action();
        else _context.Post(_ => action(), null);
    }
}
