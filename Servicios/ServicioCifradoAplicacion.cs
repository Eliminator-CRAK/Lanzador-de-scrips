// (Autor: Alex Roman)
// Descripcion: Firma y verifica datos compartidos que viajan entre equipos.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace LanzadorScripts.Servicios;

public sealed class ServicioCifradoAplicacion
{
    private const int Version = 2;
    private const string Algoritmo = "RSA-SHA256";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly RSA? _firmaPruebas;
    private readonly RSA? _verificacionPruebas;
    private readonly string? _emisorPruebas;

    public ServicioCifradoAplicacion()
    {
    }

    public ServicioCifradoAplicacion(RSA firmaPruebas, RSA verificacionPruebas, string emisorPruebas)
    {
        _firmaPruebas = firmaPruebas;
        _verificacionPruebas = verificacionPruebas;
        _emisorPruebas = emisorPruebas;
    }

    public string CifrarTexto(string tipo, string texto)
    {
        return FirmarTexto(tipo, texto);
    }

    public bool IntentarDescifrarTexto(string tipo, string texto, out string claro)
    {
        return IntentarVerificarTexto(tipo, texto, out claro);
    }

    public string FirmarTexto(string tipo, string texto)
    {
        var datos = Convert.ToBase64String(Encoding.UTF8.GetBytes(texto));
        var creado = DateTimeOffset.UtcNow;
        using var certificado = _firmaPruebas is null ? BuscarCertificadoPrivado() : null;
        using var rsaCertificado = certificado?.GetRSAPrivateKey();
        var rsa = _firmaPruebas ?? rsaCertificado
            ?? throw new InvalidOperationException("No se encontro el certificado corporativo de firma.");
        var firma = rsa.SignData(ObtenerBytesFirmados(tipo, datos, creado), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var emisor = _emisorPruebas ?? certificado?.Subject ?? "Certificado corporativo";

        var contenedor = new ContenedorFirmado(
            "Alex Roman",
            "Datos firmados de LanzadorScripts.",
            Version,
            tipo,
            Algoritmo,
            emisor,
            creado,
            datos,
            Convert.ToBase64String(firma));

        return JsonSerializer.Serialize(contenedor, OpcionesJson);
    }

    public bool IntentarVerificarTexto(string tipo, string texto, out string claro)
    {
        claro = string.Empty;
        try
        {
            var contenedor = JsonSerializer.Deserialize<ContenedorFirmado>(texto, OpcionesJson);
            if (contenedor is null
                || contenedor.Version != Version
                || !string.Equals(contenedor.Tipo, tipo, StringComparison.Ordinal)
                || !string.Equals(contenedor.Algoritmo, Algoritmo, StringComparison.Ordinal))
            {
                return false;
            }

            var firma = Convert.FromBase64String(contenedor.Firma);
            using var certificado = _verificacionPruebas is null ? CargarCertificadoPublico() : null;
            using var rsaCertificado = certificado?.GetRSAPublicKey();
            var rsa = _verificacionPruebas ?? rsaCertificado;
            if (rsa is null || !rsa.VerifyData(ObtenerBytesFirmados(tipo, contenedor.Datos, contenedor.CreadoUtc), firma, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                return false;
            }

            claro = Encoding.UTF8.GetString(Convert.FromBase64String(contenedor.Datos));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ObtenerBytesFirmados(string tipo, string datos, DateTimeOffset creadoUtc)
    {
        return Encoding.UTF8.GetBytes($"LanzadorScripts|v{Version}|{tipo}|{creadoUtc:O}|{datos}");
    }

    private static X509Certificate2? BuscarCertificadoPrivado()
    {
        using var usuario = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        using var equipo = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        usuario.Open(OpenFlags.ReadOnly);
        equipo.Open(OpenFlags.ReadOnly);

        return BuscarCertificadoPrivado(usuario)
            ?? BuscarCertificadoPrivado(equipo);
    }

    private static X509Certificate2? BuscarCertificadoPrivado(X509Store almacen)
    {
        return almacen.Certificates
            .Find(X509FindType.FindByThumbprint, ServicioTokenMaestro.HuellaCertificado, validOnly: true)
            .OfType<X509Certificate2>()
            .FirstOrDefault(certificado => certificado.HasPrivateKey);
    }

    private static X509Certificate2 CargarCertificadoPublico()
    {
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(ServicioTokenMaestro.CertificadoPublicoBase64));
    }

    private sealed record ContenedorFirmado(
        string Autor,
        string Descripcion,
        int Version,
        string Tipo,
        string Algoritmo,
        string Emisor,
        DateTimeOffset CreadoUtc,
        string Datos,
        string Firma);
}
