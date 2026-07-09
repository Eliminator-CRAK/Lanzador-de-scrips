// (Autor: Alex Roman)
// Descripcion: Normaliza el perfil local usado por la aplicacion portable.

using System.Text;

namespace LanzadorScripts.Servicios;

public static class PerfilAplicacion
{
    private const string PerfilPredeterminado = "default";
    private const int LongitudMaximaPerfil = 48;

    public static string ObtenerPerfilUsuarioActual()
    {
        return Normalizar(Environment.UserName);
    }

    internal static string Normalizar(string? perfil)
    {
        var valor = perfil?.Trim();
        if (string.IsNullOrWhiteSpace(valor))
        {
            return PerfilPredeterminado;
        }

        var constructor = new StringBuilder(valor.Length);
        foreach (var caracter in valor)
        {
            constructor.Append(EsCaracterPermitido(caracter) ? char.ToLowerInvariant(caracter) : '_');
        }

        var normalizado = constructor.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(normalizado))
        {
            return PerfilPredeterminado;
        }

        return normalizado.Length <= LongitudMaximaPerfil
            ? normalizado
            : normalizado[..LongitudMaximaPerfil];
    }

    private static bool EsCaracterPermitido(char caracter)
    {
        return caracter is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_'
            or '-'
            or '.';
    }
}
