using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Hostix.Core.Services;
using Serilog;

namespace Hostix.Modules.Services
{
    public class SSLManager : ISSLManager
    {
        private readonly string _sslRoot;
        private readonly string _certsRoot;

        public SSLManager()
        {
            _sslRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ssl");
            if (!Directory.Exists(_sslRoot)) Directory.CreateDirectory(_sslRoot);
            
            _certsRoot = Path.Combine(_sslRoot, "certs");
            if (!Directory.Exists(_certsRoot)) Directory.CreateDirectory(_certsRoot);
        }

        public bool GenerateCert(string domain)
        {
            try
            {
                Log.Information("[SSLManager] Orchestrating trusted SSL generation for: {Domain}", domain);
                
                var certDir = Path.Combine(_certsRoot, domain);
                if (!Directory.Exists(certDir)) Directory.CreateDirectory(certDir);

                var certPath = Path.Combine(certDir, "server.crt");
                var keyPath = Path.Combine(certDir, "server.key");

                // 1. Load or Create Root CA
                using var rootCA = GetOrCreateRootCA();

                // 2. Check if existing cert is already trusted by THIS CA
                if (File.Exists(certPath))
                {
                    try
                    {
                        using var existingCert = new X509Certificate2(certPath);
                        // If "Issued By" matches our Root CA, it's already good.
                        if (existingCert.Issuer == rootCA.Subject && existingCert.NotAfter > DateTime.Now.AddMonths(1))
                        {
                            Log.Information("[SSLManager] Existing trusted certificate for {Domain} is still valid. Skipping regeneration.", domain);
                            return true;
                        }
                        Log.Warning("[SSLManager] Existing cert for {Domain} is self-signed or from old CA. Purging for regeneration.", domain);
                    }
                    catch { /* Invalid cert file, proceed to regenerate */ }
                }

                // 3. Generate Domain Private Key
                using var domainRsa = RSA.Create(2048);

                // 4. Create Certificate Request
                var request = new CertificateRequest($"CN={domain}", domainRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                
                var sanBuilder = new SubjectAlternativeNameBuilder();
                sanBuilder.AddDnsName(domain);
                sanBuilder.AddDnsName("localhost");
                request.CertificateExtensions.Add(sanBuilder.Build());

                // 5. Add Enhanced Key Usage (Server Auth) - CRITICAL for Chrome
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

                // 6. Add Authority Key Identifier (link to Root CA)
                request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(rootCA, true, false));
                request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

                // 7. SIGN with Root CA
                var serialNumber = Guid.NewGuid().ToByteArray();
                // Ensure we use the private key from the rootCA object
                using var signedCert = request.Create(rootCA, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1), serialNumber);
                
                // Export the domain cert and domain key as PEM for Nginx.
                var certPem = signedCert.ExportCertificatePem();
                var keyPem = domainRsa.ExportRSAPrivateKeyPem();

                File.WriteAllText(certPath, certPem.Replace("\r\n", "\n"));
                File.WriteAllText(keyPath, keyPem.Replace("\r\n", "\n"));

                Log.Information("[SSLManager] Trusted certificate chain generated and saved for {Domain}", domain);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SSLManager] Failed to generate trusted SSL for {Domain}", domain);
                return false;
            }
        }

        private X509Certificate2 GetOrCreateRootCA()
        {
            var rootPfxPath = Path.Combine(_sslRoot, "HostixRootCA.pfx");
            var password = "hostix-dev-root";

            if (File.Exists(rootPfxPath))
            {
                try
                {
                    return new X509Certificate2(rootPfxPath, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[SSLManager] Failed to load existing Root CA. It might be corrupted. Re-initializing...");
                    File.Delete(rootPfxPath);
                }
            }

            Log.Information("[SSLManager] Initializing NEW Hostix Trusted Root Authority...");
            using var rsa = RSA.Create(4096);
            var request = new CertificateRequest("CN=Hostix Local Development Root CA", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
            
            // Export with private key
            File.WriteAllBytes(rootPfxPath, cert.Export(X509ContentType.Pfx, password));

            // Install to system store
            TryInstallToRootStore(cert);

            return new X509Certificate2(cert.Export(X509ContentType.Pfx, password), password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        private void TryInstallToRootStore(X509Certificate2 cert)
        {
            var certPath = Path.Combine(_sslRoot, "HostixRootCA.crt");
            File.WriteAllBytes(certPath, cert.Export(X509ContentType.Cert));

            try
            {
                Log.Information("[SSLManager] Attempting system-wide trust installation via certutil...");
                
                // Use certutil to force trust in the Root store
                var psi = new System.Diagnostics.ProcessStartInfo("certutil", $"-addstore -f Root \"{certPath}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = true,
                    Verb = "runas" // Request elevation
                };
                
                var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit();
                
                Log.Information("[SSLManager] certutil trust operation completed.");
            }
            catch (Exception ex)
            {
                Log.Warning("[SSLManager] certutil failed ({Msg}). Falling back to .NET API.", ex.Message);
                
                try
                {
                    using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                    store.Open(OpenFlags.ReadWrite);
                    var existing = store.Certificates.Find(X509FindType.FindBySubjectName, "Hostix Local Development Root CA", false);
                    if (existing.Count == 0)
                    {
                        store.Add(cert);
                        Log.Information("[SSLManager] Root CA added via store.Add (LocalMachine).");
                    }
                }
                catch
                {
                    using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(cert);
                    Log.Information("[SSLManager] Root CA added via store.Add (CurrentUser).");
                }
            }
        }

        public void PurgeCert(string domain)
        {
            try
            {
                var certDir = Path.Combine(_certsRoot, domain);
                if (Directory.Exists(certDir))
                {
                    Directory.Delete(certDir, true);
                    Log.Information("[SSLManager] Purged SSL certificates for {Domain}", domain);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SSLManager] Failed to purge certs for {Domain}", domain);
            }
        }

        public bool InstallRootCA()
        {
            try
            {
                using var cert = GetOrCreateRootCA();
                TryInstallToRootStore(cert);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SSLManager] Manual Root CA installation failed.");
                return false;
            }
        }

        public bool IsCertValid(string domain)
        {
            var certFile = Path.Combine(_certsRoot, domain, "server.crt");
            return File.Exists(certFile);
        }
    }
}
