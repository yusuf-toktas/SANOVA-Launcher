using System.Text;
using Sanova.Core.Content;
using Sanova.Core.Updates;

namespace Sanova.Core.Tests;

public static class ManifestSamples
{
    public static readonly string Hash = new('a', 64);

    public static ContentManifest Valid() => new()
    {
        SchemaVersion = 1,
        ContentVersion = "1.0.0",
        Components =
        [
            new ComponentSpec { Id = "core", Name = LocalizedText.English("Core"), Required = true },
            new ComponentSpec { Id = "hd", Name = LocalizedText.English("HD"), Dependencies = ["core"] },
            new ComponentSpec { Id = "lowres", Name = LocalizedText.English("Low"), Conflicts = ["hd"], DefaultEnabled = false },
            new ComponentSpec { Id = "extra", Name = LocalizedText.English("Extra"), Dependencies = ["hd"], DefaultEnabled = false },
        ],
        Packages = [new PackageSpec { Id = "core", FileName = "core.zip", Size = 10, Sha256 = Hash }],
        Files =
        [
            new ManifestFile { Path = "SANOVA.asi", Size = 1, Sha256 = Hash, Component = "core", Package = "core" },
            new ManifestFile { Path = "modloader/hd/a.txd", Size = 1, Sha256 = Hash, Component = "hd", Package = "core" },
        ],
    };
}

