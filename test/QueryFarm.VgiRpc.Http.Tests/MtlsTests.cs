using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using QueryFarm.VgiRpc.Http;
using Xunit;

namespace QueryFarm.VgiRpc.Http.Tests;

/// <summary>Mirrors the canonical Python repo's <c>tests/test_mtls.py</c> coverage.</summary>
public class MtlsTests
{
    // -------------------------------------------------------------------
    // Shared helpers
    // -------------------------------------------------------------------

    private static X509Certificate2 MakeTestCert(string cn = "test-client", int daysValid = 365, TimeSpan? notBeforeOffset = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var notBefore = DateTimeOffset.UtcNow + (notBeforeOffset ?? -TimeSpan.FromHours(1));
        var notAfter = notBefore.AddDays(daysValid);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static string CertToHeader(X509Certificate2 cert) => Uri.EscapeDataString(PemEncode(cert));

    private static string PemEncode(X509Certificate2 cert) => $"-----BEGIN CERTIFICATE-----\n{Convert.ToBase64String(cert.RawData, Base64FormattingOptions.InsertLineBreaks)}\n-----END CERTIFICATE-----\n";

    private static DefaultHttpContext MakeContext(string? certHeader = null, string headerName = "X-SSL-Client-Cert", string? xfcc = null)
    {
        var context = new DefaultHttpContext();
        if (certHeader is not null)
        {
            context.Request.Headers[headerName] = certHeader;
        }

        if (xfcc is not null)
        {
            context.Request.Headers["x-forwarded-client-cert"] = xfcc;
        }

        return context;
    }

    // -------------------------------------------------------------------
    // FromHeader
    // -------------------------------------------------------------------

    [Fact]
    public async Task FromHeader_ValidCert_CallsValidateAndStoresIdentity()
    {
        using var cert = MakeTestCert("alice");
        var authFn = MtlsAuth.FromHeader(c => new MtlsIdentity(c.GetNameInfo(X509NameType.SimpleName, false)));
        var context = MakeContext(certHeader: CertToHeader(cert));

        await authFn(context);

        var identity = MtlsAuth.GetIdentity(context);
        Assert.NotNull(identity);
        Assert.Equal("alice", identity!.Principal);
    }

    [Fact]
    public async Task FromHeader_InvalidPem_ThrowsInvalidCredential()
    {
        var authFn = MtlsAuth.FromHeader(_ => new MtlsIdentity(""));
        var context = MakeContext(certHeader: Uri.EscapeDataString("not a certificate"));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authFn(context));
        Assert.Equal(AuthReason.InvalidCredential, exc.Reason);
    }

