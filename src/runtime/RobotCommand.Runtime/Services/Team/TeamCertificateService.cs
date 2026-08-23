using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RobotCommand.Services.Team;

public sealed record TeamServerCertificate(X509Certificate2 Certificate, string Sha256Fingerprint, string InstanceId);

public interface ITeamCertificateService
{
    TeamServerCertificate GetOrCreate();
}

public sealed class TeamCertificateService : ITeamCertificateService
{
    private const string MarkerOid = "1.3.6.1.4.1.61452.1.1";
    private readonly object _gate = new();
    private TeamServerCertificate? _current;

    public TeamServerCertificate GetOrCreate()
    {
        lock (_gate) return _current ??= LoadOrCreate();
    }

    private static TeamServerCertificate LoadOrCreate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var existing = store.Certificates
            .Find(X509FindType.FindByTimeValid, DateTime.Now, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(certificate => certificate.HasPrivateKey &&
                certificate.Extensions.Cast<X509Extension>().Any(extension => extension.Oid?.Value == MarkerOid));
        if (existing is not null) return Describe(existing);

        var instanceId = Guid.NewGuid().ToString("N");
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN=Robot Command {Environment.MachineName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509Extension(new Oid(MarkerOid), System.Text.Encoding.UTF8.GetBytes(instanceId), false));

        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var persisted = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx, password),
            password,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        store.Add(persisted);
        return Describe(persisted);
    }

    private static TeamServerCertificate Describe(X509Certificate2 certificate)
    {
        var marker = certificate.Extensions.Cast<X509Extension>().First(extension => extension.Oid?.Value == MarkerOid);
        var instanceId = System.Text.Encoding.UTF8.GetString(marker.RawData);
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return new TeamServerCertificate(certificate, fingerprint, instanceId);
    }
}
