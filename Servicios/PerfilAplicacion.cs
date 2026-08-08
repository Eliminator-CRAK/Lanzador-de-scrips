// (Autor: Alex Roman)
// Descripcion: Normaliza la identidad usada para separar datos locales por usuario.

using System.Security.Cryptography;
using System.Security.Principal;
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

    public static string ObtenerIdentificadorUsuarioActual()
    {
        // Usa el SID para separar perfiles de usuarios con el mismo nombre.
        try
        {
            return CrearIdentificadorSid(WindowsIdentity.GetCurrent().User?.Value);
        }
        catch
        {
            return CrearIdentificadorSid(Environment.UserDomainName + "\\" + Environment.UserName);
        }
    }

    internal static string CrearIdentificadorSid(string? sid)
    {
        // Conserva la unicidad sin alargar las rutas locales.
        var valor = string.IsNullOrWhiteSpace(sid) ? PerfilPredeterminado : sid.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(valor));
        return "sid-" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
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
