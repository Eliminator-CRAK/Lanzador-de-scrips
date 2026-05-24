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

    public static string RaizProgramData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LanzadorScripts");

    public static string RutaConfiguracionUsuario => Path.Combine(RaizAppData, "configuracion.dat");

    public static string RutaConfiguracionUsuarioLegadaJson => Path.Combine(RaizAppData, "configuracion.json");

    public static string RutaConfiguracionProgramData => Path.Combine(RaizProgramData, "configuracion.dat");

    public static string RutaConfiguracionProgramDataLegadaJson => Path.Combine(RaizProgramData, "configuracion.json");

    public static string RutaConfiguracionLegada => Path.Combine(AppContext.BaseDirectory, "configuracion.json");

    public static string RutaLogsUsuario => Path.Combine(RaizLocalAppData, "Logs");

    public static string RutaAuditoria => Path.Combine(RaizLocalAppData, "Auditoria");

    public static string RutaTokensUsuario => Path.Combine(RaizAppData, "Tokens");

    public static string RutaPerfilWebView2 => Path.Combine(RaizLocalAppData, "WebView2");

    public static string RutaRuntimeWebView2Fijo => Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
}
