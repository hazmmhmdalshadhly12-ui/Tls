using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace HAZEM.Tunnel.Core.Secrets;

/// <summary>
/// Removes secret material from text before it reaches logs or the UI.
/// Combines regex patterns (passwords, tokens, keys in JSON) with a registry of exact
/// secret values seen at runtime. Registered secrets are matched longest-first.
/// </summary>
public sealed class SecretRedactor
{
    public const string Mask = "*****";

    private static readonly Regex[] Patterns =
    [
        // JSON-ish fields whose name smells secret: "password":"v"
        new Regex("""(?i)("?\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|passphrase)"?\b"?\s*[:=]\s*")([^"]{1,512})(")""", RegexOptions.Compiled),
        new Regex("""(?i)("?\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key|passphrase)"?\b"?\s*[:=]\s*')([^']{1,512})(')""", RegexOptions.Compiled),
        // Unquoted assignment (yaml/ini/log style): password=s3cr3t (prefix keeps the separator)
        new Regex("""(?i)("?\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|passphrase)"?\b\s*[:=]\s*)([^\s"',;]{3,512})""", RegexOptions.Compiled),
        // PEM key bodies
        new Regex("""(?s)(-----BEGIN [A-Z ]*PRIVATE KEY-----).+?(-----END [A-Z ]*PRIVATE KEY-----)""", RegexOptions.Compiled),
        // Bearer / Basic auth headers
        new Regex("""(?i)\b(authorization\s*:\s*(?:bearer|basic)\s+)([A-Za-z0-9\-._~+/=]{4,512})""", RegexOptions.Compiled),
        // OpenSSH private key first line marker
        new Regex("""(?i)(ssh-rsa|ssh-ed25519|ecdsa-sha2-\S+)\s+(AAAA[A-Za-z0-9+/=]{16,})""", RegexOptions.Compiled),
    ];

    private readonly ConcurrentDictionary<string, byte> _secrets = new();

    public void Register(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 3) return;
        _secrets[secret] = 0;
    }

    public void Unregister(string secret)
    {
        if (!string.IsNullOrEmpty(secret)) _secrets.TryRemove(secret, out _);
    }

    public int RegisteredCount => _secrets.Count;

    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var text = input;
        foreach (var pattern in Patterns)
            text = pattern.Replace(text, static m =>
            {
                // 3-group patterns: prefix + secret + suffix → mask the middle only.
                if (m.Groups.Count >= 4 && m.Groups[3].Value.Length > 0)
                    return m.Groups[1].Value + Mask + m.Groups[3].Value;
                return m.Groups[1].Value + Mask;
            });

        if (!_secrets.IsEmpty)
        {
            var longest = _secrets.Keys.OrderByDescending(k => k.Length);
            foreach (var s in longest)
            {
                if (text.Contains(s, StringComparison.Ordinal))
                    text = text.Replace(s, Mask, StringComparison.Ordinal);
            }
        }
        return text;
    }

    /// <summary>Returns true when the text still contains any registered secret (used in tests and the log provider self-check).</summary>
    public bool ContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return _secrets.Keys.Any(s => text.Contains(s, StringComparison.Ordinal));
    }
}
