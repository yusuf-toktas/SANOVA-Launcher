using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Sanova.Core.IO;

namespace Sanova.Core.Diagnostics;

/// <summary>
/// Builds the zip a player attaches to a bug report: a readable summary, machine-readable
/// results, recent logs and settings — all passed through the <see cref="Redactor"/>.
/// The report is written in English so maintainers can read reports from every locale.
/// </summary>
public static class DiagnosticReport
{
    public static string BuildSummary(IReadOnlyList<DiagnosticCheck> checks, string launcherVersion, string informationalVersion, Redactor redactor)
    {
        var text = new StringBuilder();
        text.AppendLine("SANOVA Launcher diagnostic report");
        text.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        text.AppendLine(CultureInfo.InvariantCulture, $"Launcher: {launcherVersion} ({informationalVersion})");
        text.AppendLine();
        foreach (var check in checks)
        {
            var args = string.Join(", ", PlainArgs(check).Select(a => $"{a.Key}={Convert.ToString(a.Value, CultureInfo.InvariantCulture)}"));
            text.AppendLine(CultureInfo.InvariantCulture, $"[{check.Status,-7}] {check.Id,-16} {check.Code} {args}");
            if (!string.IsNullOrWhiteSpace(check.Detail))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"          {check.Detail}");
            }
        }
        return redactor.Redact(text.ToString());
    }

    public static async Task CreateAsync(
        string destinationZip,
        IReadOnlyList<DiagnosticCheck> checks,
        string launcherVersion,
        string informationalVersion,
        string logsDirectory,
        string? settingsFile,
        Redactor redactor,
        CancellationToken cancellationToken)
    {
        var temp = destinationZip + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                await AddTextAsync(archive, "report.txt", BuildSummary(checks, launcherVersion, informationalVersion, redactor), cancellationToken).ConfigureAwait(false);

                var json = JsonSerializer.Serialize(new
                {
                    generatedAt = DateTimeOffset.UtcNow,
                    launcherVersion,
                    informationalVersion,
                    checks = checks.Select(c => new
                    {
                        c.Id,
                        status = c.Status.ToString(),
                        c.Code,
                        args = PlainArgs(c).ToDictionary(a => a.Key, a => Convert.ToString(a.Value, CultureInfo.InvariantCulture)),
                        c.Detail,
                    }),
                }, JsonDefaults.Indented);
                await AddTextAsync(archive, "report.json", redactor.Redact(json), cancellationToken).ConfigureAwait(false);

                if (settingsFile is not null && File.Exists(settingsFile))
                {
                    await AddTextAsync(archive, "settings.json", redactor.Redact(await ReadSharedAsync(settingsFile, cancellationToken).ConfigureAwait(false)), cancellationToken)
                        .ConfigureAwait(false);
                }

                if (Directory.Exists(logsDirectory))
                {
                    foreach (var log in new DirectoryInfo(logsDirectory).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
                    {
                        var content = await ReadSharedAsync(log.FullName, cancellationToken).ConfigureAwait(false);
                        await AddTextAsync(archive, $"logs/{log.Name}", redactor.Redact(content), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            FileSystem.MoveFile(temp, destinationZip, overwrite: true);
        }
        catch
        {
            FileSystem.TryDeleteFile(temp);
            throw;
        }
    }

    /// <summary>Scalar arguments only; structured values (e.g. executable details) are already summarized in Detail.</summary>
    private static IEnumerable<KeyValuePair<string, object?>> PlainArgs(DiagnosticCheck check)
        => check.Args.Where(a => a.Value is null or string or IConvertible);

    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        // Log files are held open by the logger; share access explicitly.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddTextAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
