using HAZEM.Tunnel.Core.Secrets;
using Xunit;

namespace HAZEM.Tunnel.Core.Tests;

public class SecretRedactorTests
{
    [Fact]
    public void JsonPasswordField_IsMasked()
    {
        var r = new SecretRedactor();
        var outText = r.Redact("""{"username":"ali","password":"hunter2!"}""");
        Assert.DoesNotContain("hunter2", outText);
        Assert.Contains(SecretRedactor.Mask, outText);
        Assert.Contains("ali", outText);
    }

    [Fact]
    public void YamlStyleAssignment_IsMasked()
    {
        var r = new SecretRedactor();
        var outText = r.Redact("password: s3cr3t-value");
        Assert.DoesNotContain("s3cr3t", outText);
    }

    [Fact]
    public void PemPrivateKeyBlock_IsMasked()
    {
        var r = new SecretRedactor();
        var pem = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAAVERYSECRETCONTENT\n-----END OPENSSH PRIVATE KEY-----";
        var outText = r.Redact(pem);
        Assert.DoesNotContain("AAAAVERYSECRET", outText);
    }

    [Fact]
    public void BearerHeader_IsMasked()
    {
        var r = new SecretRedactor();
        var outText = r.Redact("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.e30.x");
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", outText);
    }

    [Fact]
    public void RegisteredSecret_IsMasked_LongestFirst()
    {
        var r = new SecretRedactor();
        r.Register("shortsecret");
        r.Register("longer-secret-value-123456");
        var outText = r.Redact("token=longer-secret-value-123456 and token=shortsecret");
        Assert.DoesNotContain("shortsecret", outText);
        Assert.DoesNotContain("longer-secret", outText);
        Assert.Equal(2, outText.Split(SecretRedactor.Mask).Length - 1);
    }

    [Fact]
    public void ShortSecrets_AreIgnored()
    {
        var r = new SecretRedactor();
        r.Register("ab"); // below minimum length
        Assert.Equal(0, r.RegisteredCount);
    }

    [Fact]
    public void ContainsSecret_DetectsLeak()
    {
        var r = new SecretRedactor();
        r.Register("topsecret-123");
        Assert.True(r.ContainsSecret("log line with topsecret-123 inside"));
        Assert.False(r.ContainsSecret("clean line"));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var r = new SecretRedactor();
        Assert.Equal(string.Empty, r.Redact(null));
        Assert.Equal(string.Empty, r.Redact(""));
    }

    [Fact]
    public void Unregister_StopsMasking()
    {
        var r = new SecretRedactor();
        r.Register("mysecret-xyz");
        r.Unregister("mysecret-xyz");
        Assert.Contains("mysecret-xyz", r.Redact("value mysecret-xyz"));
    }

    [Fact]
    public void NormalText_Unchanged()
    {
        var r = new SecretRedactor();
        var text = "Tunnel connected via ssh.example.com in 240 ms. Health check passed.";
        Assert.Equal(text, r.Redact(text));
    }
}
