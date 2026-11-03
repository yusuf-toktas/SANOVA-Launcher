using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Sanova.Core.IO;

namespace Sanova.Core.Game;

public sealed record RunningGameProcess(int ProcessId, string? ExecutablePath);

public sealed record GameRunState(IReadOnlyList<RunningGameProcess> Processes, bool IsSanovaRunning)
{
    public static GameRunState NotRunning { get; } = new([], false);

    public bool IsAnyRunning => Processes.Count > 0;
}

/// <summary>Polls for running game processes so installs and updates never touch files in use.</summary>
public sealed class GameProcessMonitor : IAsyncDisposable, IDisposable
{
    private static readonly string[] ProcessNames = ["gta_sa", "gta-sa"];

    private readonly ILogger<GameProcessMonitor> _logger;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _loop;
    private Task? _loopTask;

    public GameProcessMonitor(ILogger<GameProcessMonitor> logger, TimeSpan? interval = null)
    {
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public event EventHandler<GameRunState>? StateChanged;

    public string? InstallDirectory { get; set; }

    public GameRunState Current { get; private set; } = GameRunState.NotRunning;

    public void Start()
    {
        if (_loop is not null) return;
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _loopTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_interval);
            do
            {
                try
                {
                    Poll();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Process poll failed");
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, token);
    }

    public GameRunState Poll()
    {
        var processes = new List<RunningGameProcess>();
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    processes.Add(new RunningGameProcess(process.Id, TryGetExecutablePath(process.Id)));
                }
            }
        }

        var installDirectory = InstallDirectory;
        var sanovaRunning = installDirectory is not null && processes.Any(p =>
            p.ExecutablePath is not null && SafePath.IsStrictlyUnder(installDirectory, p.ExecutablePath));

        var state = new GameRunState(processes, sanovaRunning);
        var changed = state.IsAnyRunning != Current.IsAnyRunning
                      || state.IsSanovaRunning != Current.IsSanovaRunning
                      || !state.Processes.Select(p => p.ProcessId).SequenceEqual(Current.Processes.Select(p => p.ProcessId));
        Current = state;
        if (changed)
        {
            _logger.LogInformation("Game process state: running={Running}, sanova={Sanova}", state.IsAnyRunning, state.IsSanovaRunning);
            StateChanged?.Invoke(this, state);
        }
        return state;
    }

    private static string? TryGetExecutablePath(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return QueryImagePath(processId);
    }

    [SupportedOSPlatform("windows")]
    private static string? QueryImagePath(int processId)
    {
        const uint processQueryLimitedInformation = 0x1000;
        var handle = OpenProcess(processQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var capacity = 1024;
            var buffer = new StringBuilder(capacity);
            return QueryFullProcessImageName(handle, 0, buffer, ref capacity) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public void Dispose()
    {
        if (_loop is null) return;
        _loop.Cancel();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfaces here; the loop is stopped either way.
        }
        _loop.Dispose();
        _loop = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop is null) return;
        await _loop.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loopTask is not null) await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _loop.Dispose();
        _loop = null;
    }
}
