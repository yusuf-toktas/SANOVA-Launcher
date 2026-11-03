using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Sanova.Core;
using Sanova.Core.Content;
using Sanova.Core.Game;
using Sanova.Core.IO;
using Sanova.Core.Updates;

namespace Sanova.ManifestTool;

/// <summary>sanova-manifest: maintainer tooling for SANOVA content releases.</summary>
public static class Program
{
    private const string Usage = """
        sanova-manifest — build and verify SANOVA content releases

        Commands:
          build        --source <Root dir> --config <sanova-content.json> --out <dir>
                       [--version <x.y.z>] [--channel stable|beta] [--max-package-mb <n>]
                       [--previous <old manifest> --previous-base-url <https url>]
                       [--sign-key <private.pem>] [--allow-unknown-redistribution]
          verify       --manifest <file> [--packages <dir>] [--public-key <public.pem>]
          keygen       --out <dir>
          sign         --manifest <file> --key <private.pem>
          basegame     --game <clean game dir> --out <file> [--executable-id <id>] [--description <text>]
          sync-check   --root <Root dir> --game <development install dir>
          init-config  --source <Root dir> --out <sanova-content.json>

        See docs/CONTENT-PIPELINE.md for the full workflow.
        """;

    public static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var options = ParseOptions(args.Skip(1).ToArray());
            return args[0] switch
            {
                "build" => await BuildAsync(options).ConfigureAwait(false),
                "verify" => await VerifyAsync(options).ConfigureAwait(false),
                "keygen" => KeyGen(options),
                "sign" => Sign(options),
                "basegame" => await BaseGameAsync(options).ConfigureAwait(false),
                "sync-check" => await SyncCheckAsync(options).ConfigureAwait(false),
                "init-config" => InitConfig(options),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or SanovaException or JsonException or InvalidDataException)
        {
            return Fail(ex.Message);
        }
    }

    internal static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            var name = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options[name] = hasValue ? args[++i] : null;
        }
        return options;
    }

    private static string Require(Dictionary<string, string?> options, string name)
        => options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"--{name} is required.");

    private static string? Optional(Dictionary<string, string?> options, string name) => options.GetValueOrDefault(name);

    private static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 1;
    }

    private static async Task<int> BuildAsync(Dictionary<string, string?> options)
    {
        var maxMb = Optional(options, "max-package-mb") is { } mb ? long.Parse(mb, CultureInfo.InvariantCulture) : 1024;
        var result = await ManifestBuilder.BuildAsync(new BuildOptions
        {
            SourceDirectory = Require(options, "source"),
            ConfigPath = Require(options, "config"),
            OutputDirectory = Require(options, "out"),
            VersionOverride = Optional(options, "version"),
            ChannelOverride = Optional(options, "channel"),
            MaxPackageBytes = maxMb * 1024 * 1024,
            PreviousManifestPath = Optional(options, "previous"),
            PreviousBaseUrl = Optional(options, "previous-base-url"),
            SigningKeyPath = Optional(options, "sign-key"),
            AllowUnknownRedistribution = options.ContainsKey("allow-unknown-redistribution"),
        }).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Built SANOVA {result.Manifest.ContentVersion}: {result.Manifest.Files.Count} file(s), {result.Manifest.Components.Count} component(s), {result.Manifest.Packages.Count} package(s).");
        Console.WriteLine("Redistribution summary:");
        foreach (var component in result.Manifest.Components)
        {
            Console.WriteLine($"  {component.Id,-28} {component.Redistribution,-11} {component.SourceKind,-9} {component.License ?? "license not recorded"}");
        }
        foreach (var warning in result.Warnings) Console.WriteLine("warning: " + warning);
        Console.WriteLine($"Release assets are listed in {Path.Combine(Path.GetDirectoryName(result.ManifestPath)!, "release-assets.txt")}");
        return 0;
    }

    private static async Task<int> VerifyAsync(Dictionary<string, string?> options)
    {
        var manifestPath = Require(options, "manifest");
        var bytes = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
        var manifest = ManifestLoader.Parse(bytes);
        Console.WriteLine($"Manifest {manifest.ContentVersion} is structurally valid ({manifest.Files.Count} files, {manifest.Packages.Count} packages).");
        var problems = 0;

        if (Optional(options, "public-key") is { } keyPath)
        {
            var signaturePath = manifestPath + ".sig";
            if (!File.Exists(signaturePath))
            {
                Console.WriteLine("Signature: MISSING");
                problems++;
            }
            else if (ManifestSignature.Verify(bytes, File.ReadAllText(signaturePath), [File.ReadAllText(keyPath)]))
            {
                Console.WriteLine("Signature: valid");
            }
            else
            {
                Console.WriteLine("Signature: INVALID");
                problems++;
            }
        }

        if (Optional(options, "packages") is { } packagesDirectory)
        {
            foreach (var package in manifest.Packages)
            {
                var path = Path.Combine(packagesDirectory, package.FileName);
                if (!File.Exists(path))
                {
                    Console.WriteLine(package.Url is null ? $"  MISSING  {package.FileName}" : $"  remote   {package.FileName} ({package.Url})");
                    if (package.Url is null) problems++;
                    continue;
                }
                var sha = await FileHashing.Sha256FileAsync(path).ConfigureAwait(false);
                if (new FileInfo(path).Length != package.Size || !FileHashing.HashEquals(sha, package.Sha256))
                {
                    Console.WriteLine($"  CORRUPT  {package.FileName}");
                    problems++;
                    continue;
                }

                using var archive = ZipFile.OpenRead(path);
                var entries = archive.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'), e => e, StringComparer.OrdinalIgnoreCase);
                foreach (var file in manifest.Files.Where(f => string.Equals(f.Package, package.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!entries.TryGetValue(file.EntryName, out var entry))
                    {
                        Console.WriteLine($"  MISSING  {file.EntryName} in {package.FileName}");
                        problems++;
                        continue;
                    }
                    await using var stream = entry.Open();
                    var fileSha = await FileHashing.Sha256StreamAsync(stream).ConfigureAwait(false);
                    if (entry.Length != file.Size || !FileHashing.HashEquals(fileSha, file.Sha256))
                    {
                        Console.WriteLine($"  CORRUPT  {file.EntryName} in {package.FileName}");
                        problems++;
                    }
                }
                Console.WriteLine($"  ok       {package.FileName}");
            }
        }

        Console.WriteLine(problems == 0 ? "Verification passed." : $"Verification found {problems} problem(s).");
        return problems == 0 ? 0 : 2;
    }

    private static int KeyGen(Dictionary<string, string?> options)
    {
        var output = Require(options, "out");
        Directory.CreateDirectory(output);
        var privatePath = Path.Combine(output, "sanova-manifest-private.pem");
        var publicPath = Path.Combine(output, "sanova-manifest-public-key.pem");
        if (File.Exists(privatePath)) throw new InvalidOperationException($"{privatePath} already exists; refusing to overwrite a signing key.");
        var (privateKey, publicKey) = ManifestSignature.GenerateKeyPair();
        File.WriteAllText(privatePath, privateKey);
        File.WriteAllText(publicPath, publicKey);
        Console.WriteLine($"Private key: {privatePath}  (keep secret; never commit it)");
        Console.WriteLine($"Public key:  {publicPath}  (fingerprint {ManifestSignature.Fingerprint(publicKey)})");
        Console.WriteLine("Add the public key to src/Sanova.Launcher/distribution.json → trust.publicKeys.");
        return 0;
    }

    private static int Sign(Dictionary<string, string?> options)
    {
        var manifestPath = Require(options, "manifest");
        var bytes = File.ReadAllBytes(manifestPath);
        ManifestLoader.Parse(bytes);
        var signature = ManifestSignature.Sign(bytes, File.ReadAllText(Require(options, "key")));
        File.WriteAllText(manifestPath + ".sig", signature);
        Console.WriteLine($"Wrote {manifestPath}.sig");
        return 0;
    }

    private static async Task<int> BaseGameAsync(Dictionary<string, string?> options)
    {
        var game = SafePath.NormalizeRoot(Require(options, "game"));
        var output = Require(options, "out");
        var executableId = Optional(options, "executable-id");
        if (executableId is null)
        {
            var identity = await ExecutableIdentifier.IdentifyAsync(game, GameVersionDatabase.LoadEmbedded()).ConfigureAwait(false);
            executableId = identity.Match?.Id ?? throw new InvalidOperationException("Could not identify the executable; pass --executable-id.");
            Console.WriteLine($"Executable: {identity.Match.Label}");
        }

        var reference = new BaseGameReference
        {
            ExecutableIds = [executableId],
            Description = Optional(options, "description") ?? $"Clean installation file list for {executableId}",
        };
        var files = BaseGameCopierFiles(game);
        long total = 0;
        foreach (var (path, size) in files)
        {
            var sha = await FileHashing.Sha256FileAsync(Path.Combine(game, path)).ConfigureAwait(false);
            reference.Files.Add(new BaseGameFile { Path = path, Size = size, Sha256 = sha });
            total += size;
        }
        AtomicFile.WriteJson(output, reference);
        Console.WriteLine($"Wrote {reference.Files.Count} file(s), {total:N0} bytes, to {output}");
        return 0;
    }

    private static List<(string Path, long Size)> BaseGameCopierFiles(string game)
        => new DirectoryInfo(game)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(f => (Path: Path.GetRelativePath(game, f.FullName).Replace('\\', '/'), f.Length))
            .Where(f => !f.Path.StartsWith(".sanova/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

    private static async Task<int> SyncCheckAsync(Dictionary<string, string?> options)
    {
        var root = SafePath.NormalizeRoot(Require(options, "root"));
        var game = SafePath.NormalizeRoot(Require(options, "game"));
        var rootFiles = BaseGameCopierFiles(root);
        var missing = new List<string>();
        var different = new List<string>();
        foreach (var (path, size) in rootFiles)
        {
            var target = Path.Combine(game, path);
            if (!File.Exists(target))
            {
                missing.Add(path);
                continue;
            }
            if (new FileInfo(target).Length != size ||
                !FileHashing.HashEquals(await FileHashing.Sha256FileAsync(Path.Combine(root, path)).ConfigureAwait(false), await FileHashing.Sha256FileAsync(target).ConfigureAwait(false)))
            {
                different.Add(path);
            }
        }

        Console.WriteLine($"Root files checked: {rootFiles.Count}");
        foreach (var path in missing) Console.WriteLine($"  missing in development install: {path}");
        foreach (var path in different) Console.WriteLine($"  differs from development install: {path}");
        Console.WriteLine(missing.Count + different.Count == 0
            ? "Root and the development installation are in sync."
            : $"{missing.Count} missing, {different.Count} different.");
        return missing.Count + different.Count == 0 ? 0 : 2;
    }

    private static int InitConfig(Dictionary<string, string?> options)
    {
        var source = SafePath.NormalizeRoot(Require(options, "source"));
        var output = Require(options, "out");
        if (File.Exists(output)) throw new InvalidOperationException($"{output} already exists.");

        var config = new ContentConfig
        {
            ContentVersion = "0.1.0",
            Title = new LocalizedText([new("en", "SANOVA — San Andreas Reimagined"), new("tr", "SANOVA — San Andreas Reimagined")]),
            Compatibility = new CompatibilityRules { SupportedExecutables = ["sa-1.0-us-retail"] },
            Components =
            [
                new ComponentConfig
                {
                    Id = "core",
                    Name = new LocalizedText([new("en", "SANOVA core"), new("tr", "SANOVA çekirdeği")]),
                    Description = new LocalizedText([new("en", "Required SANOVA files."), new("tr", "Gerekli SANOVA dosyaları.")]),
                    Category = "core",
                    Required = true,
                    Include = ["**"],
                    Redistribution = Redistribution.Unknown,
                },
            ],
        };

        if (Directory.Exists(Path.Combine(source, "modloader")))
        {
            foreach (var mod in Directory.EnumerateDirectories(Path.Combine(source, "modloader")))
            {
                var name = Path.GetFileName(mod);
                if (name.StartsWith('.')) continue;
                var id = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
                if (id.Length == 0) continue;
                config.Components.Add(new ComponentConfig
                {
                    Id = id,
                    Name = LocalizedText.English(name),
                    Category = "general",
                    Include = [$"modloader/{name}/**"],
                    Priority = 10,
                    Redistribution = Redistribution.Unknown,
                });
            }
        }

        AtomicFile.WriteJson(output, config);
        Console.WriteLine($"Wrote {output} with {config.Components.Count} component(s). Review names, licenses and redistribution status before building.");
        return 0;
    }
}
