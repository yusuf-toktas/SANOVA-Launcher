using System.Security.Cryptography;
using System.Text;

namespace Sanova.Core.Content;

/// <summary>
/// ECDSA P-256 / SHA-256 signatures over the exact manifest bytes. The launcher embeds the
/// public key(s); maintainers sign with the private key using <c>sanova-manifest sign</c>.
/// Because the manifest carries the SHA-256 of every package and file, a valid signature
/// authenticates the entire release, independent of the hosting or mirror used.
/// </summary>
public static class ManifestSignature
{
    public static (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    public static string Sign(ReadOnlySpan<byte> manifestBytes, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    /// <summary>True when any trusted public key verifies the signature.</summary>
    public static bool Verify(ReadOnlySpan<byte> manifestBytes, string signatureText, IEnumerable<string> trustedPublicKeysPem)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var pem in trustedPublicKeysPem)
        {
            if (string.IsNullOrWhiteSpace(pem)) continue;
            try
            {
                using var key = ECDsa.Create();
                key.ImportFromPem(pem);
                if (key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                // A malformed configured key cannot verify anything; try the next one.
            }
        }
        return false;
    }

    public static string Fingerprint(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var spki = key.ExportSubjectPublicKeyInfo();
        return Convert.ToHexStringLower(SHA256.HashData(spki))[..16];
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