public class ManifestValidatorTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("{\"schemaVersion\":1,\"contentVersion\":\"1.0.0\",\"components\":null}")]
    [InlineData("{\"schemaVersion\":1,\"contentVersion\":\"1.0.0\",\"files\":[null]}")]
    [InlineData("{\"schemaVersion\":1,\"contentVersion\":\"1.0.0\",\"components\":[{\"id\":\"core\",\"name\":null,\"dependencies\":null}]}")]
    public void Malformed_manifest_shapes_report_a_manifest_error(string json)
    {
        var error = Assert.Throws<SanovaException>(() => ManifestLoader.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(ErrorCodes.ManifestInvalid, error.Code);
    }

    [Fact]
    public void Accepts_a_valid_manifest() => Assert.Empty(ManifestValidator.Validate(ManifestSamples.Valid()));

    [Fact]
    public void Rejects_unsafe_and_duplicate_paths()
    {
        var manifest = ManifestSamples.Valid();
        manifest.Files.Add(new ManifestFile { Path = "../evil.dll", Size = 1, Sha256 = ManifestSamples.Hash, Component = "core", Package = "core" });
        manifest.Files.Add(new ManifestFile { Path = "sanova.ASI", Size = 1, Sha256 = ManifestSamples.Hash, Component = "core", Package = "core" });
        manifest.Files.Add(new ManifestFile { Path = ".sanova/install.json", Size = 1, Sha256 = ManifestSamples.Hash, Component = "core", Package = "core" });
        var errors = ManifestValidator.Validate(manifest);
        Assert.Contains(errors, e => e.Contains("unsafe", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Duplicate file path", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("metadata folder", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_unknown_references_bad_hashes_and_cycles()
    {
        var manifest = ManifestSamples.Valid();
        manifest.Files.Add(new ManifestFile { Path = "x.txt", Size = 1, Sha256 = "zz", Component = "ghost", Package = "nope" });
        manifest.Components[0].Dependencies.Add("extra");
        var errors = ManifestValidator.Validate(manifest);
        Assert.Contains(errors, e => e.Contains("invalid sha256", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unknown component 'ghost'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_file_and_folder_collisions()
    {
        var manifest = ManifestSamples.Valid();
        manifest.Files.Add(new ManifestFile { Path = "modloader/hd", Size = 1, Sha256 = ManifestSamples.Hash, Component = "core", Package = "core" });
        Assert.Contains(ManifestValidator.Validate(manifest), e => e.Contains("both as a file and as a folder", StringComparison.Ordinal));
    }

    [Fact]
    public void Requires_https_and_hashes_for_external_sources()
    {
        var manifest = ManifestSamples.Valid();
        manifest.Components.Add(new ComponentSpec
        {
            Id = "ext",
            Name = LocalizedText.English("External"),
            Source = new ComponentSource { Type = ComponentSourceKind.External, Url = "http://example.invalid/mod.zip" },
        });
        var errors = ManifestValidator.Validate(manifest);
        Assert.Contains(errors, e => e.Contains("without a valid sha256", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("absolute https url", StringComparison.Ordinal));
    }

    [Fact]
    public void Newer_schema_versions_require_a_newer_launcher()
    {
        var ex = Assert.Throws<SanovaException>(() => ManifestLoader.Parse(Encoding.UTF8.GetBytes("""{ "schemaVersion": 99 }""")));
        Assert.Equal(ErrorCodes.ManifestSchemaUnsupported, ex.Code);
    }

    [Fact]
    public void Localized_text_accepts_strings_and_maps()
    {
        var json = """{ "schemaVersion": 1, "contentVersion": "1.0.0", "title": "Plain", "releaseNotes": { "en": "Notes", "tr": "Notlar" } }""";
        var manifest = ManifestLoader.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("Plain", manifest.Title!["en"]);
        Assert.Equal("Notlar", manifest.ReleaseNotes!["tr"]);
    }
}

public class ManifestSignatureTests
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("""{ "schemaVersion": 1, "contentVersion": "1.0.0" }""");

    [Fact]
    public void Valid_signatures_verify_and_tampering_is_detected()
    {
        var (privateKey, publicKey) = ManifestSignature.GenerateKeyPair();
        var signature = ManifestSignature.Sign(Bytes, privateKey);
        Assert.True(ManifestSignature.Verify(Bytes, signature, [publicKey]));

        var tampered = (byte[])Bytes.Clone();
        tampered[^3] ^= 0x01;
        Assert.False(ManifestSignature.Verify(tampered, signature, [publicKey]));

        var (_, otherPublic) = ManifestSignature.GenerateKeyPair();
        Assert.False(ManifestSignature.Verify(Bytes, signature, [otherPublic]));
        Assert.False(ManifestSignature.Verify(Bytes, "not base64!", [publicKey]));
    }

    [Fact]
    public void Loader_enforces_the_trust_policy()
    {
        var (privateKey, publicKey) = ManifestSignature.GenerateKeyPair();
        var signature = ManifestSignature.Sign(Bytes, privateKey);
        var trust = new ManifestTrustConfig { PublicKeys = [publicKey], RequireSignature = true };

        Assert.True(ManifestLoader.Load(Bytes, signature, trust, null).SignatureVerified);
        Assert.Equal(ErrorCodes.ManifestSignatureMissing, Assert.Throws<SanovaException>(() => ManifestLoader.Load(Bytes, null, trust, null)).Code);

        var (otherPrivate, _) = ManifestSignature.GenerateKeyPair();
        var forged = ManifestSignature.Sign(Bytes, otherPrivate);
        Assert.Equal(ErrorCodes.ManifestSignatureInvalid, Assert.Throws<SanovaException>(() => ManifestLoader.Load(Bytes, forged, trust, null)).Code);

        // Without configured keys and without requirement, unsigned manifests are accepted but marked unverified.
        Assert.False(ManifestLoader.Load(Bytes, null, new ManifestTrustConfig(), null).SignatureVerified);
    }
}

public class ComponentResolverTests
{
    [Fact]
    public void Initial_selection_uses_defaults_and_choices()
    {
        var manifest = ManifestSamples.Valid();
        Assert.Equal(["core", "hd"], ComponentResolver.InitialSelection(manifest, null).Order().ToArray());
        var chosen = ComponentResolver.InitialSelection(manifest, new Dictionary<string, bool> { ["extra"] = true });
        Assert.Contains("extra", chosen);
        Assert.Contains("hd", chosen);
    }

    [Fact]
    public void Enabling_pulls_in_dependencies_and_disables_conflicts()
    {
        var manifest = ManifestSamples.Valid();
        var selection = new HashSet<string>(["core"], StringComparer.OrdinalIgnoreCase);
        ComponentResolver.Toggle(manifest, selection, "extra", true, out var changes);
        Assert.Contains("hd", selection);
        Assert.Contains(changes, c => c is { ComponentId: "hd", Reason: SelectionChangeReason.RequiredByDependency });

        ComponentResolver.Toggle(manifest, selection, "lowres", true, out changes);
        Assert.DoesNotContain("hd", selection);
        Assert.DoesNotContain("extra", selection);
        Assert.Contains(changes, c => c is { ComponentId: "hd", Reason: SelectionChangeReason.ConflictDisabled });
        Assert.Contains(changes, c => c is { ComponentId: "extra", Reason: SelectionChangeReason.DependentDisabled });
        Assert.Empty(ComponentResolver.Problems(manifest, selection));
    }

    [Fact]
    public void Required_components_cannot_be_disabled()
    {
        var manifest = ManifestSamples.Valid();
        var selection = ComponentResolver.InitialSelection(manifest, null);
        Assert.False(ComponentResolver.Toggle(manifest, selection, "core", false, out _));
        Assert.Contains("core", selection);
    }
}
