using System.Diagnostics;
using System.Security.Authentication;

namespace QuickHue;

internal enum HueConnectionState
{
    Connecting,
    On,
    Off,
    Offline
}

internal sealed class HueController : IDisposable
{
    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly IBridgeDiscovery _discovery;
    private readonly IHueClient _client;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool? _isOn;
    private Task? _eventTask;

    public HueController(
        AppConfig config,
        ConfigStore store,
        IBridgeDiscovery? discovery = null,
        IHueClient? client = null)
    {
        _config = config;
        _store = store;
        _discovery = discovery ?? new BridgeDiscovery();
        _client = client ?? new HueClient(config, DpapiProtector.Unprotect(config.ProtectedApplicationKey));
    }

    public event EventHandler<HueConnectionState>? StateChanged;
    public event EventHandler<string>? Error;
    public long LastCommandMilliseconds { get; private set; }

    public async Task StartAsync()
    {
        ChangeState(HueConnectionState.Connecting);
        try
        {
            _isOn = await ExecuteWithRecoveryAsync(
                token => _client.GetLightStateAsync(_config.Target.Id, token),
                _lifetime.Token);
            PublishCurrentState();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GoOffline(exception);
        }
        _eventTask = RunEventLoopAsync(_lifetime.Token);
    }

    public async Task ToggleAsync()
    {
        await _commandGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!_isOn.HasValue)
            {
                _isOn = await ExecuteWithRecoveryAsync(
                    token => _client.GetLightStateAsync(_config.Target.Id, token),
                    _lifetime.Token);
            }
            await SetCoreAsync(!_isOn.Value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GoOffline(exception);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task SetAsync(bool isOn)
    {
        await _commandGate.WaitAsync(_lifetime.Token);
        try
        {
            await SetCoreAsync(isOn);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GoOffline(exception);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task SetCoreAsync(bool isOn)
    {
        var stopwatch = Stopwatch.StartNew();
        await ExecuteWithRecoveryAsync(
            async token =>
            {
                await _client.SetLightStateAsync(_config.Target.Id, isOn, token);
                return true;
            },
            _lifetime.Token);
        stopwatch.Stop();
        LastCommandMilliseconds = stopwatch.ElapsedMilliseconds;
        _isOn = isOn;
        PublishCurrentState();
    }

    private async Task RunEventLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var refreshBeforeReconnect = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (refreshBeforeReconnect)
                {
                    _isOn = await ExecuteWithRecoveryAsync(
                        token => _client.GetLightStateAsync(_config.Target.Id, token),
                        cancellationToken);
                    PublishCurrentState();
                    refreshBeforeReconnect = false;
                }
                await _client.ReadEventStreamAsync(
                    _config.Target.Id,
                    isOn =>
                    {
                        _isOn = isOn;
                        PublishCurrentState();
                    },
                    cancellationToken);
                delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                GoOffline(exception, notify: false);
                refreshBeforeReconnect = true;
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task<T> ExecuteWithRecoveryAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var firstAttempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        firstAttempt.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            return await action(firstAttempt.Token);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or TaskCanceledException)
        {
            var bridges = await _discovery.DiscoverAsync(cancellationToken);
            var bridge = bridges.FirstOrDefault(candidate =>
                BridgeDiscovery.NormalizeBridgeId(candidate.Id)
                    .Equals(BridgeDiscovery.NormalizeBridgeId(_config.BridgeId), StringComparison.OrdinalIgnoreCase));
            if (bridge is not null &&
                !bridge.Address.Equals(_config.BridgeAddress, StringComparison.OrdinalIgnoreCase))
            {
                _config.BridgeAddress = bridge.Address;
                _store.Save(_config);
            }

            using var retry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            retry.CancelAfter(TimeSpan.FromSeconds(4));
            return await action(retry.Token);
        }
    }

    private void PublishCurrentState() =>
        ChangeState(_isOn == true ? HueConnectionState.On : HueConnectionState.Off);

    private void ChangeState(HueConnectionState state) =>
        StateChanged?.Invoke(this, state);

    private void GoOffline(Exception exception, bool notify = true)
    {
        _isOn = null;
        ChangeState(HueConnectionState.Offline);
        if (notify)
        {
            Error?.Invoke(this, FriendlyMessage(exception));
        }
    }

    internal static string FriendlyMessage(Exception exception) => exception switch
    {
        AuthenticationException => "The bridge certificate changed. Open Settings and pair again.",
        HueApiException => exception.Message,
        TaskCanceledException => "The Hue Bridge did not respond in time.",
        HttpRequestException => "QuickHue cannot reach the Hue Bridge.",
        IOException => "The connection to the Hue Bridge was interrupted.",
        _ => "QuickHue could not switch the light."
    };

    public void Dispose()
    {
        _lifetime.Cancel();
        try
        {
            _eventTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Cancellation during shutdown is expected.
        }
        _client.Dispose();
        if (_discovery is IDisposable disposableDiscovery)
        {
            disposableDiscovery.Dispose();
        }
        _commandGate.Dispose();
        _lifetime.Dispose();
    }
}
