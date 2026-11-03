using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sanova.Core;
using Sanova.Core.Content;
using Sanova.Core.Downloads;
using Sanova.Core.Game;
using Sanova.Core.Installation;
using Sanova.Core.Settings;
using Sanova.Core.Updates;
using Sanova.Launcher.Controls;
using Sanova.Launcher.Services;
using Sanova.Launcher.ViewModels;
using Sanova.Launcher.Views;
using Sanova.ManifestTool;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Sanova.Launcher.Tests.TestAppBuilder))]

namespace Sanova.Launcher.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        DuskScene.PlayIntro = false;
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions { DefaultFamilyName = Program.FontFamilyUri });
    }
}

public enum Stage
{
    FirstRun,
    NotPublished,
    ReadyToInstall,
    Installed,
    UpdateAvailable,
}

internal sealed class NoRegistry : IRegistryReader
{
    public string? GetString(RegHive hive, RegView view, string subKey, string valueName) => null;

    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, RegView view, string subKey) => [];
}

/// <summary>
/// A fully isolated launcher environment: temporary data folder, a fake original game, a
/// real content release built with the manifest tool, and optionally a real installation.
/// </summary>
public sealed class UiScenario : IDisposable
{
    private UiScenario(string root)
    {
        Root = root;
    }

    public string Root { get; }
    public ServiceProvider Services { get; private set; } = null!;
    public MainViewModel Shell => Services.GetRequiredService<MainViewModel>();
    public AppState State => Services.GetRequiredService<AppState>();

    public static async Task<UiScenario> CreateAsync(Stage stage, string language = "en", AppTheme theme = AppTheme.Dark)
    {
        var scenario = new UiScenario(Path.Combine(Path.GetTempPath(), "sanova-ui-tests", Guid.NewGuid().ToString("N")[..10]));
        Directory.CreateDirectory(scenario.Root);
        var game = Path.Combine(scenario.Root, "GTA San Andreas");
        var exe = new byte[8192];
        new Random(5).NextBytes(exe);
        Write(game, "gta_sa.exe", exe);
        foreach (var file in BaseGameReference.MinimalRequiredFiles) Write(game, file, new byte[2048 + file.Length]);
        Write(game, "data/handling.cfg", "original"u8.ToArray());

        var contentRoot = Path.Combine(scenario.Root, "Root");
        Write(contentRoot, "SANOVA.asi", "core"u8.ToArray());
        Write(contentRoot, "modloader/Roads HD/roads.txd", new byte[300_000]);
        Write(contentRoot, "modloader/Night Lights/lights.dat", "lights"u8.ToArray());
        Write(contentRoot, "modloader/Radio Pack/radio.txt", "radio"u8.ToArray());
        Write(contentRoot, "data/handling.cfg", "sanova handling"u8.ToArray());

        var releases = Path.Combine(scenario.Root, "releases");
        var v1 = await BuildReleaseAsync(scenario.Root, contentRoot, Path.Combine(releases, "v1"), "1.0.0", exe);
        var install = Path.Combine(scenario.Root, "SANOVA");

        if (stage is Stage.Installed or Stage.UpdateAvailable)
        {
            var manifest = await LoadAsync(v1);
            var checker = new CompatibilityChecker(GameVersionDatabase.LoadEmbedded(), [], NullLogger<CompatibilityChecker>.Instance);
            var report = await checker.CheckAsync(game, new CompatibilityCheckOptions(false, manifest.Manifest.Compatibility));
            var http = new HttpClient();
            var downloader = new ResumableDownloader(http, new BandwidthThrottle(), NullLogger<ResumableDownloader>.Instance);
            var service = new InstallationService(new ContentAcquirer(downloader, http, new BandwidthThrottle(), NullLogger<ContentAcquirer>.Instance),
                new BaseGameCopier(NullLogger<BaseGameCopier>.Instance), new TransactionEngine(NullLogger<TransactionEngine>.Instance),
                NullLogger<InstallationService>.Instance, "0.1.0");
            await service.InstallAsync(new InstallRequest
            {
                OriginalGamePath = game,
                InstallPath = install,
                Manifest = manifest,
                Components = ComponentResolver.InitialSelection(manifest.Manifest, null),
                Compatibility = report,
            }, null, CancellationToken.None);
        }

        var source = v1;
        if (stage == Stage.UpdateAvailable)
        {
            Write(contentRoot, "modloader/Roads HD/roads.txd", new byte[310_000]);
            Write(contentRoot, "cleo/traffic.cs", "traffic"u8.ToArray());
            source = await BuildReleaseAsync(scenario.Root, contentRoot, Path.Combine(releases, "v2"), "1.1.0", exe);
        }

        var paths = new AppPaths(Path.Combine(scenario.Root, "data"));
        paths.EnsureCreated();
        var settings = new LauncherSettings
        {
            Language = language,
            Theme = theme,
            SetupCompleted = stage != Stage.FirstRun,
            OriginalGamePath = stage == Stage.FirstRun ? null : game,
            InstallPath = stage == Stage.FirstRun ? null : install,
            CheckLauncherUpdatesOnStartup = false,
            // Without a published manifest nothing declares the fake test executable supported.
            AllowUnsupportedGameVersions = stage == Stage.NotPublished,
            DeveloperMode = stage != Stage.NotPublished,
            ContentSource = stage == Stage.NotPublished
                ? new ContentSourceOverride()
                : new ContentSourceOverride { Kind = ContentSourceKind.LocalFolder, Value = source },
        };
        Core.IO.AtomicFile.WriteJson(paths.SettingsFile, settings);

        scenario.Services = Bootstrapper.Build(paths, NullLoggerFactory.Instance,
            services => services.AddSingleton<IRegistryReader>(new NoRegistry()));
        await scenario.Shell.InitializeAsync();
        return scenario;
    }

