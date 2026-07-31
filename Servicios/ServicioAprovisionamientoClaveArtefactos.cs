// (Autor: Alex Roman)
// Descripcion: Crea y consume el paquete central firmado para aprovisionar la clave AES.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanzadorScripts.Servicios;

internal sealed class ServicioAprovisionamientoClaveArtefactos
{
    public const string NombrePaquete = "clave-artefactos.dpng.json";

    private const int VersionFormato = 1;
    private const int LongitudClave = 32;
    private const int LongitudMaximaPaquete = 1024 * 1024;
    private const int LongitudMaximaArtefacto = 24 * 1024 * 1024;
    private const string Algoritmo = "DPAPI-NG+RSA-PSS-SHA256";
    private const string Autor = "Alex Roman";
    private const string Descripcion = "Aprovisionamiento cifrado de la clave AES de LanzadorScripts.";

    private static readonly UTF8Encoding Utf8Estricto = new(false, true);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly ServicioClaveArtefactos _claveLocal;
    private readonly ServicioFirmaArtefactos _firma;
    private readonly ServicioArtefactosProtegidos _artefactos;
    private readonly IProtectorDpapiNg _dpapiNg;
    private readonly bool _aplicarAcl;

    public ServicioAprovisionamientoClaveArtefactos()
        : this(
            new ServicioClaveArtefactos(),
            new ServicioFirmaArtefactos(),
            new ServicioArtefactosProtegidos(),
            new ServicioDpapiNg(),
            aplicarAcl: true)
    {
    }

    internal ServicioAprovisionamientoClaveArtefactos(
        ServicioClaveArtefactos claveLocal,
        ServicioFirmaArtefactos firma,
        ServicioArtefactosProtegidos artefactos,
        IProtectorDpapiNg dpapiNg,
        bool aplicarAcl)
    {
        _claveLocal = claveLocal;
        _firma = firma;
        _artefactos = artefactos;
        _dpapiNg = dpapiNg;
        _aplicarAcl = aplicarAcl;
    }

    public ResultadoAprovisionamientoClave IntentarAprovisionar(string rutaCarpetaPermisos)
    {
        string? keyIdLocal = null;
        if (_claveLocal.Existe)
        {
            try
            {
                using var materialExistente = _claveLocal.ObtenerMaterial();
                keyIdLocal = materialExistente.KeyId;
            }
            catch (ClaveArtefactosNoDisponibleException ex)
            {
                return new ResultadoAprovisionamientoClave(
                    EstadoAprovisionamientoClave.Error,
                    "La clave local existe pero no es valida; no se reemplazo automaticamente.",
                    TipoError: ex.GetType().Name,
                    DetalleError: ServicioRedaccionSecretos.Sanitizar(ex.Message));
            }
        }

        try
        {
            var rutas = RutasArtefactosProtegidos.Resolver(rutaCarpetaPermisos);
            var rutaPaquete = ServicioRutasSeguras.ResolverArchivoEnCarpeta(
                rutas.Carpeta,
                NombrePaquete,
                "paquete de aprovisionamiento",
                ".json");
            if (!File.Exists(rutaPaquete))
            {
                if (keyIdLocal is not null)
                {
                    return new ResultadoAprovisionamientoClave(
                        EstadoAprovisionamientoClave.YaDisponible,
                        "La clave local ya estaba aprovisionada y no existe un paquete central nuevo.",
                        keyIdLocal);
                }

                return new ResultadoAprovisionamientoClave(
                    EstadoAprovisionamientoClave.PaqueteAusente,
                    $"No existe el paquete central {NombrePaquete}.");
            }

            var paquete = LeerYValidarPaquete(rutaPaquete);
            ValidarKeyIdArtefactos(rutas, paquete.KeyId);
            if (string.Equals(keyIdLocal, paquete.KeyId, StringComparison.Ordinal))
            {
                return new ResultadoAprovisionamientoClave(
                    EstadoAprovisionamientoClave.YaDisponible,
                    "La clave local coincide con el paquete central firmado.",
                    keyIdLocal);
            }

            var protegido = Convert.FromBase64String(paquete.ClaveProtegida);
            var clave = _dpapiNg.Desproteger(protegido);
            try
            {
                if (clave.Length != LongitudClave
                    || !string.Equals(ObtenerKeyId(clave), paquete.KeyId, StringComparison.Ordinal))
                {
                    throw new CryptographicException(
                        "La clave recuperada no coincide con el identificador firmado.");
                }

                ServicioClaveArtefactos.Aprovisionar(
                    _claveLocal.RutaClave,
                    clave,
                    _aplicarAcl);
                using var materialGuardado = _claveLocal.ObtenerMaterial();
                if (!string.Equals(materialGuardado.KeyId, paquete.KeyId, StringComparison.Ordinal))
                {
                    throw new CryptographicException(
                        "La clave local no coincide despues del aprovisionamiento.");
                }

                return new ResultadoAprovisionamientoClave(
                    keyIdLocal is null
                        ? EstadoAprovisionamientoClave.Aprovisionada
                        : EstadoAprovisionamientoClave.Actualizada,
                    keyIdLocal is null
                        ? "La clave AES se aprovisiono automaticamente desde el paquete central."
                        : "La clave AES local se actualizo desde el paquete central firmado.",
                    paquete.KeyId);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clave);
                CryptographicOperations.ZeroMemory(protegido);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or FormatException
            or CryptographicException
            or InvalidOperationException
            or ArgumentException)
        {
            return new ResultadoAprovisionamientoClave(
                EstadoAprovisionamientoClave.Error,
                "No se pudo aprovisionar automaticamente la clave AES. "
                + "Compruebe el acceso al paquete central y la pertenencia al grupo autorizado.",
                TipoError: ex.GetType().Name,
                DetalleError: ServicioRedaccionSecretos.Sanitizar(ex.Message));
        }
    }

