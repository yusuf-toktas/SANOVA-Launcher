using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Sanova.Core.Settings;
using Sanova.Launcher.Infrastructure;
using Sanova.Launcher.Services;
using Sanova.Launcher.ViewModels;

namespace Sanova.Launcher.Tests;

public class PrimaryActionTests
{
    [AvaloniaFact]
    public async Task The_main_button_follows_the_installation_state()
    {
        using (var notPublished = await UiScenario.CreateAsync(Stage.NotPublished))
        {
            var home = notPublished.Services.GetService(typeof(HomeViewModel)) as HomeViewModel;
            Assert.Equal(PrimaryAction.Unavailable, home!.Action);
            Assert.False(home.ActionEnabled);
        }

        using (var ready = await UiScenario.CreateAsync(Stage.ReadyToInstall))
        {
            var home = (HomeViewModel)ready.Services.GetService(typeof(HomeViewModel))!;
            Assert.Equal(PrimaryAction.Install, home.Action);
            Assert.Equal("INSTALL", home.ActionLabel.Value);
        }

        using (var installed = await UiScenario.CreateAsync(Stage.Installed))
        {
            var home = (HomeViewModel)installed.Services.GetService(typeof(HomeViewModel))!;
            Assert.Equal(PrimaryAction.Play, home.Action);
            Assert.Equal("PLAY", home.ActionLabel.Value);
        }

        using (var update = await UiScenario.CreateAsync(Stage.UpdateAvailable, "tr"))
        {
            var home = (HomeViewModel)update.Services.GetService(typeof(HomeViewModel))!;
            Assert.Equal(PrimaryAction.Update, home.Action);
            Assert.Equal("GÜNCELLE", home.ActionLabel.Value);
            Assert.True(home.ShowPlayAnyway);
        }

        using (var firstRun = await UiScenario.CreateAsync(Stage.FirstRun))
        {
            Assert.True(firstRun.Shell.IsSetupVisible);
        }
    }
}

public class LocalizationUiTests
{
    [AvaloniaFact]
    public async Task Switching_language_refreshes_mod_credits_and_technical_details()
    {
        using var scenario = await UiScenario.CreateAsync(Stage.Installed);
        var about = (AboutViewModel)scenario.Services.GetService(typeof(AboutViewModel))!;
        var game = (GameViewModel)scenario.Services.GetService(typeof(GameViewModel))!;
        string? creditName = null;
        string? details = null;
        about.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AboutViewModel.ModCredits)) creditName = about.ModCredits[0].Name;
        };
        game.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GameViewModel.TechnicalDetails)) details = game.TechnicalDetails;
        };

        scenario.State.UpdateSettings(s => s.Language = "tr");

        Assert.Equal("SANOVA çekirdeği", creditName);
        var loc = (LocalizationService)scenario.Services.GetService(typeof(LocalizationService))!;
        Assert.StartsWith(loc.T("game.tech.file") + ":", details);
    }

    [AvaloniaFact]
    public async Task Switching_language_refreshes_content_release_notes()
    {
        using var scenario = await UiScenario.CreateAsync(Stage.UpdateAvailable);
        var updates = (UpdatesViewModel)scenario.Services.GetService(typeof(UpdatesViewModel))!;
        var notifications = new List<string?>();
        updates.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UpdatesViewModel.ContentNotes)) notifications.Add(updates.ContentNotes);
        };
        Assert.Contains("Highlights", updates.ContentNotes);

        scenario.State.UpdateSettings(s => s.Language = "tr");

        Assert.NotEmpty(notifications);
        Assert.Contains("Los Santos genelinde", notifications[^1]);
        Assert.Contains("Los Santos genelinde", updates.ContentNotes);
    }

    [AvaloniaFact]
    public async Task Switching_language_updates_visible_text_immediately()
    {
        using var scenario = await UiScenario.CreateAsync(Stage.Installed);
        var window = scenario.CreateWindow();
        var home = (HomeViewModel)scenario.Services.GetService(typeof(HomeViewModel))!;
        Assert.Equal("PLAY", home.ActionLabel.Value);
        Assert.Contains(window.GetVisualTexts(), t => t == "Settings");

        scenario.State.UpdateSettings(s => s.Language = "tr");

        Assert.Equal("OYNA", home.ActionLabel.Value);
        Assert.Contains(window.GetVisualTexts(), t => t == "Ayarlar");
        Assert.DoesNotContain(window.GetVisualTexts(), t => t == "Settings");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Theme_switch_changes_surface_colours()
    {
        using var scenario = await UiScenario.CreateAsync(Stage.Installed);
        var window = scenario.CreateWindow();
        Brush Background() => (Brush)window.FindResource(window.ActualThemeVariant, "Bg0")!;
        var dark = ((SolidColorBrush)Background()).Color;

        scenario.State.UpdateSettings(s => s.Theme = AppTheme.Light);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
        Assert.NotEqual(dark, ((SolidColorBrush)Background()).Color);
        window.Close();
    }
}

