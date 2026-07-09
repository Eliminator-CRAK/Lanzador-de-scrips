// (Autor: Alex Roman)
// Descripcion: Comprueba WebView2 sin instalar componentes en el equipo.

using Microsoft.Web.WebView2.Core;

namespace LanzadorScripts.Servicios;

public sealed class ServicioDisponibilidadWebView2
{
    public ResultadoDisponibilidadWebView2 Comprobar(string? runtimeFijo = null)
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString(runtimeFijo);
            return string.IsNullOrWhiteSpace(version)
                ? ResultadoDisponibilidadWebView2.Error(runtimeFijo)
                : ResultadoDisponibilidadWebView2.Correcto(version, runtimeFijo);
        }
        catch
        {
            return ResultadoDisponibilidadWebView2.Error(runtimeFijo);
        }
    }
}

public sealed record ResultadoDisponibilidadWebView2(bool Exito, string Mensaje, string? Version, string? RuntimeFijo)
{
    public static ResultadoDisponibilidadWebView2 Correcto(string version, string? runtimeFijo)
    {
        return new ResultadoDisponibilidadWebView2(true, string.Empty, version, runtimeFijo);
    }

    public static ResultadoDisponibilidadWebView2 Error(string? runtimeFijo)
    {
        var mensaje = string.IsNullOrWhiteSpace(runtimeFijo)
            ? "Microsoft Edge WebView2 Runtime no esta disponible. La aplicacion no instala componentes en el equipo."
            : $"El runtime portable de WebView2 no es valido o no se puede leer: {runtimeFijo}";

        return new ResultadoDisponibilidadWebView2(
            false,
            mensaje,
            null,
            runtimeFijo);
    }
}
