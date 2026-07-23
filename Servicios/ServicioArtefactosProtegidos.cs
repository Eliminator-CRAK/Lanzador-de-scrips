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
    private const string ClaveAesBase64 = "8+7822V744mba2DtGiOft84f8Lj9XYmlw4ynob3ArPE=";
    private const string ClavePrivadaBase64 = "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";
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