public class ContentSourceStateTests
{
    [AvaloniaFact]
    public async Task Disabling_developer_mode_revalidates_the_installed_manifest()
    {
        using var scenario = await UiScenario.CreateAsync(Stage.Installed);
        Assert.NotNull(scenario.State.InstalledManifest);

        scenario.State.UpdateSettings(s => s.DeveloperMode = false);

        Assert.Null(scenario.State.InstalledManifest);
    }

    [AvaloniaFact]
    public async Task An_in_flight_developer_check_cannot_restore_content_after_developer_mode_is_disabled()
    {
        using var handler = new DeferredManifestHandler();
        using var client = new HttpClient(handler);
        using var scenario = await UiScenario.CreateAsync(Stage.ReadyToInstall,
            overrides: services => services.AddSingleton(new ApiHttp(client)));
        var manifestBytes = scenario.State.LatestManifest!.Bytes;
        scenario.State.UpdateSettings(s => s.ContentSource = new ContentSourceOverride
        {
            Kind = ContentSourceKind.ManifestUrl,
            Value = "https://example.invalid/sanova-manifest.json",
        });
        var pending = scenario.State.CheckContentAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        scenario.State.UpdateSettings(s => s.DeveloperMode = false);
        handler.Response.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(manifestBytes),
        });
        await pending;

        Assert.Null(scenario.State.LatestManifest);
        Assert.Equal(ContentStatus.NotConfigured, scenario.State.ContentStatus);
    }

    [AvaloniaFact]
    public async Task Disabling_developer_mode_discards_the_developer_manifest()
    {
        using var scenario = await UiScenario.CreateAsync(Stage.ReadyToInstall);
        Assert.NotNull(scenario.State.LatestManifest);

        scenario.State.UpdateSettings(s => s.DeveloperMode = false);

        Assert.Null(scenario.State.LatestManifest);
        Assert.Null(scenario.State.ContentSource);
        Assert.Equal(ContentStatus.NotConfigured, scenario.State.ContentStatus);
        var home = (HomeViewModel)scenario.Services.GetService(typeof(HomeViewModel))!;
        Assert.Equal(PrimaryAction.Unavailable, home.Action);
    }

    [AvaloniaFact]
    public async Task Changing_channel_discards_the_previous_channels_manifest()
    {
        using var scenario = await UiScenario.CreateAsync(Stage.ReadyToInstall);
        Assert.NotNull(scenario.State.LatestManifest);

        scenario.State.UpdateSettings(s => s.UpdateChannel = "beta");

        Assert.Null(scenario.State.LatestManifest);
        Assert.Null(scenario.State.LastContentCheck);
        Assert.Equal(ContentStatus.Unknown, scenario.State.ContentStatus);
    }

    private sealed class DeferredManifestHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            Started.SetResult();
            return Response.Task.WaitAsync(cancellationToken);
        }
    }
}

