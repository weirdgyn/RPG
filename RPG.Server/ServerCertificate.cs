using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RPG.Server;

/// <summary>
/// Certificato TLS del server: autofirmato, creato al primo avvio e salvato in un file PFX (senza password,
/// protetto solo dai permessi del file system: è una chiave privata, non va pubblicata né condivisa).
/// I client non si fidano di una CA ma dell'impronta del certificato, memorizzata alla prima connessione.
/// </summary>
public static class ServerCertificate
{
    public const string FileName = "server.pfx";

    /// <summary>Carica il certificato da <paramref name="path"/>; se il file non esiste ne crea uno nuovo e lo salva.</summary>
    public static X509Certificate2 LoadOrCreate(string path, out bool created)
    {
        if (File.Exists(path))
        {
            created = false;
            return new X509Certificate2(path, (string?)null, X509KeyStorageFlags.Exportable);
        }

        created = true;
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=RPG Server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        var serverAuth = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(serverAuth, critical: false));

        var now = DateTimeOffset.UtcNow;
        using var selfSigned = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        byte[] pfx = selfSigned.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, pfx);

        // Esporto e reimporto: su Windows SslStream non accetta le chiavi "effimere" appena generate.
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
    }
}
