using System.Globalization;
using Microsoft.Extensions.Logging;
using Sanova.Core.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Sanova.Launcher.Infrastructure;

/// <summary>Serilog file logging (daily files, 14 days kept) plus an in-memory tail for the Diagnostics page.</summary>
public static class LoggingSetup
{
    public static LoggingLevelSwitch LevelSwitch { get; } = new(LogEventLevel.Information);

    public static RecentLogSink Recent { get; } = new(400);

    public static AppPaths Paths { get; } = AppPaths.CreateDefault();

    public static void Initialize()
    {
        try
        {
            Paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging falls back to the in-memory sink only.
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(Paths.LogsDirectory, "launcher-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(Recent)
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled exception");
                WriteCrashReport(ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    public static ILoggerFactory CreateFactory()
        => LoggerFactory.Create(builder => builder.AddSerilog(Log.Logger, dispose: false).SetMinimumLevel(LogLevel.Trace));

    public static void SetVerbose(bool verbose) => LevelSwitch.MinimumLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;

    public static void WriteCrashReport(Exception ex)
    {
        try
        {
            var path = Path.Combine(Paths.LogsDirectory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, $"SANOVA Launcher {AppInfo.InformationalVersion}{Environment.NewLine}{DateTimeOffset.Now:O}{Environment.NewLine}{ex}");
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done.
        }
    }
}

/// <summary>Keeps the most recent log lines in memory.</summary>
public sealed class RecentLogSink : ILogEventSink
{
    private readonly int _capacity;
    private readonly Queue<string> _lines = new();
    private readonly Lock _gate = new();

    public RecentLogSink(int capacity)
    {
        _capacity = capacity;
    }

    public void Emit(LogEvent logEvent)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{logEvent.Timestamp:HH:mm:ss} {LevelCode(logEvent.Level)} {logEvent.RenderMessage(CultureInfo.InvariantCulture)}");
        if (logEvent.Exception is not null) line += $" — {logEvent.Exception.GetType().Name}: {logEvent.Exception.Message}";
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity) _lines.Dequeue();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToList();
        }
    }

    private static string LevelCode(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        _ => "FTL",
    };
}