    [Fact]
    public async Task FromHeader_MissingHeader_ThrowsProxyRequired()
    {
        var authFn = MtlsAuth.FromHeader(_ => new MtlsIdentity(""));
        var context = MakeContext();

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authFn(context));
        Assert.Equal(AuthReason.ProxyRequired, exc.Reason);
    }

    [Fact]
    public async Task FromHeader_CustomHeaderName_IsRespected()
    {
        using var cert = MakeTestCert("bob");
        var authFn = MtlsAuth.FromHeader(_ => new MtlsIdentity("bob"), header: "X-Amzn-Mtls-Clientcert");
        var context = MakeContext(certHeader: CertToHeader(cert), headerName: "X-Amzn-Mtls-Clientcert");

        await authFn(context);

        Assert.Equal("bob", MtlsAuth.GetIdentity(context)!.Principal);
    }

    [Fact]
    public async Task FromHeader_ValidateRejection_Propagates()
    {
        using var cert = MakeTestCert("evil");
        var authFn = MtlsAuth.FromHeader(_ => throw new AuthFailure(AuthReason.InvalidCredential, "certificate revoked"));
        var context = MakeContext(certHeader: CertToHeader(cert));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authFn(context));
        Assert.Equal("certificate revoked", exc.Detail);
    }

    [Fact]
    public async Task FromHeader_CheckExpiry_ExpiredCert_ThrowsExpiredCredential()
    {
        using var cert = MakeTestCert("expired", daysValid: 0, notBeforeOffset: -TimeSpan.FromDays(2));
        var authFn = MtlsAuth.FromHeader(_ => new MtlsIdentity("x"), checkExpiry: true);
        var context = MakeContext(certHeader: CertToHeader(cert));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authFn(context));
        Assert.Equal(AuthReason.ExpiredCredential, exc.Reason);
        Assert.Contains("expired", exc.Detail);
    }

    [Fact]
    public async Task FromHeader_CheckExpiry_NotYetValidCert_ThrowsExpiredCredential()
    {
        using var cert = MakeTestCert("future", notBeforeOffset: TimeSpan.FromDays(30));
        var authFn = MtlsAuth.FromHeader(_ => new MtlsIdentity("x"), checkExpiry: true);
        var context = MakeContext(certHeader: CertToHeader(cert));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authFn(context));
        Assert.Equal(AuthReason.ExpiredCredential, exc.Reason);
        Assert.Contains("not yet valid", exc.Detail);
    }

    [Fact]
    public async Task FromHeader_CheckExpiryFalse_DoesNotValidateExpiry()
    {
        using var cert = MakeTestCert("expired", daysValid: 0, notBeforeOffset: -TimeSpan.FromDays(2));
        var authFn = MtlsAuth.FromHeader(_ => new MtlsIdentity("still-accepted"));
        var context = MakeContext(certHeader: CertToHeader(cert));

        await authFn(context); // does not throw

        Assert.Equal("still-accepted", MtlsAuth.GetIdentity(context)!.Principal);
    }

    // -------------------------------------------------------------------
    // FromFingerprint
    // -------------------------------------------------------------------

    [Fact]
    public async Task FromFingerprint_KnownFingerprint_Accepted()
    {
        using var cert = MakeTestCert("known");
        var fp = Convert.ToHexStringLower(cert.GetCertHash(HashAlgorithmName.SHA256));
        var authFn = MtlsAuth.FromFingerprint(new Dictionary<string, MtlsIdentity> { [fp] = new("known-client") });
        var context = MakeContext(certHeader: CertToHeader(cert));

        await authFn(context);

        Assert.Equal("known-client", MtlsAuth.GetIdentity(context)!.Principal);
    }

    [Fact]
    public async Task FromFingerprint_UnknownFingerprint_ThrowsInvalidCredential()
    {
        using var cert = MakeTestCert("unknown");
        var authFn = MtlsAuth.FromFingerprint(new Dictionary<string, MtlsIdentity> { ["deadbeef"] = new("x") });
        var context = MakeContext(certHeader: CertToHeader(cert));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authFn(context));
        Assert.Equal(AuthReason.InvalidCredential, exc.Reason);
    }

    [Fact]
    public async Task FromFingerprint_CustomAlgorithmSha1_Works()
    {
        using var cert = MakeTestCert("sha1-client");
        var fp = Convert.ToHexStringLower(cert.GetCertHash(HashAlgorithmName.SHA1));
        var authFn = MtlsAuth.FromFingerprint(new Dictionary<string, MtlsIdentity> { [fp] = new("sha1-ok") }, algorithm: "sha1");
        var context = MakeContext(certHeader: CertToHeader(cert));

        await authFn(context);

        Assert.Equal("sha1-ok", MtlsAuth.GetIdentity(context)!.Principal);
    }

    [Fact]
    public void FromFingerprint_UnsupportedAlgorithm_ThrowsAtConstructionTime()
    {
        Assert.Throws<ArgumentException>(() => MtlsAuth.FromFingerprint(new Dictionary<string, MtlsIdentity> { ["abc"] = new("x") }, algorithm: "md5"));
    }

    // -------------------------------------------------------------------
    // FromSubject
    // -------------------------------------------------------------------

    [Fact]
    public async Task FromSubject_CnExtraction_UsedAsPrincipal()
    {
        using var cert = MakeTestCert("my-service");
        var authFn = MtlsAuth.FromSubject();
        var context = MakeContext(certHeader: CertToHeader(cert));

        await authFn(context);

        var identity = MtlsAuth.GetIdentity(context)!;
        Assert.Equal("my-service", identity.Principal);
        Assert.Equal("mtls", identity.Domain);
    }

    [Fact]
    public async Task FromSubject_AllowedSubjectsPass_Accepted()
    {
        using var cert = MakeTestCert("allowed");
        var authFn = MtlsAuth.FromSubject(allowedSubjects: new HashSet<string> { "allowed", "also-ok" });
        var context = MakeContext(certHeader: CertToHeader(cert));

        await authFn(context);

        Assert.Equal("allowed", MtlsAuth.GetIdentity(context)!.Principal);
    }

    [Fact]
    public async Task FromSubject_AllowedSubjectsReject_ThrowsInsufficientScope()
    {
        using var cert = MakeTestCert("forbidden");
        var authFn = MtlsAuth.FromSubject(allowedSubjects: new HashSet<string> { "allowed" });
        var context = MakeContext(certHeader: CertToHeader(cert));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authFn(context));
        Assert.Equal(AuthReason.InsufficientScope, exc.Reason);
    }

    [Fact]
    public async Task FromSubject_ClaimsContainSerialAndValidity()
    {
        using var cert = MakeTestCert("claims-test");
        var authFn = MtlsAuth.FromSubject();
        var context = MakeContext(certHeader: CertToHeader(cert));

        await authFn(context);

        var claims = MtlsAuth.GetIdentity(context)!.Claims;
        Assert.Contains("subject_dn", claims.Keys);
        Assert.Contains("serial", claims.Keys);
        Assert.Contains("not_valid_after", claims.Keys);
        Assert.True(long.TryParse((string)claims["serial"], System.Globalization.NumberStyles.HexNumber, null, out _) || ((string)claims["serial"]).Length > 0);
    }

    [Fact]
    public async Task FromSubject_CheckExpiry_ExpiredCert_Rejected()
    {
        using var cert = MakeTestCert("expired-subj", daysValid: 0, notBeforeOffset: -TimeSpan.FromDays(2));
        var authFn = MtlsAuth.FromSubject(checkExpiry: true);
        var context = MakeContext(certHeader: CertToHeader(cert));

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authFn(context));
        Assert.Equal(AuthReason.ExpiredCredential, exc.Reason);
    }

    // -------------------------------------------------------------------
    // ParseXfcc
    // -------------------------------------------------------------------

    [Fact]
    public void ParseXfcc_SimpleSingleElement()
    {
        var result = MtlsAuth.ParseXfcc("Hash=abc123;Subject=\"CN=client1\"");

        Assert.Single(result);
        Assert.Equal("abc123", result[0].Hash);
        Assert.Equal("CN=client1", result[0].Subject);
    }

    [Fact]
    public void ParseXfcc_MultipleElements()
    {
        var result = MtlsAuth.ParseXfcc("Hash=a;Subject=\"CN=first\",Hash=b;Subject=\"CN=second\"");

        Assert.Equal(2, result.Count);
        Assert.Equal("CN=first", result[0].Subject);
        Assert.Equal("CN=second", result[1].Subject);
    }

    [Fact]
    public void ParseXfcc_QuotedSubjectWithCommas_DoesNotSplit()
    {
        var result = MtlsAuth.ParseXfcc("Subject=\"CN=test,O=Acme\\, Inc.\"");

        Assert.Single(result);
        Assert.Equal("CN=test,O=Acme, Inc.", result[0].Subject);
    }

    [Fact]
    public void ParseXfcc_QuotedSubjectWithSemicolons_DoesNotSplit()
    {
        var result = MtlsAuth.ParseXfcc("Subject=\"CN=test;extra=val\";Hash=abc");

        Assert.Single(result);
        Assert.Equal("CN=test;extra=val", result[0].Subject);
        Assert.Equal("abc", result[0].Hash);
    }

    [Fact]
    public void ParseXfcc_UrlEncodedCertField_IsDecoded()
    {
        var encoded = Uri.EscapeDataString("-----BEGIN CERTIFICATE-----\nMIIB...\n-----END CERTIFICATE-----\n");
        var result = MtlsAuth.ParseXfcc($"Cert={encoded}");

        Assert.Single(result);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", result[0].Cert);
    }

    [Fact]
    public void ParseXfcc_EmptyHeader_ReturnsEmptyList()
    {
        Assert.Empty(MtlsAuth.ParseXfcc(""));
    }

    [Fact]
    public void ParseXfcc_MultipleDns_CollectedIntoList()
    {
        var result = MtlsAuth.ParseXfcc("DNS=a.example.com;DNS=b.example.com");

        Assert.Single(result);
        Assert.Equal(["a.example.com", "b.example.com"], result[0].Dns);
    }

    [Fact]
    public void ParseXfcc_UriField_IsDecoded()
    {
        var encoded = Uri.EscapeDataString("spiffe://cluster.local/ns/default/sa/client");
        var result = MtlsAuth.ParseXfcc($"URI={encoded}");

        Assert.Equal("spiffe://cluster.local/ns/default/sa/client", result[0].Uri);
    }

    [Fact]
    public void ParseXfcc_ByField_IsDecoded()
    {
        var encoded = Uri.EscapeDataString("spiffe://cluster.local/ns/default/sa/server");
        var result = MtlsAuth.ParseXfcc($"By={encoded}");

        Assert.Equal("spiffe://cluster.local/ns/default/sa/server", result[0].By);
    }

    // -------------------------------------------------------------------
    // Xfcc authenticate delegate
    // -------------------------------------------------------------------

    [Fact]
    public async Task Xfcc_ValidHeader_ExtractsPrincipalFromSubjectCn()
    {
        var authFn = MtlsAuth.Xfcc();
        var context = MakeContext(xfcc: "Hash=abc;Subject=\"CN=client1,O=Acme\"");

        await authFn(context);

        var identity = MtlsAuth.GetIdentity(context)!;
        Assert.Equal("client1", identity.Principal);
        Assert.Equal("mtls", identity.Domain);
    }

    [Fact]
    public async Task Xfcc_MissingHeader_ThrowsProxyRequired()
    {
        var authFn = MtlsAuth.Xfcc();
        var context = MakeContext();

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authFn(context));
        Assert.Equal(AuthReason.ProxyRequired, exc.Reason);
    }

    [Fact]
    public async Task Xfcc_CustomValidate_IsUsed()
    {
        var authFn = MtlsAuth.Xfcc(validate: elem => elem.Hash == "trusted"
            ? new MtlsIdentity("validated", "xfcc")
            : throw new AuthFailure(AuthReason.InvalidCredential, "untrusted"));
        var context = MakeContext(xfcc: "Hash=trusted");

        await authFn(context);

        Assert.Equal("validated", MtlsAuth.GetIdentity(context)!.Principal);
    }

    [Fact]
    public async Task Xfcc_CustomValidateRejection_Propagates()
    {
        var authFn = MtlsAuth.Xfcc(validate: _ => throw new AuthFailure(AuthReason.InvalidCredential, "nope"));
        var context = MakeContext(xfcc: "Hash=whatever");

        var exc = await Assert.ThrowsAsync<AuthFailure>(() => authFn(context));
        Assert.Equal("nope", exc.Detail);
    }

    [Fact]
    public async Task Xfcc_SelectElementFirst_UsesOriginalClient()
    {
        var authFn = MtlsAuth.Xfcc(selectElement: MtlsAuth.XfccSelectElement.First);
        var context = MakeContext(xfcc: "Subject=\"CN=original\",Subject=\"CN=proxy\"");

        await authFn(context);

        Assert.Equal("original", MtlsAuth.GetIdentity(context)!.Principal);
    }

    [Fact]
    public async Task Xfcc_SelectElementLast_UsesNearestProxy()
    {
        var authFn = MtlsAuth.Xfcc(selectElement: MtlsAuth.XfccSelectElement.Last);
        var context = MakeContext(xfcc: "Subject=\"CN=original\",Subject=\"CN=proxy\"");

        await authFn(context);

        Assert.Equal("proxy", MtlsAuth.GetIdentity(context)!.Principal);
    }

    [Fact]
    public async Task Xfcc_ClaimsPopulatedFromFields()
    {
        var encodedUri = Uri.EscapeDataString("spiffe://cluster/ns/default");
        var authFn = MtlsAuth.Xfcc();
        var context = MakeContext(xfcc: $"Hash=deadbeef;Subject=\"CN=svc\";URI={encodedUri}");

        await authFn(context);

        var claims = MtlsAuth.GetIdentity(context)!.Claims;
        Assert.Equal("deadbeef", claims["hash"]);
        Assert.Equal("CN=svc", claims["subject"]);
        Assert.Equal("spiffe://cluster/ns/default", claims["uri"]);
    }
}
