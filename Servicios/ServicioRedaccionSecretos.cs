// (Autor: Alex Roman)
// Descripcion: Oculta secretos antes de escribirlos en consola o logs.

using System.Text.RegularExpressions;

namespace LanzadorScripts.Servicios;

public static class ServicioRedaccionSecretos
{
    private static readonly Regex PatronClaveValor = new(
        "(?i)(\"?(?:password|pass|token|secret|api_key|bearer|authorization|contrasena|contraseña|clave)\"?\\s*[:=]\\s*)(\"[^\"\\r\\n]*\"|[^\\s,;}\\]]+)",
        RegexOptions.Compiled);

    private static readonly Regex PatronBearer = new(
        "(?i)\\bBearer\\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.Compiled);

    public static string Sanitizar(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return string.Empty;
        }

        var resultado = PatronClaveValor.Replace(texto, coincidencia =>
        {
            var prefijo = coincidencia.Groups[1].Value;
            var valor = coincidencia.Groups[2].Value;
            return valor.StartsWith('"')
                ? $"{prefijo}\"[oculto]\""
                : $"{prefijo}[oculto]";
        });

        return PatronBearer.Replace(resultado, "Bearer [oculto]");
    }

    public static IReadOnlyDictionary<string, string?>? Sanitizar(IReadOnlyDictionary<string, string?>? datos)
    {
        if (datos is null)
        {
            return null;
        }

        return datos.ToDictionary(
            dato => dato.Key,
            dato => string.IsNullOrEmpty(dato.Value) ? dato.Value : Sanitizar(dato.Value),
            StringComparer.OrdinalIgnoreCase);
    }
}
