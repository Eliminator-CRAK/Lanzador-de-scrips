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

    private const int Version = 1;
    private const string Algoritmo = "AES-256-GCM+RSA-PSS-SHA256";
    private const string AutorContenedor = "Alex Roman";
    private const string DescripcionContenedor = "Artefacto cifrado y firmado de LanzadorScripts.";
    private const string KeyIdIntegrado = "547B5A49214738CE";
    private const int LongitudMaximaCifrada = 16 * 1024 * 1024;
    private const string ClaveAesBase64 = "***REMOVED***";
    private const string ClavePrivadaBase64 = "***REMOVED***";
    private const string ClavePublicaBase64 = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAwPGTdfQwv/lxBbAoc8BQV+LQ5Wn/99JfIyQgz87kkSN/rHRWlKWWnE7Z8eOqDElxbWcElcbjE4N56RoRYXAx73nJ3h7YOy33P4rHRz1/K6kwMrLvQvZecdqpmny2qhc55fi4cP4uF+UOl3klt80bJCVpXlEx9VQVR/FZbmpX09yiVHXzWDl+k4UsEMH7XCaRY8zj4ueBNpll5vDDTySCPjVbgIlo7M0lRdm3WzQqcpjb+4CN7w5HUyXrVCGBo/iDPkJNsE5dbRUAdCsZaIGpZbXZtWrGet+TEcbf0aPp6a+dkkoXk3otIE1JSAVDDS5fbnoupl7tuB3LutODzXCK8BQPVH1p9Of6JdVW8wmTlwMYAhMKbqk94GTC9/fmnrr76+kv5UWZiewyx6ocqBCwXGDS/ji74rCGyaaivFh460Wg01n0s0oDG333SY3YmpBwckZtUtK4au2WosoILTvpFCkOVQUOHxgfp37IJeuRlBop0vqlHGBvhMCiVzLDCMKnAgMBAAE=";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly byte[] _claveAes;
    private readonly byte[] _clavePrivada;
    private readonly byte[] _clavePublica;
    private readonly string _keyId;

    public ServicioArtefactosProtegidos()
        : this(
            Convert.FromBase64String(ClaveAesBase64),
            Convert.FromBase64String(ClavePrivadaBase64),
            Convert.FromBase64String(ClavePublicaBase64),
            KeyIdIntegrado)
    {
    }

    internal ServicioArtefactosProtegidos(byte[] claveAes, byte[] clavePrivada, byte[] clavePublica, string keyId)
    {
        if (claveAes.Length != 32)
        {
            throw new ArgumentException("La clave AES debe tener 32 bytes.", nameof(claveAes));
        }

        _claveAes = claveAes.ToArray();
        _clavePrivada = clavePrivada.ToArray();
        _clavePublica = clavePublica.ToArray();
        _keyId = keyId;
    }

    public string KeyId => _keyId;

    public string ProtegerTexto(string tipo, string texto)
    {
        ValidarTipo(tipo);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var claro = Encoding.UTF8.GetBytes(texto);
        var cifrado = new byte[claro.Length];
        var etiqueta = new byte[16];
        var datosAsociados = ObtenerDatosAsociados(tipo);

        using (var aes = new AesGcm(_claveAes, etiqueta.Length))
        {
            aes.Encrypt(nonce, claro, cifrado, etiqueta, datosAsociados);
        }

        var nonceBase64 = Convert.ToBase64String(nonce);
        var etiquetaBase64 = Convert.ToBase64String(etiqueta);
        var datosBase64 = Convert.ToBase64String(cifrado);
        var firma = Firmar(ObtenerBytesFirma(tipo, nonceBase64, etiquetaBase64, datosBase64));
        var contenedor = new ContenedorProtegido(
            AutorContenedor,
            DescripcionContenedor,
            Version,
            tipo,
            Algoritmo,
            _keyId,
            nonceBase64,
            etiquetaBase64,
            datosBase64,
            Convert.ToBase64String(firma));

        CryptographicOperations.ZeroMemory(claro);
        return JsonSerializer.Serialize(contenedor, OpcionesJson);
    }

    public bool IntentarDesprotegerTexto(string tipo, string texto, out string claro, out string error)
    {
        claro = string.Empty;
        error = string.Empty;

        try
        {
            ValidarTipo(tipo);
            var contenedor = JsonSerializer.Deserialize<ContenedorProtegido>(texto, OpcionesJson);
            if (contenedor is null
                || !string.Equals(contenedor.Autor, AutorContenedor, StringComparison.Ordinal)
                || !string.Equals(contenedor.Descripcion, DescripcionContenedor, StringComparison.Ordinal)
                || contenedor.Version != Version
                || !string.Equals(contenedor.Tipo, tipo, StringComparison.Ordinal)
                || !string.Equals(contenedor.Algoritmo, Algoritmo, StringComparison.Ordinal)
                || !string.Equals(contenedor.KeyId, _keyId, StringComparison.Ordinal))
            {
                error = "El contenedor protegido no tiene el tipo, version o clave esperados.";
                return false;
            }

            var nonce = Convert.FromBase64String(contenedor.Nonce);
            var etiqueta = Convert.FromBase64String(contenedor.Etiqueta);
            var cifrado = Convert.FromBase64String(contenedor.Datos);
            var firma = Convert.FromBase64String(contenedor.Firma);
            if (nonce.Length != 12 || etiqueta.Length != 16 || cifrado.Length > LongitudMaximaCifrada)
            {
                error = "El contenedor protegido tiene longitudes no validas.";
                return false;
            }

            if (!VerificarFirma(
                ObtenerBytesFirma(tipo, contenedor.Nonce, contenedor.Etiqueta, contenedor.Datos),
                firma))
            {
                error = "La firma del contenedor protegido no es valida.";
                return false;
            }

            var claroBytes = new byte[cifrado.Length];
            using (var aes = new AesGcm(_claveAes, etiqueta.Length))
            {
                aes.Decrypt(nonce, cifrado, etiqueta, claroBytes, ObtenerDatosAsociados(tipo));
            }

            claro = Encoding.UTF8.GetString(claroBytes);
            CryptographicOperations.ZeroMemory(claroBytes);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or ArgumentException)
        {
            error = "El contenedor protegido esta corrupto o fue modificado.";
            return false;
        }
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

    private byte[] Firmar(byte[] datos)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(_clavePrivada, out _);
        return rsa.SignData(datos, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    private bool VerificarFirma(byte[] datos, byte[] firma)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(_clavePublica, out _);
        return rsa.VerifyData(datos, firma, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
    }

    private byte[] ObtenerDatosAsociados(string tipo)
    {
        return Encoding.UTF8.GetBytes($"LanzadorScripts|artefacto|v{Version}|{tipo}|{Algoritmo}|{_keyId}");
    }

    private byte[] ObtenerBytesFirma(string tipo, string nonce, string etiqueta, string datos)
    {
        return Encoding.UTF8.GetBytes(
            $"LanzadorScripts|firma|{AutorContenedor}|{DescripcionContenedor}|v{Version}|{tipo}|{Algoritmo}|{_keyId}|{nonce}|{etiqueta}|{datos}");
    }

    private bool IntentarCargarDesdeRuta(string ruta, string tipo, out string claro, out string error)
    {
        claro = string.Empty;
        string rutaSegura;
        try
        {
            rutaSegura = ServicioRutasSeguras.ResolverArchivoAbsoluto(ruta, "archivo protegido");
        }
        catch
        {
            error = "La ruta del archivo protegido no es segura.";
            return false;
        }

        if (!File.Exists(rutaSegura))
        {
            error = "No se encontro el archivo protegido.";
            return false;
        }

        try
        {
            return IntentarDesprotegerTexto(
                tipo,
                File.ReadAllText(rutaSegura, Encoding.UTF8),
                out claro,
                out error);
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
