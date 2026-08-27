// (Autor: Alex Roman)
// Descripcion: Define los nombres Kerberos usados para autenticar el servidor central.

using System.Net;

namespace LanzadorScripts.Protocolo;

public static class AutenticacionServidorCentral
{
    public const string ClaseSpn = "LanzadorScripts";

    public static IReadOnlyList<string> CrearSpnCandidatos(string servidor)
    {
        var nombre = NormalizarServidor(servidor);

        return
        [
            $"{ClaseSpn}/{nombre}",
            $"HOST/{nombre}"
        ];
    }

    public static string NormalizarServidor(string servidor)
    {
        var valor = servidor?.Trim().TrimEnd('.')
            ?? throw new ArgumentNullException(nameof(servidor));
        if (valor.Length is <= 0 or > 253
            || IPAddress.TryParse(valor, out _)
            || valor.Contains('\\', StringComparison.Ordinal)
            || valor.Contains('/', StringComparison.Ordinal)
            || valor.Contains(':', StringComparison.Ordinal)
            || valor.Split('.').Any(segmento => segmento.Length is <= 0 or > 63
                || segmento[0] == '-'
                || segmento[^1] == '-'
                || segmento.Any(caracter => !EsCaracterDnsAscii(caracter))))
        {
            throw new ArgumentException("El nombre del servidor central no es valido.", nameof(servidor));
        }

        return valor;
    }

    private static bool EsCaracterDnsAscii(char caracter)
    {
        return caracter is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-';
    }
}
