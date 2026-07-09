// (Autor: Alex Roman)
// Descripcion: Rutas usadas por la aplicacion.

using System.IO;

namespace LanzadorScripts.Servicios;

public static class RutasAplicacion
{
    public static string RaizAppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LanzadorScripts");

    public static string RaizLocalAppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanzadorScripts");

    public static string RutaConfiguracionUsuario => Path.Combine(RaizAppData, "configuracion.dat");

    public static string RutaConfiguracionUsuarioLegadaJson => Path.Combine(RaizAppData, "configuracion.json");

    public static string RutaConfiguracionLegada => Path.Combine(AppContext.BaseDirectory, "configuracion.json");

    public static string RutaLogsUsuario => Path.Combine(RaizLocalAppData, "Logs");

    public static string RutaAuditoria => Path.Combine(RaizLocalAppData, "Auditoria");

    public static string RutaTokensUsuario => Path.Combine(RaizAppData, "Tokens");

    public static string RutaPerfilWebView2 => Path.Combine(
        RaizLocalAppData,
        "WebView2",
        PerfilAplicacion.ObtenerPerfilUsuarioActual());

    public static string RutaRuntimesWebView2 => Path.Combine(
        RaizLocalAppData,
        "Runtimes",
        "WebView2");

    public static string RutaRuntimesWebView2Temporal => Path.Combine(
        Path.GetTempPath(),
        "LanzadorScripts",
        "Runtimes",
        "WebView2");

    public static string RutaRuntimeWebView2Portable => Path.Combine(
        AppContext.BaseDirectory,
        "WebView2Runtime");
}
