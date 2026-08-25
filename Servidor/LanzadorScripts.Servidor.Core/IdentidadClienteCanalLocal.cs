// (Autor: Alex Roman)
// Descripcion: Obtiene la cuenta autenticada del cliente conectado al canal administrativo local.

using System.IO.Pipes;
using System.Security.Principal;

namespace LanzadorScripts.Servidor.Core;

internal static class IdentidadClienteCanalLocal
{
    public static string ObtenerCuenta(NamedPipeServerStream canal)
    {
        ArgumentNullException.ThrowIfNull(canal);

        var cuenta = ConfiguracionServidor.NormalizarCuenta(
            canal.GetImpersonationUserName());
        if (cuenta.Length > 0)
        {
            return cuenta;
        }

        var cuentaImpersonada = string.Empty;
        canal.RunAsClient(() =>
        {
            using var identidad = WindowsIdentity.GetCurrent(ifImpersonating: true);
            cuentaImpersonada = NormalizarIdentidad(identidad);
        });
        return cuentaImpersonada;
    }

    private static string NormalizarIdentidad(WindowsIdentity? identidad)
    {
        if (identidad is null)
        {
            return string.Empty;
        }

        var cuenta = ConfiguracionServidor.NormalizarCuenta(identidad.Name);
        if (cuenta.Length > 0 || identidad.User is null)
        {
            return cuenta;
        }

        try
        {
            var traducida = identidad.User.Translate(typeof(NTAccount));
            return ConfiguracionServidor.NormalizarCuenta(traducida.Value);
        }
        catch (IdentityNotMappedException)
        {
            return string.Empty;
        }
    }
}
