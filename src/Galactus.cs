using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CaptureMod;

// A connection to Galactus under one Connect Code. Galactus only speaks Socket.IO v2 (Engine.IO 3), so this is just
// enough of that protocol (ADR 0002): a websocket, pings, and events with one string argument. It reconnects with
// backoff until stopped, or until a newer Capture takes over the Connect Code (ADR 0001).
public sealed class Galactus
{
    static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(1), MaxRetry = TimeSpan.FromSeconds(30);

    public CaptureLink Link { get; }
    readonly string version;
    readonly Action<string> log;
    readonly CancellationTokenSource stop = new();
    volatile Channel<string>? outbox;
    int connections;

    public Galactus(CaptureLink link, string version, Action<string> log)
    {
        Link = link;
        this.version = version;
        this.log = log;
    }

    public bool Connected => outbox is not null;

    // Goes up by one on every connection. Galactus forgets a Capture's state when it disconnects, so whoever reports
    // game state sends all of it again when this changes.
    public int Connections => Volatile.Read(ref connections);

    // A replaced Capture must never reconnect, or two Captures would replace each other forever.
    public bool Replaced => replaced;
    volatile bool replaced;

    public void Start() => _ = Task.Run(RunAsync);

    public void Stop() => stop.Cancel();

    // Dropped while disconnected; everything is resent after reconnecting.
    public void Emit(string name, string arg) =>
        outbox?.Writer.TryWrite("42" + JsonSerializer.Serialize(new[] { name, arg }));

    async Task RunAsync()
    {
        var retry = MinRetry;
        while (!stop.IsCancellationRequested)
        {
            var before = Connections;
            try
            {
                await ConnectOnceAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log($"Galactus connection failed: {e.Message}");
            }

            if (Replaced)
            {
                log("Another capture connected with this connect code, so this one stopped");
                return;
            }
            if (Connections != before) retry = MinRetry;
            log($"Reconnecting to Galactus in {retry.TotalSeconds:0}s");
            try
            {
                await Task.Delay(retry, stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
        }
    }

    async Task ConnectOnceAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        var query = $"EIO=3&transport=websocket&kind=capture-mod&version={Uri.EscapeDataString(version)}";
        await socket.ConnectAsync(new Uri(Link.Galactus, "/socket.io/?" + query), ct);

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var packets = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var lastPong = DateTime.UtcNow;
        var background = Task.CompletedTask;
        try
        {
            while (true)
            {
                var packet = await ReceiveAsync(socket, connection.Token);
                if (packet is null || packet.StartsWith('1')) return;

                if (packet.StartsWith('0'))
                {
                    // Engine.IO handshake. In v2 the client pings and the server pongs.
                    using var handshake = JsonDocument.Parse(packet[1..]);
                    var interval = TimeSpan.FromMilliseconds(handshake.RootElement.GetProperty("pingInterval").GetInt32());
                    var timeout = TimeSpan.FromMilliseconds(handshake.RootElement.GetProperty("pingTimeout").GetInt32());
                    background = Task.WhenAll(
                        SendAllAsync(socket, packets.Reader, connection.Token),
                        PingAsync(packets.Writer, interval, timeout, () => lastPong, socket, connection.Token));
                }
                else if (packet.StartsWith('3'))
                {
                    lastPong = DateTime.UtcNow;
                }
                else if (packet == "40")
                {
                    // Connected to the default namespace. The Connect Code goes first, so Galactus knows which game
                    // everything after it belongs to.
                    packets.Writer.TryWrite("42" + JsonSerializer.Serialize(new[] { "connectCode", Link.ConnectCode }));
                    outbox = packets;
                    Interlocked.Increment(ref connections);
                    log($"Connected to Galactus at {Link.Galactus.Authority}");
                }
                else if (packet.StartsWith("41"))
                {
                    return;
                }
                else if (packet.StartsWith("42") && EventName(packet) == "killself")
                {
                    replaced = true;
                    return;
                }
            }
        }
        finally
        {
            outbox = null;
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // closing is a courtesy; the socket is disposed either way
                }
            }
            connection.Cancel();
            packets.Writer.TryComplete();
            try
            {
                await background;
            }
            catch (Exception)
            {
                // the connection is already gone; whatever stopped it has been reported
            }
            log("Disconnected from Galactus");
        }
    }

    static async Task SendAllAsync(ClientWebSocket socket, ChannelReader<string> packets, CancellationToken ct)
    {
        await foreach (var packet in packets.ReadAllAsync(ct))
            await socket.SendAsync(Encoding.UTF8.GetBytes(packet), WebSocketMessageType.Text, true, ct);
    }

    // A connection that drops without closing never errors on its own, so give up on it when pongs stop.
    static async Task PingAsync(ChannelWriter<string> packets, TimeSpan interval, TimeSpan timeout,
        Func<DateTime> lastPong, ClientWebSocket socket, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(interval, ct);
            if (DateTime.UtcNow - lastPong() > interval + timeout)
            {
                socket.Abort();
                return;
            }
            packets.TryWrite("2");
        }
    }

    static async Task<string?> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    // 42["name",args...], possibly with an ack id between the 42 and the array.
    static string? EventName(string packet)
    {
        var start = packet.IndexOf('[');
        if (start < 0) return null;
        using var json = JsonDocument.Parse(packet[start..]);
        return json.RootElement[0].GetString();
    }
}
