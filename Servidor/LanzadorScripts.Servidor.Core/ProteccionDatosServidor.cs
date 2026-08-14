// (Autor: Alex Roman)
// Descripcion: Genera, protege y usa la clave maestra del servidor.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanzadorScripts.Servidor.Core;

public interface IProtectorClaveServidor
{
    byte[] Proteger(ReadOnlySpan<byte> datos);

    byte[] Desproteger(ReadOnlySpan<byte> datos);
}

public sealed class ProtectorClaveDpapi : IProtectorClaveServidor
{
    private static readonly byte[] Entropia = Encoding.UTF8.GetBytes(
        "LanzadorScriptsServidor.ClaveBaseDatos.v1");

    public byte[] Proteger(ReadOnlySpan<byte> datos)
    {
        var claro = datos.ToArray();
        try
        {
            return ProtectedData.Protect(claro, Entropia, DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claro);
        }
    }

    public byte[] Desproteger(ReadOnlySpan<byte> datos)
    {
        return ProtectedData.Unprotect(datos.ToArray(), Entropia, DataProtectionScope.LocalMachine);
    }
}

public sealed class AlmacenClaveServidor
{
    private static readonly byte[] Cabecera = "LSDBKEY1"u8.ToArray();
    private readonly RutasServidor _rutas;
    private readonly IProtectorClaveServidor _protector;

    public AlmacenClaveServidor(RutasServidor rutas, IProtectorClaveServidor? protector = null)
    {
        _rutas = rutas;
        _protector = protector ?? new ProtectorClaveDpapi();
    }

    public byte[] ObtenerOCrear()
    {
        _rutas.PrepararDirectorios();
        for (var intento = 0; intento < 20; intento++)
        {
            if (File.Exists(_rutas.RutaClaveProtegida))
            {
                try
                {
                    return Leer();
                }
                catch (IOException) when (intento < 19)
                {
                    Thread.Sleep(50);
                    continue;
                }
            }

            var clave = RandomNumberGenerator.GetBytes(32);
            try
            {
                var protegida = _protector.Proteger(clave);
                try
                {
                    var contenido = new byte[Cabecera.Length + protegida.Length];
                    Cabecera.CopyTo(contenido, 0);
                    protegida.CopyTo(contenido, Cabecera.Length);
                    try
                    {
                        using var flujo = new FileStream(
                            _rutas.RutaClaveProtegida,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            4096,
                            FileOptions.WriteThrough);
                        flujo.Write(contenido);
                        flujo.Flush(flushToDisk: true);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(contenido);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protegida);
                }

                return clave.ToArray();
            }
            catch (IOException) when (File.Exists(_rutas.RutaClaveProtegida) && intento < 19)
            {
                Thread.Sleep(50);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clave);
            }
        }

        throw new IOException("No se pudo crear o leer la clave protegida del servidor.");
    }

    private byte[] Leer()
    {
        RutasServidor.RechazarPuntoReanalisis(_rutas.RutaClaveProtegida);
        var contenido = File.ReadAllBytes(_rutas.RutaClaveProtegida);
        if (contenido.Length <= Cabecera.Length || contenido.Length > 64 * 1024
            || !contenido.AsSpan(0, Cabecera.Length).SequenceEqual(Cabecera))
        {
            throw new CryptographicException("El archivo de clave del servidor no tiene un formato valido.");
        }

        var protegida = contenido.AsSpan(Cabecera.Length).ToArray();
        try
        {
            var clave = _protector.Desproteger(protegida);
            if (clave.Length != 32)
            {
                CryptographicOperations.ZeroMemory(clave);
                throw new CryptographicException("La clave de la base de datos no tiene la longitud esperada.");
            }

            return clave;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protegida);
            CryptographicOperations.ZeroMemory(contenido);
        }
    }
}

public sealed class CifradorDatosServidor : IDisposable
{
    private const int LongitudNonce = 12;
    private const int LongitudEtiqueta = 16;
    private const int LongitudMaxima = 4 * 1024 * 1024;
    private readonly byte[] _claveCifrado;
    private readonly byte[] _claveIndices;

    public CifradorDatosServidor(ReadOnlySpan<byte> claveMaestra)
    {
        if (claveMaestra.Length != 32)
        {
            throw new ArgumentException("La clave maestra debe tener 256 bits.", nameof(claveMaestra));
        }

        _claveCifrado = Derivar(claveMaestra, "cifrado-filas-v1");
        _claveIndices = Derivar(claveMaestra, "indices-filas-v1");
    }

