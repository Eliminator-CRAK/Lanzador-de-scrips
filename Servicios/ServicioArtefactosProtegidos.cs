// (Autor: Alex Roman)
// Descripcion: Cifra, firma, valida y guarda artefactos compartidos de la aplicacion.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanzadorScripts.Servicios;

public sealed class ServicioArtefactosProtegidos
{
    public const string TipoPermisos = "permissions";
    public const string TipoCatalogoScripts = "script-catalog";

    private const int Version = 2;
    private const string Algoritmo = "AES-256-GCM+RSA-PSS-SHA256";
    private const string AutorContenedor = "Alex Roman";
    private const string DescripcionContenedor = "Artefacto cifrado y firmado de LanzadorScripts.";
    private const int LongitudMaximaCifrada = 16 * 1024 * 1024;
    private const int LongitudMaximaArchivo = 24 * 1024 * 1024;
    private const int LongitudMaximaFirma = 16 * 1024;

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly ServicioClaveArtefactos? _servicioClave;
    private readonly ServicioFirmaArtefactos _servicioFirma;
    private readonly byte[]? _clavePruebas;

    public ServicioArtefactosProtegidos()
    {
        _servicioClave = new ServicioClaveArtefactos();
        _servicioFirma = new ServicioFirmaArtefactos();
    }

    internal ServicioArtefactosProtegidos(byte[] claveAes, RSA claveFirma, RSA claveVerificacion)
    {
        if (claveAes.Length != 32)
        {
            throw new ArgumentException("La clave AES debe tener 32 bytes.", nameof(claveAes));
        }

        _clavePruebas = claveAes.ToArray();
        _servicioFirma = new ServicioFirmaArtefactos(claveFirma, claveVerificacion);
    }

    internal ServicioArtefactosProtegidos(
        ServicioClaveArtefactos servicioClave,
        ServicioFirmaArtefactos servicioFirma)
    {
        _servicioClave = servicioClave;
        _servicioFirma = servicioFirma;
    }

    public string KeyId
    {
        get
        {
            using var material = ObtenerMaterialClave();
            return material.KeyId;
        }
    }

