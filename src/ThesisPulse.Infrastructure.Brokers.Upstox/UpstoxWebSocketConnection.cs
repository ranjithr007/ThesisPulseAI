using System.Net.WebSockets;

namespace ThesisPulse.Infrastructure.Brokers.Upstox;

public sealed record UpstoxWebSocketReceiveResult(
    int Count,
    WebSocketMessageType MessageType,
    bool EndOfMessage,
    WebSocketCloseStatus? CloseStatus,
    string? CloseStatusDescription);

public interface IUpstoxWebSocketConnection : IAsyncDisposable
{
    WebSocketState State { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);

    ValueTask<UpstoxWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);

    Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken);
}

public interface IUpstoxWebSocketConnectionFactory
{
    IUpstoxWebSocketConnection Create();
}

public sealed class UpstoxWebSocketConnectionFactory(
    UpstoxLiveFeedOptions options) : IUpstoxWebSocketConnectionFactory
{
    public IUpstoxWebSocketConnection Create() =>
        new ClientUpstoxWebSocketConnection(
            TimeSpan.FromSeconds(options.KeepAliveSeconds));
}

internal sealed class ClientUpstoxWebSocketConnection :
    IUpstoxWebSocketConnection
{
    private readonly ClientWebSocket _socket = new();

    public ClientUpstoxWebSocketConnection(TimeSpan keepAliveInterval)
    {
        _socket.Options.KeepAliveInterval = keepAliveInterval;
        _socket.Options.SetRequestHeader("User-Agent", "ThesisPulseAI-MarketData/0.2.0");
    }

    public WebSocketState State => _socket.State;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) =>
        _socket.SendAsync(
            payload,
            messageType,
            endOfMessage,
            cancellationToken);

    public async ValueTask<UpstoxWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var result = await _socket.ReceiveAsync(buffer, cancellationToken);
        return new UpstoxWebSocketReceiveResult(
            result.Count,
            result.MessageType,
            result.EndOfMessage,
            _socket.CloseStatus,
            _socket.CloseStatusDescription);
    }

    public Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) =>
        _socket.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
