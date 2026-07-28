using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace QuickHue;

internal static class CertificatePinning
{
    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    public static bool Matches(X509Certificate2? certificate, string expectedFingerprint) =>
        certificate is not null &&
        !string.IsNullOrWhiteSpace(expectedFingerprint) &&
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(Fingerprint(certificate)),
            Convert.FromHexString(Normalize(expectedFingerprint)));

    public static string Normalize(string fingerprint) =>
        fingerprint.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
}
