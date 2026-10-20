using System.Buffers;
using System.IO;
using System.Net.WebSockets;

namespace HAZEM.Tunnel.Networking.Transports;

/// <summary>
/// Exposes a ClientWebSocket session as a read/write Stream so the relay can pump bytes
/// through it like any other transport. One WebSocketStream serves one pipe at a time.
/// </summary>
public sealed class WebSocketStream : Stream
{
    private readonly ClientWebSocket _ws;
    private readonly byte[] _readBuffer;      // payload bytes pending delivery
    private int _readStart;
    private int _readEnd;
    private bool _closed;

    public WebSocketStream(ClientWebSocket ws, int bufferSize = 32 * 1024)
    {
        _ws = ws;
        _readBuffer = new byte[bufferSize];
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_closed) return 0;
        if (_readStart < _readEnd)
        {
            var n = Math.Min(buffer.Length, _readEnd - _readStart);
            _readBuffer.AsSpan(_readStart, n).CopyTo(buffer.Span);
            _readStart += n;
            return n;
        }
        while (true)
        {
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(_readBuffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await TryCloseOutputAsync(WebSocketCloseStatus.NormalClosure, "remote close").ConfigureAwait(false);
                _closed = true;
                return 0;
            }
            var count = result.Count;
            if (count == 0) continue;
            var n = Math.Min(buffer.Length, count);
            _readBuffer.AsSpan(0, n).CopyTo(buffer.Span);
            _readStart = n;   // remainder stays buffered
            _readEnd = count;
            return n;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (_closed) throw new IOException("WebSocket pipe is closed.");
        await _ws.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
    }

    public override void Flush() { /* WebSocket framing sends immediately */ }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await TryCloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client closed").ConfigureAwait(false);
        _ws.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (!_closed)
                {
                    TryCloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client closed")
                        .AsTask().GetAwaiter().GetResult();
                }
            }
            catch { /* socket already dead */ }
            _ws.Dispose();
        }
        base.Dispose(disposing);
    }

    private async ValueTask TryCloseOutputAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
            }
        }
        catch { /* best effort */ }
    }
}
