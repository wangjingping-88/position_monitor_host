namespace PositionMonitor.Core.Mqtt;

/// <summary>单个可取消会话；串行连接与恢复订阅，退出时等待后台任务结束。</summary>
public sealed class MqttSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Mqtt311Client? _client;
    private Task? _run;
    public bool IsSubscribed { get; private set; }
    public event EventHandler<MqttApplicationMessage>? MessageReceived;
    public event EventHandler<string>? StateChanged;

    public void Start(MqttClientOptions options, string topic, byte qos) => Start(options, new[] { topic }, qos);

    public void Start(MqttClientOptions options, IEnumerable<string> topics, byte qos)
    {
        if (_run is not null) throw new InvalidOperationException("会话已经启动。");
        options.Validate();
        var filters = SubscriptionFilters.Normalize(topics);
        if (qos > 1) throw new ArgumentOutOfRangeException(nameof(qos));
        _run = Task.Run(() => RunAsync(options, filters, qos));
    }

    private async Task RunAsync(MqttClientOptions options, string[] topics, byte qos)
    {
        var retry = 1;
        while (!_stop.IsCancellationRequested)
        {
            await using var client = new Mqtt311Client();
            _client = client;
            var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ConnectionFaulted += (_, error) => fault.TrySetResult(error);
            client.MessageReceived += (_, message) => MessageReceived?.Invoke(this, message);
            try
            {
                StateChanged?.Invoke(this, "正在连接服务器…");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                await client.ConnectAsync(options, timeout.Token);
                foreach (var topic in topics)
                {
                    using var subscribeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    subscribeTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                    try { await client.SubscribeAsync(topic, qos, subscribeTimeout.Token); }
                    catch (Exception error) when (!_stop.IsCancellationRequested)
                    { throw new InvalidOperationException($"订阅 {topic} 失败：{error.Message}", error); }
                }
                IsSubscribed = true;
                retry = 1;
                StateChanged?.Invoke(this, "已连接并订阅");
                throw await fault.Task.WaitAsync(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                IsSubscribed = false;
                StateChanged?.Invoke(this, $"连接未就绪：{error.Message}  {retry} 秒后重试");
            }
            finally
            {
                IsSubscribed = false;
                _client = null;
                await client.DisconnectAsync();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(retry), _stop.Token); }
            catch (OperationCanceledException) { break; }
            retry = Math.Min(30, retry * 2);
        }
        StateChanged?.Invoke(this, "未连接");
    }

    public Task PublishAsync(MqttApplicationMessage message, CancellationToken token = default)
    {
        var client = _client;
        if (!IsSubscribed || client is null) throw new InvalidOperationException("MQTT 尚未就绪，回执未发送。");
        return client.PublishAsync(message, token);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_run is not null) await _run;
        _stop.Dispose();
    }
}
