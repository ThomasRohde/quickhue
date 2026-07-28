using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using HueApi.BridgeLocator;

namespace QuickHue;

internal sealed record BridgeInfo(string Id, string Address, string Source)
{
    /// <summary>Leads with the address because that is what a person can recognise.</summary>
    public override string ToString()
    {
        var host = Address.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');
        return $"{host}  ({Source})";
    }
}

internal interface IBridgeDiscovery
{
    Task<IReadOnlyList<BridgeInfo>> DiscoverAsync(CancellationToken cancellationToken);
}

internal sealed class BridgeDiscovery : IBridgeDiscovery, IDisposable
{
    private static readonly TimeSpan FastDiscoveryTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NetworkScanTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public BridgeDiscovery(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    }

    public async Task<IReadOnlyList<BridgeInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var fast = await DiscoverFastAsync(cancellationToken);
        if (fast.Count > 0)
        {
            return Merge(fast);
        }

        var scanned = await DiscoverLocalNetworkSafelyAsync(cancellationToken);
        return Merge(scanned);
    }

    private async Task<IReadOnlyList<BridgeInfo>> DiscoverFastAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FastDiscoveryTimeout);

        var mdnsTask = RunLocatorSafelyAsync(
            new MdnsBridgeLocator(),
            "HueApi mDNS",
            timeout,
            cancellationToken);
        var ssdpTask = RunLocatorSafelyAsync(
            new SsdpBridgeLocator(),
            "HueApi SSDP",
            timeout,
            cancellationToken);
        var brokerTask = DiscoverBrokerSafelyAsync(timeout, cancellationToken);
        await Task.WhenAll(mdnsTask, ssdpTask, brokerTask);
        cancellationToken.ThrowIfCancellationRequested();
        return mdnsTask.Result.Concat(ssdpTask.Result).Concat(brokerTask.Result).ToArray();
    }

    private static async Task<IReadOnlyList<BridgeInfo>> RunLocatorSafelyAsync(
        BridgeLocator locator,
        string source,
        CancellationTokenSource sharedTimeout,
        CancellationToken cancellationToken)
    {
        BridgeLocator.BridgeFoundHandler found = (_, _) => sharedTimeout.Cancel();
        locator.BridgeFound += found;
        try
        {
            var located = await locator.LocateBridgesAsync(sharedTimeout.Token);
            var mapped = MapLocatedBridges(located, source);
            if (mapped.Count > 0)
            {
                sharedTimeout.Cancel();
            }
            return mapped;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or OperationCanceledException or SocketException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Array.Empty<BridgeInfo>();
        }
        finally
        {
            locator.BridgeFound -= found;
        }
    }

    private static async Task<IReadOnlyList<BridgeInfo>> DiscoverLocalNetworkSafelyAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NetworkScanTimeout);
        return await RunLocatorSafelyAsync(
            new LocalNetworkScanBridgeLocator(),
            "HueApi local scan",
            timeout,
            cancellationToken);
    }

    private async Task<IReadOnlyList<BridgeInfo>> DiscoverBrokerSafelyAsync(
        CancellationTokenSource sharedTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                "https://discovery.meethue.com/",
                sharedTimeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(sharedTimeout.Token);
            var entries = await JsonSerializer.DeserializeAsync<BrokerEntry[]>(
                stream,
                cancellationToken: sharedTimeout.Token) ?? Array.Empty<BrokerEntry>();
            var results = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.InternalIpAddress))
                .Select(entry => new BridgeInfo(
                    NormalizeBridgeId(entry.Id),
                    entry.InternalIpAddress!.Trim(),
                    "Hue discovery"))
                .ToArray();
            if (results.Length > 0)
            {
                sharedTimeout.Cancel();
            }
            return results;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Array.Empty<BridgeInfo>();
        }
    }

    internal static IReadOnlyList<BridgeInfo> MapLocatedBridges(
        IEnumerable<LocatedBridge> located,
        string source) =>
        located
            .Where(bridge =>
                !string.IsNullOrWhiteSpace(bridge.BridgeId) &&
                !string.IsNullOrWhiteSpace(bridge.IpAddress))
            .Select(bridge => new BridgeInfo(
                NormalizeBridgeId(bridge.BridgeId),
                bridge.Url,
                source))
            .GroupBy(bridge => bridge.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    private static IReadOnlyList<BridgeInfo> Merge(IEnumerable<BridgeInfo> bridges)
    {
        var merged = new Dictionary<string, BridgeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var bridge in bridges.OrderBy(bridge => SourceRank(bridge.Source)))
        {
            var key = string.IsNullOrWhiteSpace(bridge.Id) ? bridge.Address : bridge.Id;
            merged.TryAdd(key, bridge);
        }
        return merged.Values
            .OrderBy(bridge => SourceRank(bridge.Source))
            .ThenBy(bridge => bridge.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int SourceRank(string source) => source switch
    {
        "HueApi mDNS" => 0,
        "HueApi SSDP" => 1,
        "HueApi local scan" => 2,
        _ => 3
    };

    internal static string NormalizeBridgeId(string? id) =>
        (id ?? string.Empty).Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed class BrokerEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("internalipaddress")]
        public string? InternalIpAddress { get; set; }
    }
}
