using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace QuickHue;

internal sealed record HueLight(string Id, string Name, bool IsOn)
{
    public override string ToString() => Name;
}

internal sealed class HueApiException : Exception
{
    public HueApiException(string message) : base(message) { }
    public HueApiException(string message, Exception innerException) : base(message, innerException) { }
}

internal interface IHueClient : IDisposable
{
    Task<IReadOnlyList<HueLight>> ListLightsAsync(CancellationToken cancellationToken);
    Task<bool> GetLightStateAsync(string lightId, CancellationToken cancellationToken);
    Task SetLightStateAsync(string lightId, bool isOn, CancellationToken cancellationToken);
    Task ReadEventStreamAsync(string lightId, Action<bool> onState, CancellationToken cancellationToken);
}

internal sealed class HueClient : IHueClient
{
    private readonly AppConfig _config;
    private readonly string _applicationKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public HueClient(AppConfig config, string applicationKey, HttpMessageHandler? handler = null)
    {
        _config = config;
        _applicationKey = applicationKey;
        if (handler is null)
        {
            handler = CreatePinnedHandler(config.CertificateSha256);
            _ownsClient = true;
        }
        _httpClient = new HttpClient(handler, disposeHandler: _ownsClient)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<IReadOnlyList<HueLight>> ListLightsAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/clip/v2/resource/light");
        using var response = await SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureNoHueErrors(document.RootElement);
        return ParseLights(document.RootElement);
    }

    public async Task<bool> GetLightStateAsync(string lightId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"/clip/v2/resource/light/{Uri.EscapeDataString(lightId)}");
        using var response = await SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureNoHueErrors(document.RootElement);
        if (!TryReadFirstLightState(document.RootElement, out var isOn))
        {
            throw new HueApiException("The bridge did not return the selected light state.");
        }
        return isOn;
    }

    public async Task SetLightStateAsync(string lightId, bool isOn, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Put,
            $"/clip/v2/resource/light/{Uri.EscapeDataString(lightId)}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { on = new { on = isOn } }),
            Encoding.UTF8,
            "application/json");
        using var response = await SendAsync(request, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureNoHueErrors(document.RootElement);
    }

    public async Task ReadEventStreamAsync(
        string lightId,
        Action<bool> onState,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/eventstream/clip/v2");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        EnsureStatus(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var data = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new IOException("The Hue event stream ended.");
            }
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    ParseEventData(data.ToString(), lightId, onState);
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }
                data.Append(line.AsSpan(5).TrimStart());
            }
        }
    }

    internal static IReadOnlyList<HueLight> ParseLights(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<HueLight>();
        }

        var lights = new List<HueLight>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement))
            {
                continue;
            }
            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            var name = item.TryGetProperty("metadata", out var metadata) &&
                       metadata.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var isOn = TryReadLightObjectState(item, out var state) && state;
            lights.Add(new HueLight(id, string.IsNullOrWhiteSpace(name) ? id : name, isOn));
        }
        return lights.OrderBy(light => light.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static void ParseEventData(string json, string lightId, Action<bool> onState)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            IEnumerable<JsonElement> events = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            foreach (var eventItem in events)
            {
                if (!eventItem.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var update in data.EnumerateArray())
                {
                    if (!update.TryGetProperty("id", out var idElement) ||
                        !string.Equals(idElement.GetString(), lightId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (TryReadLightObjectState(update, out var isOn))
                    {
                        onState(isOn);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Ignore a malformed event and keep the long-lived stream active.
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.SendAsync(request, cancellationToken);
        try
        {
            EnsureStatus(response);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, BridgePairingService.BuildUri(_config.BridgeAddress, path));
        request.Headers.TryAddWithoutValidation("hue-application-key", _applicationKey);
        return request;
    }

    private static HttpMessageHandler CreatePinnedHandler(string fingerprint)
    {
        return new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null &&
                    CertificatePinning.Matches(new X509Certificate2(certificate), fingerprint)
            }
        };
    }

    private static void EnsureStatus(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "The Hue Bridge rejected the application key. Re-pair QuickHue.",
            HttpStatusCode.NotFound => "The selected Hue light no longer exists.",
            _ => $"Hue Bridge request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
        };
        throw new HueApiException(message);
    }

    private static void EnsureNoHueErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) ||
            errors.ValueKind != JsonValueKind.Array ||
            errors.GetArrayLength() == 0)
        {
            return;
        }
        var first = errors[0];
        var message = first.TryGetProperty("description", out var description)
            ? description.GetString()
            : first.ToString();
        throw new HueApiException(message ?? "The Hue Bridge returned an error.");
    }

    private static bool TryReadFirstLightState(JsonElement root, out bool isOn)
    {
        isOn = false;
        return root.TryGetProperty("data", out var data) &&
               data.ValueKind == JsonValueKind.Array &&
               data.GetArrayLength() > 0 &&
               TryReadLightObjectState(data[0], out isOn);
    }

    private static bool TryReadLightObjectState(JsonElement item, out bool isOn)
    {
        isOn = false;
        if (!item.TryGetProperty("on", out var on) ||
            !on.TryGetProperty("on", out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        isOn = value.GetBoolean();
        return true;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
