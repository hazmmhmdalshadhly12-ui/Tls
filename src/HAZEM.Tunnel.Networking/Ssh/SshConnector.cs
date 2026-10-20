using System.Net.Sockets;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Secrets;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace HAZEM.Tunnel.Networking.Ssh;

/// <summary>
/// Builds and tests SSH.NET connections. Host key verification always runs via
/// SshHostKeyVerifier (TOFU/Strict/Pinned) — the session fails closed on mismatch.
/// </summary>
public sealed class SshConnector : ISshConnector
{
    private readonly SshHostKeyVerifier _verifier;
    private readonly SecretResolver _secrets;
    private readonly ILogger<SshConnector> _logger;

    public SshConnector(SshHostKeyVerifier verifier, SecretResolver secrets, ILogger<SshConnector> logger)
    {
        _verifier = verifier;
        _secrets = secrets;
        _logger = logger;
    }

    public SecretResolver Secrets => _secrets;

    /// <summary>Builds an SSH.NET authentication method from profile options (resolves secrets safely).</summary>
    public AuthenticationMethod BuildAuthentication(SshOptions options) => options.AuthMode switch
    {
        SshAuthMode.Password => new PasswordAuthenticationMethod(
            options.Username, _secrets.Resolve(options.PasswordRef, "SSH password")),
        SshAuthMode.PrivateKey => CreateKeyAuth(options),
        _ => throw new TunnelException(TunnelErrorCategory.Configuration, "Unsupported SSH auth mode.")
    };

    private ConnectionInfo BuildConnectionInfo(SshOptions options) =>
        new(options.Host, options.Port, options.Username, BuildAuthentication(options))
        {
            Timeout = options.ConnectTimeout,
            RetryAttempts = 1
        };

    private PrivateKeyAuthenticationMethod CreateKeyAuth(SshOptions options)
    {
        var pem = _secrets.Resolve(options.PrivateKeyRef, "SSH private key");
        byte[] bytes;
        using (var ms = new MemoryStream())
        using (var w = new StreamWriter(ms))
        {
            w.Write(pem);
            w.Flush();
            bytes = ms.ToArray();
        }
        PrivateKeyFile keyFile;
        if (options.PassphraseRef is not null)
        {
            var passphrase = _secrets.Resolve(options.PassphraseRef, "SSH key passphrase");
            keyFile = new PrivateKeyFile(new MemoryStream(bytes), passphrase);
        }
        else
        {
            keyFile = new PrivateKeyFile(new MemoryStream(bytes));
        }
        return new PrivateKeyAuthenticationMethod(options.Username, keyFile);
    }

    public async Task<(SshClient Client, string Fingerprint, string Algorithm)> ConnectAsync(
        SshOptions options, CancellationToken ct)
    {
        _logger.LogInformation("SSH connect: {User}@{Host}:{Port} auth={Auth} policy={Policy}",
            options.Username, options.Host, options.Port, options.AuthMode, options.HostKeyPolicy);

        var info = BuildConnectionInfo(options);
        var client = new SshClient(info);

        string? presentedFp = null;
        client.HostKeyReceived += (_, e) =>
        {
            var decision = _verifier.Verify(options.Host, options.Port, e.HostKey, options.HostKeyPolicy,
                options.PinnedHostKeyFingerprint);
            presentedFp = HostKeyFormatter.Fingerprint(e.HostKey);
            e.CanTrust = decision is HostKeyDecision.Accepted or HostKeyDecision.FirstUseAccepted;
            if (!e.CanTrust)
            {
                _logger.LogError("SSH host key REJECTED for {Host}:{Port} decision={Decision} fingerprint={Fp}",
                    options.Host, options.Port, decision, presentedFp);
            }
        };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.ConnectTimeout);
            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (SshAuthenticationException ex)
        {
            client.Dispose();
            throw new TunnelException(TunnelErrorCategory.SshAuthentication,
                $"SSH authentication failed for {options.Username}@{options.Host}: {ex.Message}", ex);
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw TunnelException.FromIo(ex, $"SSH server {options.Host}:{options.Port}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TunnelException(TunnelErrorCategory.Timeout,
                $"SSH connection to {options.Host}:{options.Port} timed out after {options.ConnectTimeout.TotalSeconds:0}s.");
        }
        catch (SshConnectionException ex)
        {
            client.Dispose();
            throw new TunnelException(TunnelErrorCategory.SshProtocol,
                $"SSH protocol error with {options.Host}:{options.Port}: {ex.Message}", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        if (!client.IsConnected)
        {
            client.Dispose();
            throw new TunnelException(TunnelErrorCategory.SshProtocol, "SSH handshake finished but the session is not connected.");
        }

        var fp = presentedFp ?? _verifier.GetSessionFingerprint(options.Host, options.Port) ?? "unknown";
        _logger.LogInformation("SSH session established, host key {Fp}", fp);
        return (client, fp, "ssh");
    }

    public async Task<SshTestResult> TestAsync(SshOptions options, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (_, fingerprint, _) = await ConnectAsync(options, ct).ConfigureAwait(false);
            sw.Stop();
            return new SshTestResult(true,
                $"Authenticated as {options.Username}. Host key {fingerprint}.",
                fingerprint, sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var message = ex is TunnelException te
                ? te.Message
                : $"SSH test failed: {ex.Message}";
            return new SshTestResult(false, message, null, sw.Elapsed);
        }
    }
}
