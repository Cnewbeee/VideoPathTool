namespace VideoPathTool.Core;

/// <summary>
/// A cooperative pause gate.
///
/// <see cref="CancellationToken"/> can only stop work, not suspend it, so the
/// long-running loops ask this object whether they may continue. Passing a
/// <c>null</c> PauseToken means "never paused", which keeps the console path
/// unchanged.
///
/// The gate is level-triggered rather than edge-triggered: <see cref="Pause"/>
/// can be called at any time, including before the loop reaches its next check,
/// and the loop will still block.
/// </summary>
public sealed class PauseToken : IDisposable
{
    private readonly ManualResetEventSlim _resume = new(true);
    private int _paused;
    private bool _disposed;

    /// <summary>True while the work should stay suspended.</summary>
    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    /// <summary>Suspends the work at its next checkpoint.</summary>
    public void Pause()
    {
        Volatile.Write(ref _paused, 1);
        try
        {
            _resume.Reset();
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose; the work is already gone.
        }
    }

    /// <summary>Lets the work continue.</summary>
    public void Resume()
    {
        Volatile.Write(ref _paused, 0);
        try
        {
            _resume.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Blocks while paused. Returns immediately when not paused, so the cost
    /// on the hot path is a single volatile read.
    /// </summary>
    /// <exception cref="OperationCanceledException">Thrown if cancelled while
    /// waiting, so a paused run can still be stopped.</exception>
    public void Wait(CancellationToken ct = default)
    {
        if (!IsPaused)
        {
            return;
        }
        while (IsPaused)
        {
            ct.ThrowIfCancellationRequested();
            _resume.Wait(100, ct);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _resume.Dispose();
    }
}
