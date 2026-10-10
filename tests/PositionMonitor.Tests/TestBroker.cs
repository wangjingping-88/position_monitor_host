using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PositionMonitor.Tests;

/// <summary>仅用于测试的 TCP Broker，真实交换 MQTT 3.1.1 字节。</summary>
internal sealed class TestBroker : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, Peer> _peers = new();
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly Task _accept;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public bool PingResponses { get; set; } = true;
    public bool SubscribeResponses { get; set; } = true;
    public Func<string, byte[], bool>? SuppressPublishAcknowledgment { get; set; }
    public X509Certificate2? Certificate { get; set; }
    public ConcurrentQueue<Exception> Errors { get; } = new();
    public int Subscriptions => _peers.Values.Sum(p => p.Filters.Count);
    public TestBroker() { _listener.Start(); _accept = AcceptAsync(); }
    public void DropClients() { foreach (var client in _peers.Keys) client.Dispose(); }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var peer = new Peer(client.GetStream());
                _peers[client] = peer;
                _handlers.Add(HandleAsync(client, peer));
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task HandleAsync(TcpClient client, Peer peer)
    {
        try
        {
            if (Certificate is not null)
            {
                var tls = new SslStream(peer.Stream, false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, _stop.Token);
                peer.Stream = tls;
            }
            while (!_stop.IsCancellationRequested)
            {
                var (header, body) = await ReadAsync(peer.Stream, _stop.Token);
                var type = header >> 4;
                if (type == 1)
                {
                    var offset = 10;
                    ReadString(body, ref offset);
                    var user = (body[7] & 0x80) != 0 ? ReadString(body, ref offset) : null;
                    var password = (body[7] & 0x40) != 0 ? ReadString(body, ref offset) : null;
                    var denied = UserName is not null && (user != UserName || password != Password);
                    await peer.SendAsync([0x20, 2, 0, (byte)(denied ? 4 : 0)]);
                    if (denied) return;
                }
                else if (type == 8)
                {
                    var offset = 2;
                    var topic = ReadString(body, ref offset);
                    var qos = body[offset];
                    var denied = topic == "denied/#";
                    if (!denied) peer.Filters[topic] = qos;
                    if (SubscribeResponses) await peer.SendAsync([0x90, 3, body[0], body[1], (byte)(denied ? 0x80 : qos)]);
                }
                else if (type == 10)
                {
                    var offset = 2; peer.Filters.TryRemove(ReadString(body, ref offset), out _);
                    await peer.SendAsync([0xB0, 2, body[0], body[1]]);
                }
                else if (type == 3)
                {
                    var offset = 0; var topic = ReadString(body, ref offset);
                    var qos = (header >> 1) & 3;
                    var id = qos > 0 ? body[offset..(offset + 2)] : null;
                    if (qos > 0) offset += 2;
                    await BroadcastAsync(topic, body[offset..], (header & 1) != 0);
                    if (id is not null && SuppressPublishAcknowledgment?.Invoke(topic, body[offset..]) != true) await peer.SendAsync([0x40, 2, id[0], id[1]]);
                }
                else if (type == 12 && PingResponses) await peer.SendAsync([0xD0, 0]);
                else if (type == 14) return;
            }
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException or AuthenticationException) { Errors.Enqueue(error); }
        finally { _peers.TryRemove(client, out _); client.Dispose(); }
    }
    public async Task BroadcastAsync(string topic, byte[] payload, bool retained = false)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        foreach (var peer in _peers.Values)
        {
            if (!peer.Filters.Keys.Any(filter => Matches(filter, topic))) continue;
            var qos = peer.Filters.Where(f => Matches(f.Key, topic)).Max(f => f.Value);
            using var body = new MemoryStream();
            body.WriteByte((byte)(topicBytes.Length >> 8)); body.WriteByte((byte)topicBytes.Length); body.Write(topicBytes);
            if (qos == 1) { body.WriteByte(0); body.WriteByte(7); }
            body.Write(payload);
            await peer.SendAsync(Packet((byte)(0x30 | qos << 1 | (retained ? 1 : 0)), body.ToArray()));
        }
    }
    private static bool Matches(string filter, string topic)
    {
        var a = filter.Split('/'); var b = topic.Split('/');
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == "#") return true;
            if (i >= b.Length || a[i] != "+" && a[i] != b[i]) return false;
        }
        return a.Length == b.Length;
    }
    private static byte[] Packet(byte header, byte[] body)
    {
        var packet = new List<byte> { header }; var length = body.Length;
        do { var value = length % 128; length /= 128; packet.Add((byte)(value | (length != 0 ? 128 : 0))); } while (length != 0);
        packet.AddRange(body); return packet.ToArray();
    }
    private static string ReadString(byte[] bytes, ref int offset)
    {
        var length = bytes[offset] << 8 | bytes[offset + 1]; offset += 2;
        var text = Encoding.UTF8.GetString(bytes, offset, length); offset += length; return text;
    }
    private static async Task<(byte, byte[])> ReadAsync(Stream stream, CancellationToken token)
    {
        var one = new byte[1]; await stream.ReadExactlyAsync(one, token); var header = one[0];
        var length = 0; var factor = 1;
        do { await stream.ReadExactlyAsync(one, token); length += (one[0] & 127) * factor; factor *= 128; } while ((one[0] & 128) != 0);
        var body = new byte[length]; await stream.ReadExactlyAsync(body, token); return (header, body);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); DropClients();
        await _accept; await Task.WhenAll(_handlers); _stop.Dispose();
    }
    private sealed class Peer(Stream stream)
    {
        public Stream Stream { get; set; } = stream;
        public ConcurrentDictionary<string, byte> Filters { get; } = new();
        private readonly SemaphoreSlim _send = new(1, 1);
        public async Task SendAsync(byte[] packet)
        {
            await _send.WaitAsync();
            try { await Stream.WriteAsync(packet); }
            finally { _send.Release(); }
        }
    }
}
