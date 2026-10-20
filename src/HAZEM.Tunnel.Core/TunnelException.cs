using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.Core;

/// <summary>
/// Domain exception carrying a stable machine-readable category.
/// The UI maps categories to honest, specific messages (never generic "connection failed").
/// </summary>
public class TunnelException : Exception
{
    public TunnelErrorCategory Category { get; }

    public TunnelException(TunnelErrorCategory category, string message, Exception? inner = null)
        : base(message, inner)
    {
        Category = category;
    }

    public static TunnelException FromIo(Exception inner, string target) => new(
        inner switch
        {
            System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound => TunnelErrorCategory.Dns,
            System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused => TunnelErrorCategory.ConnectionRefused,
            System.Net.Sockets.SocketException se when se.SocketErrorCode == System.Net.Sockets.SocketError.NetworkUnreachable
                || se.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut => TunnelErrorCategory.NetworkUnreachable,
            _ => TunnelErrorCategory.NetworkUnreachable
        },
        $"Could not reach {target}: {inner.Message}", inner);
}
