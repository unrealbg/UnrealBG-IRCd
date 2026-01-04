namespace IRCd.Transport.Tls
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;

    internal static class SelfSignedCertificateGenerator
    {
        public static X509Certificate2 CreateAndPersistPfx(
            string contentRoot,
            string pfxPath,
            string password,
            string commonName,
            int daysValid)
        {
            if (string.IsNullOrWhiteSpace(pfxPath))
                throw new ArgumentException("PFX path is required", nameof(pfxPath));

            if (!Path.IsPathRooted(pfxPath))
            {
                pfxPath = Path.Combine(contentRoot, pfxPath);
            }

            var dir = Path.GetDirectoryName(pfxPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            daysValid = Math.Clamp(daysValid, 1, 3650);
            commonName = string.IsNullOrWhiteSpace(commonName) ? "localhost" : commonName.Trim();

            using var rsa = RSA.Create(2048);

            var subject = new X500DistinguishedName($"CN={commonName}");
            var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                    critical: false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(commonName);
            san.AddDnsName("localhost");
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
            req.CertificateExtensions.Add(san.Build());

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = notBefore.AddDays(daysValid);

            using var cert = req.CreateSelfSigned(notBefore, notAfter);

            var bytes = cert.Export(X509ContentType.Pkcs12, password);
            File.WriteAllBytes(pfxPath, bytes);

            return X509CertificateLoader.LoadPkcs12(bytes, password);
        }
    }
}
