// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Transport;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

internal static class Backends
{
    public static string[] ForCurrentOs { get; } = OperatingSystem.IsWindows() ? ["iocp", "rio"] : ["epoll", "iouring"];

    // BEDROCK_INLINE=1 completes receives on the provider worker (NetworkEngineOptions.CompleteReceivesInline) in every mode of the sample.
    public static NetworkEngineOptions Create(string name, int workers)
    {
        NetworkEngineOptions options = name switch
        {
            "iocp" => new IocpEngineOptions { WorkerCount = workers },
            "rio" => new RioEngineOptions { WorkerCount = workers },
            "epoll" => new EpollEngineOptions { WorkerCount = workers },
            "iouring" => new IoUringEngineOptions { WorkerCount = workers },
            _ => throw new ArgumentException($"Unknown backend '{name}'. Use iocp, rio, epoll or iouring.")
        };
        options.CompleteReceivesInline = Environment.GetEnvironmentVariable("BEDROCK_INLINE") == "1";
        return options;
    }
}

internal static class TlsFixture
{
    public static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    public static PemFiles WritePem(X509Certificate2 certificate)
    {
        var folder = Directory.CreateTempSubdirectory("bedrock-tls-").FullName;
        var certificatePath = Path.Combine(folder, "certificate.pem");
        var keyPath = Path.Combine(folder, "key.pem");
        using var key = certificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("The fixture requires an RSA key.");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return new PemFiles(folder, certificatePath, keyPath);
    }

    internal sealed class PemFiles(string folder, string certificatePath, string privateKeyPath) : IDisposable
    {
        public string CertificatePath { get; } = certificatePath;

        public string PrivateKeyPath { get; } = privateKeyPath;

        public void Dispose() => Directory.Delete(folder, recursive: true);
    }
}

internal sealed class Arguments(Dictionary<string, string> values)
{
    public static Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                values[args[i][2..]] = i + 1 < args.Length ? args[++i] : "true";
            }
        }
        return new Arguments(values);
    }

    public string Get(string key, string fallback) => values.TryGetValue(key, out var value) ? value : fallback;

    public int Get(string key, int fallback) => values.TryGetValue(key, out var value) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
}
