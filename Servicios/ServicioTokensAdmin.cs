// (Autor: Alex Roman)
// Descripcion: Gestiona tokens de administrador cifrados por usuario.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace LanzadorScripts.Servicios;

public sealed class ServicioTokensAdmin
{
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true
    };

    public TokenAdmin ObtenerOCrear(string usuarioWindows)
    {
        ServicioDirectoriosAplicacion.PrepararDatosUsuario();
        Directory.CreateDirectory(RutasAplicacion.RutaTokensUsuario);

        var ruta = ResolverRutaToken(usuarioWindows);
        var tokenExistente = LeerToken(ruta);
        if (tokenExistente is not null)
        {
            return tokenExistente;
        }

        var token = new TokenAdmin(usuarioWindows, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.Now);
        GuardarToken(ruta, token);
        return token;
    }

    public bool Validar(string usuarioWindows, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        ServicioDirectoriosAplicacion.PrepararDatosUsuario();
        Directory.CreateDirectory(RutasAplicacion.RutaTokensUsuario);
        var tokenGuardado = LeerToken(ResolverRutaToken(usuarioWindows));
        return tokenGuardado is not null
            && string.Equals(tokenGuardado.UsuarioWindows, usuarioWindows, StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(tokenGuardado.Valor),
                Encoding.UTF8.GetBytes(token));
    }

    private static TokenAdmin? LeerToken(string ruta)
    {
        if (!File.Exists(ruta))
        {
            return null;
        }

        try
        {
            var protegido = File.ReadAllBytes(ruta);
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(protegido, null, DataProtectionScope.CurrentUser));
            return JsonSerializer.Deserialize<TokenAdmin>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void GuardarToken(string ruta, TokenAdmin token)
    {
        var json = JsonSerializer.Serialize(token, OpcionesJson);
        var protegido = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(ruta, protegido);
    }

    private static string ObtenerRutaToken(string usuarioWindows)
    {
        var nombreSeguro = string.Concat(usuarioWindows.Select(caracter =>
            Path.GetInvalidFileNameChars().Contains(caracter) || caracter is '\\' or '/' ? '_' : caracter));

        return Path.Combine(RutasAplicacion.RutaTokensUsuario, $"{nombreSeguro}.token");
    }

    private static string ResolverRutaToken(string usuarioWindows)
    {
        // Copia el token anterior a la carpeta local segura.
        var rutaNueva = ObtenerRutaToken(usuarioWindows);
        if (File.Exists(rutaNueva))
        {
            return rutaNueva;
        }

        var nombre = Path.GetFileName(rutaNueva);
        var rutaLegada = Path.Combine(RutasAplicacion.RutaTokensUsuarioLegada, nombre);
        if (!File.Exists(rutaLegada))
        {
            return rutaNueva;
        }

        try
        {
            File.Copy(rutaLegada, rutaNueva, overwrite: false);
            return rutaNueva;
        }
        catch
        {
            return rutaNueva;
        }
    }
}

public sealed record TokenAdmin(string UsuarioWindows, string Valor, DateTimeOffset Creado);
