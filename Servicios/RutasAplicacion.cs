// (Autor: Alex Roman)
// Descripcion: Rutas usadas por la aplicacion.

using System.IO;

namespace LanzadorScripts.Servicios;

public static class RutasAplicacion
{
    public static string RaizProgramData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LanzadorScripts");

    public static string RaizProgramFiles => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "LanzadorScripts");

    public static string RaizAppDataLegada => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LanzadorScripts");

    public static string RaizLocalAppDataLegada => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanzadorScripts");

    public static string RaizDatosUsuario => Path.Combine(
        RutaUsuarios,
        PerfilAplicacion.ObtenerIdentificadorUsuarioActual());

    public static string RutaUsuarios => Path.Combine(RaizProgramData, "Usuarios");

    public static string RutaConfiguracionUsuario => Path.Combine(RaizDatosUsuario, "configuracion.dat");

    public static string RutaConfiguracionUsuarioLegadaDat => Path.Combine(RaizAppDataLegada, "configuracion.dat");

    public static string RutaConfiguracionUsuarioLegadaJson => Path.Combine(RaizAppDataLegada, "configuracion.json");

    public static string RutaConfiguracionLegada => Path.Combine(AppContext.BaseDirectory, "configuracion.json");

    public static string RutaLogsUsuario => Path.Combine(RaizDatosUsuario, "Logs");

    public static string RutaAuditoria => Path.Combine(RaizDatosUsuario, "Auditoria");

    public static string RutaTokensUsuario => Path.Combine(RaizDatosUsuario, "Tokens");

    public static string RutaTokensUsuarioLegada => Path.Combine(RaizAppDataLegada, "Tokens");

    public static string RutaSeguridad => Path.Combine(RaizProgramData, "Seguridad");

    public static string RutaClaveArtefactos => Path.Combine(RutaSeguridad, "artefactos.key");

    public static string RutaStaging => Path.Combine(RaizProgramFiles, "Staging");

    public static string RutaRaizWebView2Usuario => Path.Combine(RaizDatosUsuario, "WebView2");

    public static string RutaPerfilWebView2 => Path.Combine(
        RutaRaizWebView2Usuario,
        "Perfil-v2");

    public static string RutaPerfilesWebView2Recuperacion => Path.Combine(
        RutaRaizWebView2Usuario,
        "Recuperacion");

    public static string RutaBaseWebView2RecuperacionSistema => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "Temp",
        "LanzadorScripts",
        "WebView2");

    public static string RutaRaizWebView2RecuperacionSistema => Path.Combine(
        RutaBaseWebView2RecuperacionSistema,
        PerfilAplicacion.ObtenerIdentificadorUsuarioActual());

    public static string RutaRuntimesWebView2 => Path.Combine(
        RaizProgramFiles,
        "Runtimes",
        "WebView2");

    public static string RutaRuntimeWebView2Portable => Path.Combine(
        AppContext.BaseDirectory,
        "WebView2Runtime");
}