    public void CrearPaquete(string rutaCarpetaPermisos, string descriptorDpapiNg)
    {
        // Reutiliza la clave local cuando se crea solo el paquete central.
        using var material = _claveLocal.ObtenerMaterial();
        _ = CrearPaquete(rutaCarpetaPermisos, descriptorDpapiNg, material.Clave);
    }

    internal string CrearPaquete(
        string rutaCarpetaPermisos,
        string descriptorDpapiNg,
        ReadOnlySpan<byte> clave)
    {
        // Firma el paquete con la misma identidad usada por permisos y catalogo.
        ServicioDpapiNg.ValidarDescriptor(descriptorDpapiNg);
        if (clave.Length != LongitudClave)
        {
            throw new ArgumentException("La clave AES debe tener 32 bytes.", nameof(clave));
        }

        var rutas = RutasArtefactosProtegidos.Resolver(rutaCarpetaPermisos);
        var keyId = ObtenerKeyId(clave);
        ValidarKeyIdArtefactos(rutas, keyId);

        var claveProtegida = _dpapiNg.Proteger(clave, descriptorDpapiNg);
        try
        {
            var claveProtegidaBase64 = Convert.ToBase64String(claveProtegida);
            var firma = _firma.Firmar(ObtenerBytesFirma(keyId, claveProtegidaBase64));
            var paquete = new PaqueteClaveArtefactos(
                Autor,
                Descripcion,
                VersionFormato,
                Algoritmo,
                keyId,
                claveProtegidaBase64,
                Convert.ToBase64String(firma));
            var rutaPaquete = ServicioRutasSeguras.ResolverArchivoEnCarpeta(
                rutas.Carpeta,
                NombrePaquete,
                "paquete de aprovisionamiento",
                ".json");
            RechazarEnlaceSistema(rutaPaquete);
            ServicioArtefactosProtegidos.GuardarTextoAtomico(
                rutaPaquete,
                JsonSerializer.Serialize(paquete, OpcionesJson));
            var paqueteValidado = LeerYValidarPaquete(rutaPaquete);
            if (!string.Equals(paqueteValidado.KeyId, keyId, StringComparison.Ordinal))
            {
                throw new CryptographicException(
                    "El paquete guardado no conserva el identificador de la clave AES.");
            }

            return keyId;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claveProtegida);
        }
    }

    private PaqueteClaveArtefactos LeerYValidarPaquete(string rutaPaquete)
    {
        var texto = LeerTextoLimitado(rutaPaquete, LongitudMaximaPaquete);
        var paquete = JsonSerializer.Deserialize<PaqueteClaveArtefactos>(texto, OpcionesJson);
        if (paquete is null
            || !string.Equals(paquete.Autor, Autor, StringComparison.Ordinal)
            || !string.Equals(paquete.Descripcion, Descripcion, StringComparison.Ordinal)
            || paquete.Version != VersionFormato
            || !string.Equals(paquete.Algoritmo, Algoritmo, StringComparison.Ordinal)
            || string.IsNullOrEmpty(paquete.KeyId)
            || string.IsNullOrEmpty(paquete.ClaveProtegida)
            || string.IsNullOrEmpty(paquete.Firma)
            || paquete.KeyId.Length != 16
            || !paquete.KeyId.All(Uri.IsHexDigit)
            || paquete.ClaveProtegida.Length > LongitudMaximaPaquete
            || paquete.Firma.Length > LongitudMaximaPaquete)
        {
            throw new InvalidDataException(
                "El paquete central no tiene el formato o los metadatos esperados.");
        }

        var claveProtegida = Convert.FromBase64String(paquete.ClaveProtegida);
        var firma = Convert.FromBase64String(paquete.Firma);
        if (claveProtegida.Length == 0
            || claveProtegida.Length > LongitudMaximaPaquete
            || firma.Length == 0
            || firma.Length > 16 * 1024
            || !_firma.Verificar(
                ObtenerBytesFirma(paquete.KeyId, paquete.ClaveProtegida),
                firma))
        {
            throw new CryptographicException(
                "La firma del paquete central no es valida.");
        }

        CryptographicOperations.ZeroMemory(claveProtegida);
        return paquete;
    }

    private void ValidarKeyIdArtefactos(RutasArtefactos rutas, string keyIdEsperado)
    {
        var keyIdPermisos = LeerKeyIdFirmado(
            rutas.RutaPermisos,
            ServicioArtefactosProtegidos.TipoPermisos);
        var keyIdCatalogo = LeerKeyIdFirmado(
            rutas.RutaCatalogo,
            ServicioArtefactosProtegidos.TipoCatalogoScripts);
        if (!string.Equals(keyIdPermisos, keyIdEsperado, StringComparison.Ordinal)
            || !string.Equals(keyIdCatalogo, keyIdEsperado, StringComparison.Ordinal))
        {
            throw new CryptographicException(
                "La clave del paquete no coincide con permisos.json y catalogo-scripts.json.");
        }
    }

    private string LeerKeyIdFirmado(string ruta, string tipo)
    {
        var texto = LeerTextoLimitado(ruta, LongitudMaximaArtefacto);
        if (!_artefactos.IntentarObtenerKeyIdFirmado(tipo, texto, out var keyId, out var error))
        {
            throw new CryptographicException(error);
        }

        return keyId;
    }

    private static string LeerTextoLimitado(string ruta, int longitudMaxima)
    {
        var rutaSegura = ServicioRutasSeguras.ResolverArchivoAbsoluto(
            ruta,
            "archivo de aprovisionamiento",
            ".json");
        RechazarEnlaceSistema(rutaSegura);
        using var flujo = new FileStream(
            rutaSegura,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        if (flujo.Length <= 0 || flujo.Length > longitudMaxima)
        {
            throw new InvalidDataException("El archivo de aprovisionamiento tiene un tamano no valido.");
        }

        using var lector = new StreamReader(
            flujo,
            Utf8Estricto,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        return lector.ReadToEnd();
    }

    private static void RechazarEnlaceSistema(string ruta)
    {
        if (File.Exists(ruta)
            && File.GetAttributes(ruta).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "El archivo de aprovisionamiento no puede ser un enlace de sistema.");
        }
    }

    private static string ObtenerKeyId(ReadOnlySpan<byte> clave)
    {
        return Convert.ToHexString(SHA256.HashData(clave))[..16];
    }

    private static byte[] ObtenerBytesFirma(string keyId, string claveProtegida)
    {
        return Encoding.UTF8.GetBytes(
            $"LanzadorScripts|aprovisionamiento|{Autor}|{Descripcion}|v{VersionFormato}|{Algoritmo}|{keyId}|{claveProtegida}");
    }

    private sealed record PaqueteClaveArtefactos(
        string Autor,
        string Descripcion,
        int Version,
        string Algoritmo,
        string KeyId,
        string ClaveProtegida,
        string Firma);
}

internal enum EstadoAprovisionamientoClave
{
    YaDisponible,
    Aprovisionada,
    Actualizada,
    PaqueteAusente,
    Error
}

internal sealed record ResultadoAprovisionamientoClave(
    EstadoAprovisionamientoClave Estado,
    string Mensaje,
    string? KeyId = null,
    string? TipoError = null,
    string? DetalleError = null);
