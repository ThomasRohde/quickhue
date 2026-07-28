using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HueApi.BridgeLocator;
using QuickHue;

if (args.Contains("--discover-locators", StringComparer.OrdinalIgnoreCase))
{
    IBridgeLocator[] locators =
    [
        new MdnsBridgeLocator(),
        new SsdpBridgeLocator(),
        new LocalNetworkScanBridgeLocator(),
        new HttpBridgeLocator()
    ];
    foreach (var locator in locators)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var bridges = await locator.LocateBridgesAsync(TimeSpan.FromSeconds(12));
        Console.WriteLine($"{locator.GetType().Name} ({started.ElapsedMilliseconds} ms)");
        Console.WriteLine(JsonSerializer.Serialize(bridges, new JsonSerializerOptions { WriteIndented = true }));
    }
    return;
}

if (args.Contains("--ui-preview", StringComparer.OrdinalIgnoreCase))
{
    var index = Array.FindIndex(args, arg => arg.Equals("--ui-preview", StringComparison.OrdinalIgnoreCase));
    var outputDirectory = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
        ? args[index + 1]
        : Path.Combine(AppContext.BaseDirectory, "ui-preview");
    QuickHue.Tests.UiPreview.Render(outputDirectory);
    return;
}

if (args.Contains("--discover", StringComparer.OrdinalIgnoreCase))
{
    using var discovery = new BridgeDiscovery();
    var bridges = await discovery.DiscoverAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(bridges, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("configuration round-trip", ConfigurationRoundTrip),
    ("configuration validation", ConfigurationValidation),
    ("invalid configuration fallback", InvalidConfigurationFallback),
    ("DPAPI round-trip", DpapiRoundTrip),
    ("hotkey validation and display", HotkeyValidation),
    ("certificate pinning", CertificatePinningTest),
    ("Hue light parsing", HueLightParsing),
    ("Hue event parsing", HueEventParsing),
    ("Hue HTTP protocol", HueHttpProtocol),
    ("pairing and TLS pinning", PairingAndTlsPinning),
    ("Hue API error handling", HueApiErrorHandling),
    ("serialized rapid toggles", SerializedRapidToggles),
    ("bridge IP recovery", BridgeIpRecovery),
    ("event stream reconnect", EventStreamReconnect),
    ("offline error state", OfflineErrorState),
    ("HueApi bridge locator mapping", HueApiBridgeLocatorMapping),
    ("Windows hotkey conflict", WindowsHotkeyConflict),
    ("single-instance mutex", SingleInstanceMutex),
    ("startup command quoting", StartupCommandQuoting),
    ("bridge URI construction", BridgeUriConstruction)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL  {test.Name}");
        Console.WriteLine(exception);
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
if (failures.Count > 0)
{
    Environment.ExitCode = 1;
}

static Task ConfigurationRoundTrip()
{
    var directory = Path.Combine(Path.GetTempPath(), "QuickHue.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new ConfigStore(directory);
        var expected = Configured();
        store.Save(expected);
        var actual = store.Load();
        Equal(expected.BridgeId, actual.BridgeId);
        Equal(expected.Target.Id, actual.Target.Id);
        Equal(expected.Hotkey.DisplayText, actual.Hotkey.DisplayText);
        True(actual.IsConfigured);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    return Task.CompletedTask;
}

static Task InvalidConfigurationFallback()
{
    var directory = Path.Combine(Path.GetTempPath(), "QuickHue.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllText(Path.Combine(directory, "settings.json"), "{broken");
        var config = new ConfigStore(directory).Load();
        False(config.IsConfigured);
        Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
    return Task.CompletedTask;
}

static Task ConfigurationValidation()
{
    var config = Configured();
    True(config.IsConfigured);
    config.CertificateSha256 = "not-a-fingerprint";
    False(config.IsConfigured);
    config.CertificateSha256 = new string('A', 64);
    config.Hotkey.Modifiers = 0;
    False(config.IsConfigured);
    return Task.CompletedTask;
}

static Task DpapiRoundTrip()
{
    const string secret = "not-a-real-hue-key";
    var protectedValue = DpapiProtector.Protect(secret);
    False(protectedValue.Contains(secret, StringComparison.Ordinal));
    Equal(secret, DpapiProtector.Unprotect(protectedValue));
    return Task.CompletedTask;
}

static Task HotkeyValidation()
{
    var binding = HotkeyBinding.Default;
    True(binding.IsValid());
    Equal("Ctrl+Alt+L", binding.DisplayText);
    binding.Modifiers = 0;
    False(binding.IsValid());
    return Task.CompletedTask;
}

static Task CertificatePinningTest()
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=001788ABCDEF1234", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    var fingerprint = CertificatePinning.Fingerprint(certificate);
    True(CertificatePinning.Matches(certificate, fingerprint));
    False(CertificatePinning.Matches(certificate, new string('0', 64)));
    return Task.CompletedTask;
}

static Task HueLightParsing()
{
    using var document = JsonDocument.Parse(
        """{"data":[{"id":"b","metadata":{"name":"Desk"},"on":{"on":true}},{"id":"a","metadata":{"name":"Ambient"},"on":{"on":false}}],"errors":[]}""");
    var lights = HueClient.ParseLights(document.RootElement);
    Equal(2, lights.Count);
    Equal("Ambient", lights[0].Name);
    False(lights[0].IsOn);
    True(lights[1].IsOn);
    return Task.CompletedTask;
}

static Task HueEventParsing()
{
    bool? state = null;
    HueClient.ParseEventData(
        """[{"type":"update","data":[{"id":"target","type":"light","on":{"on":true}},{"id":"other","on":{"on":false}}]}]""",
        "target",
        value => state = value);
    Equal(true, state);
    return Task.CompletedTask;
}

static async Task HueHttpProtocol()
{
    var handler = new ScriptedHandler(
        request =>
        {
            Equal(HttpMethod.Get, request.Method);
            Equal("/clip/v2/resource/light", request.RequestUri!.AbsolutePath);
            True(request.Headers.Contains("hue-application-key"));
            return Json("""{"data":[{"id":"desk","metadata":{"name":"Desk"},"on":{"on":false}}],"errors":[]}""");
        },
        request =>
        {
            Equal(HttpMethod.Get, request.Method);
            Equal("/clip/v2/resource/light/desk", request.RequestUri!.AbsolutePath);
            return Json("""{"data":[{"id":"desk","on":{"on":false}}],"errors":[]}""");
        },
        request =>
        {
            Equal(HttpMethod.Put, request.Method);
            Equal("/clip/v2/resource/light/desk", request.RequestUri!.AbsolutePath);
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            True(body.Contains("\"on\":true", StringComparison.Ordinal));
            return Json("""{"data":[{"rid":"desk","rtype":"light"}],"errors":[]}""");
        });
    var config = Configured();
    using var client = new HueClient(config, "app-key", handler);
    var lights = await client.ListLightsAsync(CancellationToken.None);
    Equal(1, lights.Count);
    False(await client.GetLightStateAsync("desk", CancellationToken.None));
    await client.SetLightStateAsync("desk", true, CancellationToken.None);
    Equal(0, handler.Remaining);
}

static async Task PairingAndTlsPinning()
{
    var pairAttempts = 0;
    await using var server = await FakeHttpsBridgeServer.StartAsync(request =>
    {
        if (request.Method == "POST" && request.Path == "/api")
        {
            pairAttempts++;
            return pairAttempts == 1
                ? FakeResponse.Json("""[{"error":{"type":101,"description":"link button not pressed"}}]""")
                : FakeResponse.Json("""[{"success":{"username":"test-key","clientkey":"unused"}}]""");
        }
        if (request.Method == "GET" && request.Path == "/api/test-key/config")
        {
            return FakeResponse.Json("""{"bridgeid":"001788ABCDEF1234","modelid":"BSB002"}""");
        }
        if (request.Method == "GET" && request.Path == "/clip/v2/resource/light")
        {
            return FakeResponse.Json("""{"data":[{"id":"desk","metadata":{"name":"Desk"},"on":{"on":false}}],"errors":[]}""");
        }
        return new FakeResponse(404, "Not Found", "{}");
    });

    var bridge = new BridgeInfo("001788ABCDEF1234", server.Address, "Test");
    var pairing = new BridgePairingService();
    await ThrowsAsync<LinkButtonNotPressedException>(() => pairing.PairAsync(bridge, CancellationToken.None));
    var result = await pairing.PairAsync(bridge, CancellationToken.None);
    Equal("001788ABCDEF1234", result.BridgeId);
    Equal("test-key", result.ApplicationKey);
    Equal(CertificatePinning.Fingerprint(server.Certificate), result.CertificateSha256);

    var config = Configured();
    config.BridgeAddress = server.Address;
    config.CertificateSha256 = result.CertificateSha256;
    using (var client = new HueClient(config, result.ApplicationKey))
    {
        var lights = await client.ListLightsAsync(CancellationToken.None);
        Equal(1, lights.Count);
        Equal("Desk", lights[0].Name);
    }

    config.CertificateSha256 = new string('0', 64);
    using var rejectedClient = new HueClient(config, result.ApplicationKey);
    await ThrowsAsync<HttpRequestException>(
        () => rejectedClient.ListLightsAsync(CancellationToken.None));
}

static async Task HueApiErrorHandling()
{
    var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
    using var client = new HueClient(Configured(), "bad-key", handler);
    var exception = await ThrowsAsync<HueApiException>(
        () => client.ListLightsAsync(CancellationToken.None));
    True(exception.Message.Contains("Re-pair", StringComparison.OrdinalIgnoreCase));
}

static async Task SerializedRapidToggles()
{
    var directory = Path.Combine(Path.GetTempPath(), "QuickHue.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var fake = new FakeHueClient(initialState: false);
        using var controller = new HueController(
            Configured(),
            new ConfigStore(directory),
            new EmptyDiscovery(),
            fake);
        await controller.StartAsync();
        await Task.WhenAll(controller.ToggleAsync(), controller.ToggleAsync(), controller.ToggleAsync());
        True(new[] { true, false, true }.SequenceEqual(fake.Commands));
        True(controller.LastCommandMilliseconds < 500);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static async Task BridgeIpRecovery()
{
    var directory = Path.Combine(Path.GetTempPath(), "QuickHue.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var config = Configured();
        config.BridgeAddress = "192.0.2.1";
        var client = new AddressAwareHueClient(config, "192.0.2.2");
        using var controller = new HueController(
            config,
            new ConfigStore(directory),
            new FixedDiscovery(new BridgeInfo(config.BridgeId, "192.0.2.2", "Test")),
            client);
        await controller.StartAsync();
        Equal("192.0.2.2", config.BridgeAddress);
        Equal("192.0.2.2", new ConfigStore(directory).Load().BridgeAddress);
        True(client.GetCalls >= 2);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static async Task EventStreamReconnect()
{
    var directory = Path.Combine(Path.GetTempPath(), "QuickHue.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var client = new ReconnectingHueClient();
        using var controller = new HueController(
            Configured(),
            new ConfigStore(directory),
            new EmptyDiscovery(),
            client);
        await controller.StartAsync();
        await client.SecondConnection.Task.WaitAsync(TimeSpan.FromSeconds(4));
        True(client.StreamCalls >= 2);
        True(client.GetCalls >= 2);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static async Task OfflineErrorState()
{
    var directory = Path.Combine(Path.GetTempPath(), "QuickHue.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var states = new List<HueConnectionState>();
        string? error = null;
        using var controller = new HueController(
            Configured(),
            new ConfigStore(directory),
            new EmptyDiscovery(),
            new FailingSetHueClient());
        controller.StateChanged += (_, state) => states.Add(state);
        controller.Error += (_, message) => error = message;
        await controller.StartAsync();
        await controller.SetAsync(true);
        Equal(HueConnectionState.Offline, states.Last());
        True(!string.IsNullOrWhiteSpace(error));
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static Task HueApiBridgeLocatorMapping()
{
    var bridges = BridgeDiscovery.MapLocatedBridges(
        [
            new LocatedBridge("00:17:88:FF:FE:71:C8:01", "192.168.1.16", null),
            new LocatedBridge("001788FFFE71C801", "192.168.1.16", 443),
            new LocatedBridge(string.Empty, "192.168.1.17", null)
        ],
        "Test locator");
    Equal(1, bridges.Count);
    Equal("001788FFFE71C801", bridges[0].Id);
    Equal("https://192.168.1.16", bridges[0].Address);
    Equal("Test locator", bridges[0].Source);
    return Task.CompletedTask;
}

static Task StartupCommandQuoting()
{
    Equal("\"C:\\Program Files\\QuickHue\\QuickHue.exe\" --startup",
        StartupManager.QuoteExecutable(@"C:\Program Files\QuickHue\QuickHue.exe"));
    return Task.CompletedTask;
}

static Task WindowsHotkeyConflict()
{
    using var first = new HotkeyWindow();
    using var second = new HotkeyWindow();
    HotkeyBinding? registered = null;
    for (var key = Keys.F11; key >= Keys.F1; key--)
    {
        var candidate = new HotkeyBinding
        {
            Modifiers = (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift),
            VirtualKey = (uint)key
        };
        if (first.TryRegister(candidate, out _))
        {
            registered = candidate;
            break;
        }
    }
    True(registered is not null);
    False(second.TryRegister(registered!, out var error));
    True(!string.IsNullOrWhiteSpace(error));
    return Task.CompletedTask;
}

static Task SingleInstanceMutex()
{
    var name = $@"Local\QuickHue.Tests.{Guid.NewGuid():N}";
    using var first = new Mutex(true, name, out var createdFirst);
    using var second = new Mutex(true, name, out var createdSecond);
    True(createdFirst);
    False(createdSecond);
    return Task.CompletedTask;
}

static Task BridgeUriConstruction()
{
    var uri = BridgePairingService.BuildUri("https://127.0.0.1:54321", "/clip/v2/resource/light");
    Equal("https", uri.Scheme);
    Equal(54321, uri.Port);
    Equal("/clip/v2/resource/light", uri.AbsolutePath);
    return Task.CompletedTask;
}

static AppConfig Configured() => new()
{
    BridgeId = "001788ABCDEF1234",
    BridgeAddress = "192.168.1.2",
    CertificateSha256 = new string('A', 64),
    ProtectedApplicationKey = "protected",
    Target = new LightTarget { Id = "desk", Name = "Desk" }
};

static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};

static void True(bool condition)
{
    if (!condition) throw new InvalidOperationException("Expected true.");
}

static void False(bool condition) => True(!condition);

static void Equal<T>(T expected, T actual)
{
    if (expected is IEnumerable<bool> expectedItems && actual is IEnumerable<bool> actualItems)
    {
        if (!expectedItems.SequenceEqual(actualItems))
            throw new InvalidOperationException($"Expected [{string.Join(',', expectedItems)}], got [{string.Join(',', actualItems)}].");
        return;
    }
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException exception)
    {
        return exception;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);
    public int Remaining => _responses.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_responses.Count == 0) throw new InvalidOperationException("Unexpected HTTP request.");
        return Task.FromResult(_responses.Dequeue()(request));
    }
}

sealed class FakeHueClient(bool initialState) : IHueClient
{
    private bool _state = initialState;
    public List<bool> Commands { get; } = [];

    public Task<IReadOnlyList<HueLight>> ListLightsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HueLight>>([new HueLight("desk", "Desk", _state)]);

    public Task<bool> GetLightStateAsync(string lightId, CancellationToken cancellationToken) =>
        Task.FromResult(_state);

    public async Task SetLightStateAsync(string lightId, bool isOn, CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);
        _state = isOn;
        Commands.Add(isOn);
    }

    public Task ReadEventStreamAsync(string lightId, Action<bool> onState, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    public void Dispose() { }
}

sealed class EmptyDiscovery : IBridgeDiscovery
{
    public Task<IReadOnlyList<BridgeInfo>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BridgeInfo>>([]);
}

sealed class FixedDiscovery(params BridgeInfo[] bridges) : IBridgeDiscovery
{
    public Task<IReadOnlyList<BridgeInfo>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BridgeInfo>>(bridges);
}

sealed class AddressAwareHueClient(AppConfig config, string workingAddress) : IHueClient
{
    public int GetCalls { get; private set; }

    public Task<IReadOnlyList<HueLight>> ListLightsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HueLight>>([]);

    public Task<bool> GetLightStateAsync(string lightId, CancellationToken cancellationToken)
    {
        GetCalls++;
        return config.BridgeAddress == workingAddress
            ? Task.FromResult(false)
            : Task.FromException<bool>(new HttpRequestException("Old address."));
    }

    public Task SetLightStateAsync(string lightId, bool isOn, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task ReadEventStreamAsync(string lightId, Action<bool> onState, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    public void Dispose() { }
}

sealed class ReconnectingHueClient : IHueClient
{
    public int GetCalls { get; private set; }
    public int StreamCalls { get; private set; }
    public TaskCompletionSource SecondConnection { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<IReadOnlyList<HueLight>> ListLightsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HueLight>>([]);

    public Task<bool> GetLightStateAsync(string lightId, CancellationToken cancellationToken)
    {
        GetCalls++;
        return Task.FromResult(false);
    }

    public Task SetLightStateAsync(string lightId, bool isOn, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task ReadEventStreamAsync(string lightId, Action<bool> onState, CancellationToken cancellationToken)
    {
        StreamCalls++;
        if (StreamCalls == 1)
        {
            return Task.FromException(new IOException("Stream interrupted."));
        }
        SecondConnection.TrySetResult();
        return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public void Dispose() { }
}

sealed class FailingSetHueClient : IHueClient
{
    public Task<IReadOnlyList<HueLight>> ListLightsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HueLight>>([]);

    public Task<bool> GetLightStateAsync(string lightId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task SetLightStateAsync(string lightId, bool isOn, CancellationToken cancellationToken) =>
        Task.FromException(new HttpRequestException("Offline."));

    public Task ReadEventStreamAsync(string lightId, Action<bool> onState, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    public void Dispose() { }
}

sealed record FakeRequest(string Method, string Path, string Body);

sealed record FakeResponse(int StatusCode, string Reason, string Body)
{
    public static FakeResponse Json(string body) => new(200, "OK", body);
}

sealed class FakeHttpsBridgeServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<FakeRequest, FakeResponse> _handler;
    private readonly Task _acceptLoop;

    private FakeHttpsBridgeServer(
        TcpListener listener,
        X509Certificate2 certificate,
        Func<FakeRequest, FakeResponse> handler)
    {
        _listener = listener;
        Certificate = certificate;
        _handler = handler;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Address = $"https://127.0.0.1:{port}";
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
    }

    public string Address { get; }
    public X509Certificate2 Certificate { get; }

    public static Task<FakeHttpsBridgeServer> StartAsync(Func<FakeRequest, FakeResponse> handler)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=001788ABCDEF1234",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));
        var usages = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1", "Server Authentication")
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var certificate = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new FakeHttpsBridgeServer(listener, certificate, handler));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            // Listener shutdown.
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = Certificate,
                EnabledSslProtocols = SslProtocols.Tls12
            }, cancellationToken);

            using var reader = new StreamReader(ssl, Encoding.UTF8, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }
            var parts = requestLine.Split(' ', 3);
            var contentLength = 0;
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrEmpty(line))
                {
                    break;
                }
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out contentLength);
                }
            }
            var bodyBuffer = new char[contentLength];
            var totalRead = 0;
            while (totalRead < contentLength)
            {
                var read = await reader.ReadAsync(
                    bodyBuffer.AsMemory(totalRead, contentLength - totalRead),
                    cancellationToken);
                if (read == 0) break;
                totalRead += read;
            }

            var response = _handler(new FakeRequest(
                parts[0],
                new Uri($"https://localhost{parts[1]}").AbsolutePath,
                new string(bodyBuffer, 0, totalRead)));
            var responseBytes = Encoding.UTF8.GetBytes(response.Body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {response.StatusCode} {response.Reason}\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {responseBytes.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await ssl.WriteAsync(headers, cancellationToken);
            await ssl.WriteAsync(responseBytes, cancellationToken);
            await ssl.FlushAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (SocketException)
        {
            // Listener shutdown.
        }
        Certificate.Dispose();
        _lifetime.Dispose();
    }
}
