// (Autor: Alex Roman)
// Descripcion: Protege y recupera la clave AES de artefactos con DPAPI de maquina.

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanzadorScripts.Servicios;

public sealed class ServicioClaveArtefactos
{
    private const int VersionFormato = 1;
    private const int LongitudClave = 32;
    private const int LongitudMaximaArchivo = 64 * 1024;
    private const string AmbitoFormato = "LocalMachine";

    private static readonly byte[] Entropia = SHA256.HashData(
        Encoding.UTF8.GetBytes("LanzadorScripts|clave-artefactos|v2"));

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string _rutaClave;

    public ServicioClaveArtefactos()
        : this(RutasAplicacion.RutaClaveArtefactos)
    {
    }

    internal ServicioClaveArtefactos(string rutaClave)
    {
        _rutaClave = Path.GetFullPath(rutaClave);
    }

    internal MaterialClaveArtefactos ObtenerMaterial()
    {
        if (!File.Exists(_rutaClave))
        {
            throw new ClaveArtefactosNoDisponibleException(
                $"No se ha aprovisionado la clave de artefactos en {_rutaClave}.");
        }

        try
        {
            if (File.GetAttributes(_rutaClave).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ClaveArtefactosNoDisponibleException(
                    "El archivo de clave de artefactos no puede ser un enlace de sistema.");
            }

            var informacion = new FileInfo(_rutaClave);
            if (informacion.Length <= 0 || informacion.Length > LongitudMaximaArchivo)
            {
                throw new ClaveArtefactosNoDisponibleException(
                    "El archivo de clave de artefactos tiene un tamano no valido.");
            }

            var formato = JsonSerializer.Deserialize<FormatoClaveProtegida>(
                File.ReadAllText(_rutaClave, Encoding.UTF8),
                OpcionesJson);
            if (formato is null
                || formato.Version != VersionFormato
                || !string.Equals(formato.Ambito, AmbitoFormato, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(formato.ClaveProtegida))
            {
                throw new ClaveArtefactosNoDisponibleException(
                    "El archivo de clave de artefactos no tiene un formato valido.");
            }

            var protegida = Convert.FromBase64String(formato.ClaveProtegida);
            var clave = ProtectedData.Unprotect(protegida, Entropia, DataProtectionScope.LocalMachine);
            CryptographicOperations.ZeroMemory(protegida);
            if (clave.Length != LongitudClave)
            {
                CryptographicOperations.ZeroMemory(clave);
                throw new ClaveArtefactosNoDisponibleException(
                    "La clave de artefactos no tiene la longitud requerida.");
            }

            return new MaterialClaveArtefactos(clave);
        }
        catch (ClaveArtefactosNoDisponibleException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or FormatException
            or CryptographicException)
        {
            throw new ClaveArtefactosNoDisponibleException(
                "No se pudo recuperar la clave de artefactos protegida para este equipo.",
                ex);
        }
    }

    internal static void Aprovisionar(string rutaClave, ReadOnlySpan<byte> clave, bool aplicarAcl = true)
    {
        if (clave.Length != LongitudClave)
        {
            throw new ArgumentException("La clave AES debe tener 32 bytes.", nameof(clave));
        }

        var rutaCompleta = Path.GetFullPath(rutaClave);
        var carpeta = Path.GetDirectoryName(rutaCompleta)
            ?? throw new InvalidOperationException("No se pudo resolver la carpeta de seguridad.");
        if (File.Exists(rutaCompleta)
            && File.GetAttributes(rutaCompleta).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException(
                "El archivo de clave de artefactos no puede ser un enlace de sistema.");
        }

        if (aplicarAcl)
        {
            if (!string.Equals(
                rutaCompleta,
                Path.GetFullPath(RutasAplicacion.RutaClaveArtefactos),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "La clave con ACL administrativa solo puede guardarse en la ruta de seguridad configurada.");
            }

            ServicioDirectoriosAplicacion.PrepararDirectorioAdministrativo(carpeta);
        }
        else
        {
            Directory.CreateDirectory(carpeta);
        }

        var copiaClave = clave.ToArray();
        var protegida = ProtectedData.Protect(copiaClave, Entropia, DataProtectionScope.LocalMachine);
        try
        {
            var formato = new FormatoClaveProtegida(
                VersionFormato,
                AmbitoFormato,
                Convert.ToBase64String(protegida));
            ServicioArtefactosProtegidos.GuardarTextoAtomico(
                rutaCompleta,
                JsonSerializer.Serialize(formato, OpcionesJson));
            if (aplicarAcl)
            {
                ServicioDirectoriosAplicacion.ProtegerArchivoClaveArtefactos();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copiaClave);
            CryptographicOperations.ZeroMemory(protegida);
        }
    }

    private sealed record FormatoClaveProtegida(
        int Version,
        string Ambito,
        string ClaveProtegida);
}

internal sealed class MaterialClaveArtefactos : IDisposable
{
    private byte[]? _clave;

    public MaterialClaveArtefactos(byte[] clave)
    {
        _clave = clave;
        KeyId = Convert.ToHexString(SHA256.HashData(clave))[..16];
    }

    public string KeyId { get; }

    public ReadOnlySpan<byte> Clave => _clave
        ?? throw new ObjectDisposedException(nameof(MaterialClaveArtefactos));

    public void Dispose()
    {
        if (_clave is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_clave);
        _clave = null;
    }
}

public sealed class ClaveArtefactosNoDisponibleException : InvalidOperationException
{
    public ClaveArtefactosNoDisponibleException(string mensaje)
        : base(mensaje)
    {
    }

    public ClaveArtefactosNoDisponibleException(string mensaje, Exception excepcionInterna)
        : base(mensaje, excepcionInterna)
    {
    }
}