    public byte[] Cifrar<T>(string tabla, string id, T datos)
    {
        ValidarContexto(tabla, id);
        var claro = JsonSerializer.SerializeToUtf8Bytes(datos);
        if (claro.Length is <= 0 or > LongitudMaxima)
        {
            throw new InvalidDataException("Los datos que se van a cifrar superan el limite permitido.");
        }

        var nonce = RandomNumberGenerator.GetBytes(LongitudNonce);
        var etiqueta = new byte[LongitudEtiqueta];
        var cifrado = new byte[claro.Length];
        var asociado = Encoding.UTF8.GetBytes($"{tabla}\0{id}\0v1");
        try
        {
            using var aes = new AesGcm(_claveCifrado, LongitudEtiqueta);
            aes.Encrypt(nonce, claro, cifrado, etiqueta, asociado);
            var resultado = new byte[1 + LongitudNonce + LongitudEtiqueta + cifrado.Length];
            resultado[0] = 1;
            nonce.CopyTo(resultado, 1);
            etiqueta.CopyTo(resultado, 1 + LongitudNonce);
            cifrado.CopyTo(resultado, 1 + LongitudNonce + LongitudEtiqueta);
            return resultado;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claro);
            CryptographicOperations.ZeroMemory(cifrado);
            CryptographicOperations.ZeroMemory(etiqueta);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(asociado);
        }
    }

    public T Descifrar<T>(string tabla, string id, ReadOnlySpan<byte> contenido)
    {
        ValidarContexto(tabla, id);
        if (contenido.Length <= 1 + LongitudNonce + LongitudEtiqueta
            || contenido.Length > LongitudMaxima + 1 + LongitudNonce + LongitudEtiqueta
            || contenido[0] != 1)
        {
            throw new CryptographicException("La fila cifrada no tiene un formato valido.");
        }

        var nonce = contenido.Slice(1, LongitudNonce);
        var etiqueta = contenido.Slice(1 + LongitudNonce, LongitudEtiqueta);
        var cifrado = contenido[(1 + LongitudNonce + LongitudEtiqueta)..];
        var claro = new byte[cifrado.Length];
        var asociado = Encoding.UTF8.GetBytes($"{tabla}\0{id}\0v1");
        try
        {
            using var aes = new AesGcm(_claveCifrado, LongitudEtiqueta);
            aes.Decrypt(nonce, cifrado, etiqueta, claro, asociado);
            return JsonSerializer.Deserialize<T>(claro)
                ?? throw new CryptographicException("La fila descifrada no contiene datos validos.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claro);
            CryptographicOperations.ZeroMemory(asociado);
        }
    }

    public byte[] CrearIndice(string contexto, string valor)
    {
        if (string.IsNullOrWhiteSpace(contexto) || contexto.Length > 100)
        {
            throw new ArgumentException("El contexto del indice no es valido.", nameof(contexto));
        }

        var normalizado = Encoding.UTF8.GetBytes(valor.Trim().ToUpperInvariant());
        try
        {
            using var hmac = new HMACSHA256(_claveIndices);
            var contextoBytes = Encoding.UTF8.GetBytes(contexto + "\0");
            try
            {
                var entrada = new byte[contextoBytes.Length + normalizado.Length];
                contextoBytes.CopyTo(entrada, 0);
                normalizado.CopyTo(entrada, contextoBytes.Length);
                try
                {
                    return hmac.ComputeHash(entrada);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(entrada);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contextoBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(normalizado);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_claveCifrado);
        CryptographicOperations.ZeroMemory(_claveIndices);
    }

    private static byte[] Derivar(ReadOnlySpan<byte> claveMaestra, string etiqueta)
    {
        var clave = claveMaestra.ToArray();
        var contexto = Encoding.UTF8.GetBytes(etiqueta);
        try
        {
            using var hmac = new HMACSHA256(clave);
            return hmac.ComputeHash(contexto);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clave);
            CryptographicOperations.ZeroMemory(contexto);
        }
    }

    private static void ValidarContexto(string tabla, string id)
    {
        if (string.IsNullOrWhiteSpace(tabla) || tabla.Length > 100
            || string.IsNullOrWhiteSpace(id) || id.Length > 256)
        {
            throw new ArgumentException("El contexto criptografico no es valido.");
        }
    }
}