    public string ProtegerTexto(string tipo, string texto)
    {
        ValidarTipo(tipo);
        using var material = ObtenerMaterialClave();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var claro = Encoding.UTF8.GetBytes(texto);
        var cifrado = new byte[claro.Length];
        var etiqueta = new byte[16];
        var datosAsociados = ObtenerDatosAsociados(tipo, material.KeyId);
        try
        {
            using (var aes = new AesGcm(material.Clave, etiqueta.Length))
            {
                aes.Encrypt(nonce, claro, cifrado, etiqueta, datosAsociados);
            }

            var nonceBase64 = Convert.ToBase64String(nonce);
            var etiquetaBase64 = Convert.ToBase64String(etiqueta);
            var datosBase64 = Convert.ToBase64String(cifrado);
            var firma = _servicioFirma.Firmar(ObtenerBytesFirma(
                tipo,
                material.KeyId,
                nonceBase64,
                etiquetaBase64,
                datosBase64));
            var contenedor = new ContenedorProtegido(
                AutorContenedor,
                DescripcionContenedor,
                Version,
                tipo,
                Algoritmo,
                material.KeyId,
                nonceBase64,
                etiquetaBase64,
                datosBase64,
                Convert.ToBase64String(firma));

            return JsonSerializer.Serialize(contenedor, OpcionesJson);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claro);
        }
    }

    public bool IntentarDesprotegerTexto(string tipo, string texto, out string claro, out string error)
    {
        claro = string.Empty;
        error = string.Empty;

        try
        {
            using var material = ObtenerMaterialClave();
            if (!IntentarValidarContenedorFirmado(tipo, texto, out var contenedor, out error))
            {
                return false;
            }

            if (!string.Equals(contenedor!.KeyId, material.KeyId, StringComparison.Ordinal))
            {
                error = "El contenedor protegido no tiene el tipo, version o clave esperados.";
                return false;
            }

            var nonce = Convert.FromBase64String(contenedor.Nonce);
            var etiqueta = Convert.FromBase64String(contenedor.Etiqueta);
            var cifrado = Convert.FromBase64String(contenedor.Datos);
            var claroBytes = new byte[cifrado.Length];
            try
            {
                using (var aes = new AesGcm(material.Clave, etiqueta.Length))
                {
                    aes.Decrypt(
                        nonce,
                        cifrado,
                        etiqueta,
                        claroBytes,
                        ObtenerDatosAsociados(tipo, material.KeyId));
                }

                claro = Encoding.UTF8.GetString(claroBytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(claroBytes);
            }
        }
        catch (ClaveArtefactosNoDisponibleException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            error = "El contenedor protegido esta corrupto o fue modificado.";
            return false;
        }
    }

    internal bool IntentarObtenerKeyIdFirmado(
        string tipo,
        string texto,
        out string keyId,
        out string error)
    {
        keyId = string.Empty;
        if (!IntentarValidarContenedorFirmado(tipo, texto, out var contenedor, out error))
        {
            return false;
        }

        keyId = contenedor!.KeyId;
        return true;
    }

    public void GuardarTextoProtegido(string ruta, string tipo, string texto)
    {
        GuardarTextoAtomico(ruta, ProtegerTexto(tipo, texto));
    }

    public bool IntentarCargarTextoProtegido(
        string ruta,
        string tipo,
        out string claro,
        out string error,
        out bool recuperado)
    {
        recuperado = false;
        if (IntentarCargarDesdeRuta(ruta, tipo, out claro, out error))
        {
            return true;
        }

        var errorPrincipal = error;
        if (IntentarCargarDesdeRuta(ruta + ".bak", tipo, out claro, out _))
        {
            recuperado = true;
            error = string.Empty;
            return true;
        }

        claro = string.Empty;
        error = errorPrincipal;
        return false;
    }

    public static void GuardarTextoAtomico(string ruta, string contenido)
    {
        var carpeta = Path.GetDirectoryName(ruta)
            ?? throw new InvalidOperationException("No se pudo resolver la carpeta del archivo protegido.");
        Directory.CreateDirectory(carpeta);

        var temporal = Path.Combine(carpeta, $".{Path.GetFileName(ruta)}.{Guid.NewGuid():N}.tmp");
        var respaldo = ruta + ".bak";
        try
        {
            using (var flujo = new FileStream(temporal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var escritor = new StreamWriter(flujo, new UTF8Encoding(false)))
            {
                escritor.Write(contenido);
                escritor.Flush();
                flujo.Flush(flushToDisk: true);
            }

            if (File.Exists(ruta))
            {
                File.Replace(temporal, ruta, respaldo, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporal, ruta);
            }
        }
        finally
        {
            if (File.Exists(temporal))
            {
                File.Delete(temporal);
            }
        }
    }

    private MaterialClaveArtefactos ObtenerMaterialClave()
    {
        return _clavePruebas is null
            ? _servicioClave!.ObtenerMaterial()
            : new MaterialClaveArtefactos(_clavePruebas.ToArray());
    }

    private static byte[] ObtenerDatosAsociados(string tipo, string keyId)
    {
        return Encoding.UTF8.GetBytes($"LanzadorScripts|artefacto|v{Version}|{tipo}|{Algoritmo}|{keyId}");
    }

    private static byte[] ObtenerBytesFirma(
        string tipo,
        string keyId,
        string nonce,
        string etiqueta,
        string datos)
    {
        return Encoding.UTF8.GetBytes(
            $"LanzadorScripts|firma|{AutorContenedor}|{DescripcionContenedor}|v{Version}|{tipo}|{Algoritmo}|{keyId}|{nonce}|{etiqueta}|{datos}");
    }

    private bool IntentarCargarDesdeRuta(string ruta, string tipo, out string claro, out string error)
    {
        claro = string.Empty;
        RutaArchivoProtegidoValidada rutaSegura;
        try
        {
            rutaSegura = ServicioRutasSeguras.ResolverArchivoProtegido(ruta);
        }
        catch
        {
            error = "La ruta del archivo protegido no es segura.";
            return false;
        }

        if (!File.Exists(rutaSegura.RutaCompleta))
        {
            error = "No se encontro el archivo protegido.";
            return false;
        }

        try
        {
            using var flujo = rutaSegura.AbrirLectura();
            if (flujo.Length <= 0 || flujo.Length > LongitudMaximaArchivo)
            {
                error = "El archivo protegido tiene un tamano no valido.";
                return false;
            }

            using var lector = new StreamReader(flujo, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return IntentarDesprotegerTexto(tipo, lector.ReadToEnd(), out claro, out error);
        }
        catch (IOException)
        {
            error = "No se pudo leer el archivo protegido.";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "No se pudo acceder al archivo protegido.";
            return false;
        }
    }

    private bool IntentarValidarContenedorFirmado(
        string tipo,
        string texto,
        out ContenedorProtegido? contenedor,
        out string error)
    {
        contenedor = null;
        error = string.Empty;
        try
        {
            ValidarTipo(tipo);
            if (string.IsNullOrWhiteSpace(texto) || texto.Length > LongitudMaximaArchivo)
            {
                error = "El contenedor protegido tiene un tamano no valido.";
                return false;
            }

            contenedor = JsonSerializer.Deserialize<ContenedorProtegido>(texto, OpcionesJson);
            if (contenedor is null
                || !string.Equals(contenedor.Autor, AutorContenedor, StringComparison.Ordinal)
                || !string.Equals(contenedor.Descripcion, DescripcionContenedor, StringComparison.Ordinal)
                || contenedor.Version != Version
                || !string.Equals(contenedor.Tipo, tipo, StringComparison.Ordinal)
                || !string.Equals(contenedor.Algoritmo, Algoritmo, StringComparison.Ordinal)
                || string.IsNullOrEmpty(contenedor.KeyId)
                || string.IsNullOrEmpty(contenedor.Nonce)
                || string.IsNullOrEmpty(contenedor.Etiqueta)
                || string.IsNullOrEmpty(contenedor.Datos)
                || string.IsNullOrEmpty(contenedor.Firma)
                || contenedor.KeyId.Length != 16
                || !contenedor.KeyId.All(Uri.IsHexDigit)
                || contenedor.Nonce.Length > 32
                || contenedor.Etiqueta.Length > 32
                || contenedor.Datos.Length > ((LongitudMaximaCifrada + 2L) / 3L * 4L)
                || contenedor.Firma.Length > ((LongitudMaximaFirma + 2L) / 3L * 4L))
            {
                error = "El contenedor protegido no tiene el tipo, version o clave esperados.";
                return false;
            }

            var nonce = Convert.FromBase64String(contenedor.Nonce);
            var etiqueta = Convert.FromBase64String(contenedor.Etiqueta);
            var cifrado = Convert.FromBase64String(contenedor.Datos);
            var firma = Convert.FromBase64String(contenedor.Firma);
            if (nonce.Length != 12
                || etiqueta.Length != 16
                || cifrado.Length > LongitudMaximaCifrada
                || firma.Length == 0
                || firma.Length > LongitudMaximaFirma)
            {
                error = "El contenedor protegido tiene longitudes no validas.";
                return false;
            }

            if (!_servicioFirma.Verificar(
                ObtenerBytesFirma(
                    tipo,
                    contenedor.KeyId,
                    contenedor.Nonce,
                    contenedor.Etiqueta,
                    contenedor.Datos),
                firma))
            {
                error = "La firma del contenedor protegido no es valida.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            contenedor = null;
            error = "El contenedor protegido esta corrupto o fue modificado.";
            return false;
        }
    }

    private static void ValidarTipo(string tipo)
    {
        if (tipo is not TipoPermisos and not TipoCatalogoScripts)
        {
            throw new ArgumentException("El tipo de artefacto no esta permitido.", nameof(tipo));
        }
    }

    private sealed record ContenedorProtegido(
        string Autor,
        string Descripcion,
        int Version,
        string Tipo,
        string Algoritmo,
        string KeyId,
        string Nonce,
        string Etiqueta,
        string Datos,
        string Firma);
}