    private static async Task<string> BuildReleaseAsync(string root, string contentRoot, string output, string version, byte[] exe)
    {
        var config = new
        {
            contentVersion = version,
            compatibility = new
            {
                additionalExecutables = new[]
                {
                    new { id = "test-1.0", label = "Test", edition = "retail", version = "1.0", region = "US", sha256 = new[] { Convert.ToHexStringLower(SHA256.HashData(exe)) }, support = "supported" },
                },
            },
            categories = new object[]
            {
                new { id = "core", order = 0 },
                new { id = "graphics", order = 1 },
                new { id = "audio", order = 2 },
            },
            releaseNotesFiles = new Dictionary<string, string> { ["en"] = "notes-en.md", ["tr"] = "notes-tr.md" },
            components = new object[]
            {
                new { id = "core", name = new { en = "SANOVA core", tr = "SANOVA çekirdeği" }, description = new { en = "Fixes, engine limits and the SANOVA configuration.", tr = "Düzeltmeler, motor sınırları ve SANOVA yapılandırması." }, category = "core", required = true, include = new[] { "**" }, redistribution = "permission", authors = new[] { "SANOVA team" } },
                new { id = "roads-hd", name = new { en = "HD roads", tr = "HD yollar" }, description = new { en = "Sharper road textures across the state.", tr = "Eyalet genelinde daha keskin yol dokuları." }, category = "graphics", include = new[] { "modloader/Roads HD/**" }, priority = 10, redistribution = "allowed", authors = new[] { "Example Author" }, license = "CC BY 4.0" },
                new { id = "night-lights", name = new { en = "Night lights", tr = "Gece ışıkları" }, description = new { en = "Warmer street lighting after dark.", tr = "Karanlıkta daha sıcak sokak aydınlatması." }, category = "graphics", include = new[] { "modloader/Night Lights/**" }, priority = 10, dependencies = new[] { "roads-hd" }, defaultEnabled = false, redistribution = "allowed" },
                new { id = "radio-pack", name = new { en = "Radio pack", tr = "Radyo paketi" }, category = "audio", include = new[] { "modloader/Radio Pack/**" }, priority = 10, redistribution = "allowed" },
            },
        };
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "notes-en.md"), "## Highlights\n- Sharper roads across Los Santos\n- **Night lights** are now optional\n\nThanks to everyone who tested this release.");
        await File.WriteAllTextAsync(Path.Combine(root, "notes-tr.md"), "## Öne çıkanlar\n- Los Santos genelinde daha keskin yollar\n- **Gece ışıkları** artık isteğe bağlı\n\nBu sürümü test eden herkese teşekkürler.");
        var configPath = Path.Combine(root, $"content-{version}.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));
        await ManifestBuilder.BuildAsync(new BuildOptions { SourceDirectory = contentRoot, ConfigPath = configPath, OutputDirectory = output, Log = TextWriter.Null });
        return output;
    }

    private static async Task<ResolvedManifest> LoadAsync(string folder)
    {
        var release = await new LocalFolderContentSource(folder, new ContentSourceConfig()).GetLatestAsync("stable", CancellationToken.None);
        return ManifestLoader.Load(await File.ReadAllBytesAsync(release!.ManifestUri.LocalPath), null, new ManifestTrustConfig(), release);
    }

    private static void Write(string root, string relative, byte[] content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public MainWindow CreateWindow()
    {
        var window = new MainWindow { DataContext = Shell, Width = 1200, Height = 780 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // Snapshots must show one page, not a cross-fade between two.
        foreach (var host in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.TransitioningContentControl>())
        {
            host.PageTransition = null;
        }
        return window;
    }

    public void Dispose()
    {
        try
        {
            Services.Dispose();
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class Snapshots
{
    public static string Folder
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SANOVA.Launcher.slnx"))) directory = directory.Parent;
            var folder = Path.Combine(directory!.FullName, "tests", "Sanova.Launcher.Tests", "snapshots");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static string Save(Avalonia.Controls.Window window, string name)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was rendered.");
        var path = Path.Combine(Folder, name + ".png");
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return path;
    }
}
