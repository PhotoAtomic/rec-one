using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DiaryApp.Shared.Abstractions;

namespace DiaryApp.Server;

public sealed class HttpsCertificateService
{
    private readonly string? _certificatePath;
    private readonly string? _certificatePassword;

    public HttpsCertificateService(IConfiguration configuration)
    {
        var certificateSection = configuration.GetSection("Kestrel:Endpoints:Https:Certificate");
        _certificatePath = certificateSection["Path"];
        _certificatePassword = certificateSection["Password"];
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_certificatePath);

    public HttpsCertificateInfo GetInfo()
        => new(IsConfigured, GetSuggestedFileName());

    public CertificateExportResult TryExportPublicCertificate()
    {
        if (!IsConfigured)
        {
            return CertificateExportResult.CreateNotConfigured();
        }

        if (!File.Exists(_certificatePath))
        {
            return CertificateExportResult.CreateMissingFile(GetSuggestedFileName());
        }

        try
        {
            var certBytes = File.ReadAllBytes(_certificatePath!);
            var passwordSpan = _certificatePassword is null ? ReadOnlySpan<char>.Empty : _certificatePassword.AsSpan();
            var certificate = X509CertificateLoader.LoadPkcs12(certBytes, passwordSpan);

            var publicBytes = certificate.Export(X509ContentType.Cert);
            var base64 = Convert.ToBase64String(publicBytes, Base64FormattingOptions.InsertLineBreaks);
            var pem = $"-----BEGIN CERTIFICATE-----\n{base64}\n-----END CERTIFICATE-----\n";

            return CertificateExportResult.CreateSuccess(pem, GetSuggestedFileName());
        }
        catch (CryptographicException cryptoEx)
        {
            return CertificateExportResult.CreateFailure($"Unable to load HTTPS certificate: {cryptoEx.Message}");
        }
        catch (Exception ex)
        {
            return CertificateExportResult.CreateFailure($"Unexpected error loading HTTPS certificate: {ex.Message}");
        }
    }

    private string GetSuggestedFileName()
    {
        if (string.IsNullOrWhiteSpace(_certificatePath))
        {
            return "https-certificate.pem";
        }

        var name = Path.GetFileNameWithoutExtension(_certificatePath);
        return string.IsNullOrWhiteSpace(name) ? "https-certificate.pem" : $"{name}.pem";
    }

    public sealed record CertificateExportResult(bool Success, bool NotConfigured, bool MissingFile, string? PemContents, string? FileName, string? Error)
    {
        public static CertificateExportResult CreateNotConfigured()
            => new(false, true, false, null, null, "HTTPS endpoint is not configured.");

        public static CertificateExportResult CreateMissingFile(string fileName)
            => new(false, false, true, null, fileName, "Configured HTTPS certificate file was not found.");

        public static CertificateExportResult CreateFailure(string error)
            => new(false, false, false, null, null, error);

        public static CertificateExportResult CreateSuccess(string pemContents, string fileName)
            => new(true, false, false, pemContents, fileName, null);
    }

}
