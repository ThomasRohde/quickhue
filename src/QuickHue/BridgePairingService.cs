using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace QuickHue;

internal sealed record PairingResult(
    string BridgeId,
    string Address,
    string CertificateSha256,
    string ApplicationKey);

internal sealed class LinkButtonNotPressedException : Exception
{
    public LinkButtonNotPressedException(string message) : base(message) { }
}

internal sealed class BridgePairingService
{
    public async Task<PairingResult> PairAsync(BridgeInfo bridge, CancellationToken cancellationToken)
    {
        string? fingerprint = null;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                {
                    return false;
                }
                var presented = CertificatePinning.Fingerprint(new X509Certificate2(certificate));
                if (fingerprint is null)
                {
                    fingerprint = presented;
                    return true;
                }
                return fingerprint.Equals(presented, StringComparison.OrdinalIgnoreCase);
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };

        var deviceType = $"quickhue#{SanitizeDeviceName(Environment.MachineName)}";
        using var content = new StringContent(
            JsonSerializer.Serialize(new { devicetype = deviceType, generateclientkey = true }),
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync(BuildUri(bridge.Address, "/api"), content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(body);
        var first = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().FirstOrDefault()
            : default;
        if (first.ValueKind == JsonValueKind.Undefined)
        {
            throw new HueApiException("The bridge returned an empty pairing response.");
        }
        if (first.TryGetProperty("error", out var error))
        {
            var type = error.TryGetProperty("type", out var typeElement) ? typeElement.GetInt32() : 0;
            var description = error.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString() ?? "Pairing failed."
                : "Pairing failed.";
            if (type == 101)
            {
                throw new LinkButtonNotPressedException("Press the round button on the Hue Bridge, then try Pair again.");
            }
            throw new HueApiException(description);
        }
        if (!first.TryGetProperty("success", out var success) ||
            !success.TryGetProperty("username", out var usernameElement))
        {
            throw new HueApiException("The bridge did not return an application key.");
        }

        var applicationKey = usernameElement.GetString()
            ?? throw new HueApiException("The bridge returned an empty application key.");
        var bridgeId = await ReadAndVerifyBridgeIdAsync(client, bridge, applicationKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new HueApiException("The bridge did not present a TLS certificate.");
        }

        return new PairingResult(
            bridgeId,
            bridge.Address,
            CertificatePinning.Normalize(fingerprint),
            applicationKey);
    }

    private static async Task<string> ReadAndVerifyBridgeIdAsync(
        HttpClient client,
        BridgeInfo bridge,
        string applicationKey,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            BuildUri(bridge.Address, $"/api/{Uri.EscapeDataString(applicationKey)}/config"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var actual = document.RootElement.TryGetProperty("bridgeid", out var idElement)
            ? BridgeDiscovery.NormalizeBridgeId(idElement.GetString())
            : BridgeDiscovery.NormalizeBridgeId(bridge.Id);
        var discovered = BridgeDiscovery.NormalizeBridgeId(bridge.Id);
        if (!string.IsNullOrWhiteSpace(discovered) &&
            !string.IsNullOrWhiteSpace(actual) &&
            !discovered.Equals(actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new HueApiException("The bridge identity did not match the discovered device.");
        }
        return string.IsNullOrWhiteSpace(actual) ? discovered : actual;
    }

    internal static Uri BuildUri(string address, string path)
    {
        var normalized = address.Trim();
        Uri? supplied;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out supplied))
        {
            Uri.TryCreate($"{Uri.UriSchemeHttps}://{normalized}", UriKind.Absolute, out supplied);
        }
        if (supplied is null)
        {
            throw new UriFormatException("Enter a valid bridge IP address or host name.");
        }
        var builder = new UriBuilder(supplied)
        {
            Scheme = Uri.UriSchemeHttps,
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private static string SanitizeDeviceName(string value)
    {
        var safe = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "windows" : safe[..Math.Min(19, safe.Length)];
    }
}
