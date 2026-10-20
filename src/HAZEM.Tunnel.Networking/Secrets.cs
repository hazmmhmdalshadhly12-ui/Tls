using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Secrets;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Networking;

/// <summary>
/// Resolves SecretRef values to plaintext at connect time. Plaintext NEVER reaches logs:
/// every resolved secret is registered in the shared SecretRedactor immediately.
/// </summary>
public sealed class SecretResolver
{
    private readonly ICredentialStore _store;
    private readonly SecretRedactor _redactor;
    private readonly ILogger<SecretResolver> _logger;

    public SecretResolver(ICredentialStore store, SecretRedactor redactor, ILogger<SecretResolver> logger)
    {
        _store = store;
        _redactor = redactor;
        _logger = logger;
    }

    public string Resolve(SecretRef? reference, string whatFor)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Key))
            throw new Core.TunnelException(Core.Models.TunnelErrorCategory.Configuration,
                $"Missing credential reference for {whatFor}.");

        var value = _store.GetSecret(reference.Key);
        if (value is null)
            throw new Core.TunnelException(Core.Models.TunnelErrorCategory.Configuration,
                $"Credential '{reference.Key}' for {whatFor} was not found in the OS credential store. " +
                "Re-enter it in the profile editor.");

        _redactor.Register(value); // from here on, this value can never appear in logs
        _logger.LogDebug("Resolved credential for {What} ({Key})", whatFor, reference.Key);
        return value;
    }

    public bool TryPeek(SecretRef? reference, out string value)
    {
        value = string.Empty;
        if (reference is null) return false;
        var v = _store.GetSecret(reference.Key);
        if (v is null) return false;
        value = v;
        return true;
    }
}
