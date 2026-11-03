using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sanova.Core.IO;

namespace Sanova.Core.Game;

/// <summary>Starts the game executable from the SANOVA installation folder.</summary>
public sealed class GameLauncher
{
    private const int ErrorElevationRequired = 740;
    private const int ErrorCancelled = 1223;

    private readonly ILogger<GameLauncher> _logger;

    public GameLauncher(ILogger<GameLauncher> logger)
    {
        _logger = logger;
    }

    /// <returns>The process id, or null when the user cancelled an elevation prompt.</returns>
    public int? Launch(string installDirectory, string executableName, string? arguments)
    {
        var executable = SafePath.GetFullPathUnder(installDirectory, executableName);
        if (!File.Exists(executable))
        {
            throw SanovaException.Create(ErrorCodes.GameExecutableMissing, $"Executable not found: {executable}", ("path", executable));
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = installDirectory,
            UseShellExecute = true,
            Arguments = arguments?.Trim() ?? string.Empty,
        };

        try
        {
            using var process = Process.Start(startInfo);
            _logger.LogInformation("Started {Executable} (pid {Pid})", executable, process?.Id);
            return process?.Id;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _logger.LogInformation("Game launch cancelled at the elevation prompt");
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            throw SanovaException.Wrap(ErrorCodes.GameLaunchElevation, "The game requires elevation.", ex);
        }
        catch (Win32Exception ex)
        {
            throw SanovaException.Wrap(ErrorCodes.GameLaunchFailed, $"Could not start the game: {ex.Message}", ex, ("detail", ex.Message));
        }
    }
}
