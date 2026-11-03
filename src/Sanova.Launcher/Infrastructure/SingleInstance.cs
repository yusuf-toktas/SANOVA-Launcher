namespace Sanova.Launcher.Infrastructure;

/// <summary>
/// Only one launcher may run per user session (two instances could otherwise modify the same
/// installation). A second start brings the existing window to the front instead.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\SANOVA.Launcher.Instance";
    private const string ActivationEventName = @"Local\SANOVA.Launcher.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, EventWaitHandle activation)
    {
        _mutex = mutex;
        _activation = activation;
    }

    /// <summary>Raised (on a background thread) when another instance asked us to come forward.</summary>
    public static event Action? ActivationRequested;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        var instance = new SingleInstance(mutex, activation);
        var thread = new Thread(instance.Listen) { IsBackground = true, Name = "SANOVA activation listener" };
        thread.Start();
        return instance;
    }

    public static void SignalExistingInstance()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var activation = EventWaitHandle.OpenExisting(ActivationEventName);
            activation.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is shutting down.
        }
    }

    private void Listen()
    {
        var handles = new[] { _activation, _stop.Token.WaitHandle };
        while (WaitHandle.WaitAny(handles) == 0)
        {
            ActivationRequested?.Invoke();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _activation.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread anymore.
        }
        _mutex.Dispose();
        _stop.Dispose();
    }
}