public class ModalUiTests
{
    [AvaloniaFact]
    public async Task Modal_dialogs_disable_background_keyboard_targets()
    {
        using var scenario = await UiScenario.CreateAsync(Stage.Installed);
        var window = scenario.CreateWindow();
        var navigation = window.GetVisualDescendants().OfType<ListBox>().First();
        Assert.True(navigation.IsEffectivelyEnabled);

        var pending = scenario.Shell.Dialogs.AlertAsync("Test", "Test");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var disabled = !navigation.IsEffectivelyEnabled;
        scenario.Shell.Dialogs.Current!.CancelCommand.Execute(null);
        await pending;
        window.Close();

        Assert.True(disabled, "Keyboard users must not activate controls behind the modal dialog.");
    }
}

public class ModsUiTests
{
    [AvaloniaFact]
    public async Task Enabling_a_mod_pulls_in_its_dependency_and_shows_pending_changes()
    {
        using var scenario = await UiScenario.CreateAsync(Stage.Installed);
        var mods = (ModsViewModel)scenario.Services.GetService(typeof(ModsViewModel))!;
        var items = mods.Groups.SelectMany(g => g.Items).ToList();
        var roads = items.Single(i => i.Id == "roads-hd");
        var lights = items.Single(i => i.Id == "night-lights");
        roads.IsEnabled = false;
        Assert.False(roads.IsEnabled);

        lights.IsEnabled = true;

        Assert.True(roads.IsEnabled, "the dependency is switched back on");
        Assert.True(mods.HasPendingChanges);
        mods.DiscardCommand.Execute(null);
        Assert.False(mods.HasPendingChanges);
    }
}

/// <summary>Renders every page in representative states for visual review (snapshots/ folder, not committed).</summary>
public class SnapshotTests
{
    private static async Task RenderPagesAsync(Stage stage, string language, AppTheme theme, params string[] pages)
    {
        using var scenario = await UiScenario.CreateAsync(stage, language, theme);
        var window = scenario.CreateWindow();
        foreach (var page in pages)
        {
            scenario.Shell.Navigate(page);
            if (scenario.Shell.CurrentPage is { } vm) await vm.OnNavigatedToAsync();
            var path = Snapshots.Save(window, $"{stage}-{language}-{theme}-{page}".ToLowerInvariant());
            Assert.True(new FileInfo(path).Length > 10_000, $"{path} looks empty");
        }
        window.Close();
    }

    [AvaloniaFact]
    public Task Installed_english_dark() => RenderPagesAsync(Stage.Installed, "en", AppTheme.Dark,
        "home", "game", "mods", "activity", "updates", "settings", "diagnostics", "about");

    [AvaloniaFact]
    public Task Ready_to_install_turkish_dark() => RenderPagesAsync(Stage.ReadyToInstall, "tr", AppTheme.Dark, "home", "mods", "updates");

    [AvaloniaFact]
    public Task Update_available_turkish_light() => RenderPagesAsync(Stage.UpdateAvailable, "tr", AppTheme.Light, "home", "updates", "game");

    [AvaloniaFact]
    public Task Not_published_english_light() => RenderPagesAsync(Stage.NotPublished, "en", AppTheme.Light, "home", "mods");

    [AvaloniaFact]
    public async Task First_run_setup_steps()
    {
        using var scenario = await UiScenario.CreateAsync(Stage.FirstRun, "en", AppTheme.Dark);
        var window = scenario.CreateWindow();
        var setup = scenario.Shell.Setup!;
        Snapshots.Save(window, "setup-1-welcome");

        await setup.NextCommand.ExecuteAsync(null);
        Snapshots.Save(window, "setup-2-game");
        window.Close();
    }
}

internal static class VisualTextExtensions
{
    public static IReadOnlyList<string> GetVisualTexts(this Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
    }
}
