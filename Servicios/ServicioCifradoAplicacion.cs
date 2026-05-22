// (Autor: Alex Roman)
// Descripcion: Cifra datos compartidos que deben viajar entre equipos con la aplicacion.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LanzadorScripts.Servicios;

public sealed class ServicioCifradoAplicacion
{
    private const int Version = 1;
    private const string Algoritmo = "AES-256-GCM";
    private const string ClaveBase64 = "fRvmVxQENrQaN5/eizYCHnllAJnVxONlzxu8j6Je1/M=";
    private static readonly byte[] Clave = Convert.FromBase64String(ClaveBase64);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string CifrarTexto(string tipo, string texto)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var claro = Encoding.UTF8.GetBytes(texto);
        var cifrado = new byte[claro.Length];
        var etiqueta = new byte[16];

        using var aes = new AesGcm(Clave, etiqueta.Length);
        aes.Encrypt(nonce, claro, cifrado, etiqueta, ObtenerDatosAsociados(tipo));

        var contenedor = new ContenedorCifrado(
            "Alex Roman",
            "Datos cifrados de LanzadorScripts.",
            Version,
            tipo,
            Algoritmo,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(etiqueta),
            Convert.ToBase64String(cifrado));

        return JsonSerializer.Serialize(contenedor, OpcionesJson);
    }

    public bool IntentarDescifrarTexto(string tipo, string texto, out string claro)
    {
        claro = string.Empty;
        try
        {
            var contenedor = JsonSerializer.Deserialize<ContenedorCifrado>(texto, OpcionesJson);
            if (contenedor is null
                || contenedor.Version != Version
                || !string.Equals(contenedor.Tipo, tipo, StringComparison.Ordinal)
                || !string.Equals(contenedor.Algoritmo, Algoritmo, StringComparison.Ordinal))
            {
                return false;
            }

            var nonce = Convert.FromBase64String(contenedor.Nonce);
            var etiqueta = Convert.FromBase64String(contenedor.Etiqueta);
            var cifrado = Convert.FromBase64String(contenedor.Datos);
            var claroBytes = new byte[cifrado.Length];

            using var aes = new AesGcm(Clave, etiqueta.Length);
            aes.Decrypt(nonce, cifrado, etiqueta, claroBytes, ObtenerDatosAsociados(tipo));
            claro = Encoding.UTF8.GetString(claroBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ObtenerDatosAsociados(string tipo)
    {
        return Encoding.UTF8.GetBytes($"LanzadorScripts.{tipo}.v{Version}");
    }

    private sealed record ContenedorCifrado(
        string Autor,
        string Descripcion,
        int Version,
        string Tipo,
        string Algoritmo,
        string Nonce,
        string Etiqueta,
        string Datos);
}
