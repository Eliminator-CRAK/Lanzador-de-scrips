// (Autor: Alex Roman)
// Descripcion: Genera y valida tokens maestros firmados con certificado.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace LanzadorScripts.Servicios;

public sealed class ServicioTokenMaestro
{
    private const string PrefijoToken = "LSMT1";
    private const string HuellaCertificado = "500266A64E574889370D92E5CE0D65D55CC963B7";
    private const string CertificadoPublicoBase64 = "MIIEUDCCArigAwIBAgIQFi8pJshCCatPid+un7OHJzANBgkqhkiG9w0BAQUFADA7MTkwNwYDVQQDDDBMYW56YWRvclNjcmlwdHMgTWFzdGVyIFRva2VuIFNpZ25lciAtIEFsZXggUm9tYW4wHhcNMjYwNTIyMDIzOTEzWhcNNDYwNTIyMDI0OTEzWjA7MTkwNwYDVQQDDDBMYW56YWRvclNjcmlwdHMgTWFzdGVyIFRva2VuIFNpZ25lciAtIEFsZXggUm9tYW4wggGiMA0GCSqGSIb3DQEBAQUAA4IBjwAwggGKAoIBgQDOd49FWtyHc7c1lNlaiMTSbc8eu5pseNylTH/dXWTm1KId4ji64Wb4eBN0QRYBj+B6TwcKRPJCTlcZ+LqG2xAmE3amgnVuY1l1oZiknx6juYS6A00X4WqwFSJInLHgPmOiG8qGvjPSfs9r2GoHm0o4qgoAbJzQ1CT6POpVLWGe7MDd50uIX7T+r/mztU/F2D/CbyU15cPje5E+Zktg7gwDGUHAkUKFhvIevzskuRgnnVHlOI3WQ9RR0ZtYf7qxxri8PbG6M6yUC7siPb7w4tASTx+xnszH0xuDSUA3UIcgYT/6abZXaS6sL4vVONqjOCzrJrwcFQ7qy3M9Wl+jBNu88zUzuyjVcEQtMdz3UumMRrCh0YyscDjHeqbBzPsvSbsyxjAHklFHqDyOgzSdgQXAix+TniRp0DJCQbkIhVNG7TngId40vD4bwbkx9WiZ3mHCzy9EwRAhKwcLV1YUXWaMCzpB1fA7uTzot5DplloPL+Z22pr8zDJPLMzhjIjmDSUCAwEAAaNQME4wDgYDVR0PAQH/BAQDAgeAMB0GA1UdJQQWMBQGCCsGAQUFBwMCBggrBgEFBQcDATAdBgNVHQ4EFgQUfno/gwusO4R8WrCvu0bm+aVs5h4wDQYJKoZIhvcNAQEFBQADggGBAGEa5W8JHwjHJR4cCH8ylMRCb/vCWTXCBZ+OY76nC7WZrx1gn0su+Ijr33x2IrK4FvxgBe0KqH9GX9VTwDn23WsNyLE2Nl3UiLl9G+kQtqjONf20IuuFeKNEIdCUqtU0R8kj2jBugY3sKjxPLfs+rL68mIZizVzSS+HXcV5SVB14y/sunaXqVnEaGg2ft6NSiTdXB1PbcYkCNEYLG73GzOpQEMRWtDfZOeK3W0eOgnY48Dkho5LemDFmED0OrzNJcDp1jdQArfKuZPpHavs2BuRar/0zubTr/K4sONRXCFpadV1o9gWCf21XFxyvKfyMhtO/HqOhQ0dOG3+aw2jVWsx09Rbx2O0IGHBxzRydAMBfp9L8w1MXFCi2DrwYf686BqI+2mcfJZlYzYrRlvnA1Bhfe6v+s9S5xbkLJ8Ej10im/u1w2IPoCK40ef6H8UQo2tg4VmUjzdYvQXvaOyJJ4FPdvvzbd2529ElxV9CDV5xjl4rkqohuUVsu+XAUSlGEWg==";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly X509Certificate2 _certificadoPublico = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(CertificadoPublicoBase64));

    public bool PuedeGenerar()
    {
        using var certificado = BuscarCertificadoPrivado();
        return certificado is not null;
    }

    public string Generar()
    {
        using var certificado = BuscarCertificadoPrivado()
            ?? throw new InvalidOperationException("No se encontro el certificado privado de Alex Roman.");
        using var rsa = certificado.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("El certificado no tiene clave privada RSA.");

        var payload = new TokenMaestroPayload(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            Environment.UserName);
        var payloadJson = JsonSerializer.Serialize(payload, OpcionesJson);
        var payloadBase64 = CodificarBase64Url(Encoding.UTF8.GetBytes(payloadJson));
        var firma = rsa.SignData(Encoding.UTF8.GetBytes(payloadBase64), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{PrefijoToken}.{payloadBase64}.{CodificarBase64Url(firma)}";
    }

    public bool Validar(string? token, out TokenMaestroPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var partes = token.Trim().Split('.');
        if (partes.Length != 3 || partes[0] != PrefijoToken)
        {
            return false;
        }

        try
        {
            var payloadBytes = DecodificarBase64Url(partes[1]);
            var firma = DecodificarBase64Url(partes[2]);
            using var rsa = _certificadoPublico.GetRSAPublicKey();
            if (rsa is null || !rsa.VerifyData(Encoding.UTF8.GetBytes(partes[1]), firma, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                return false;
            }

            payload = JsonSerializer.Deserialize<TokenMaestroPayload>(payloadBytes, OpcionesJson);
            return payload is not null;
        }
        catch
        {
            return false;
        }
    }

    private static X509Certificate2? BuscarCertificadoPrivado()
    {
        using var almacen = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        almacen.Open(OpenFlags.ReadOnly);
        return almacen.Certificates
            .Find(X509FindType.FindByThumbprint, HuellaCertificado, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(certificado => certificado.HasPrivateKey);
    }

    private static string CodificarBase64Url(byte[] datos)
    {
        return Convert.ToBase64String(datos).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] DecodificarBase64Url(string texto)
    {
        var base64 = texto.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }
}

public sealed record TokenMaestroPayload(string Id, DateTimeOffset Creado, string EquipoEmisor, string UsuarioEmisor);
