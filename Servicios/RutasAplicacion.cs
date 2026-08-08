// (Autor: Alex Roman)
// Descripcion: Rutas usadas por la aplicacion.

using System.IO;

namespace LanzadorScripts.Servicios;

public static class RutasAplicacion
{
    public static ContextoDistribucion Distribucion => ContextoDistribucion.ObtenerActual();

    public static string RaizProgramData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LanzadorScripts");

    public static string RaizProgramFiles => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "LanzadorScripts");

    public static string RaizAppDataLegada => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LanzadorScripts");

    public static string RaizLocalAppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanzadorScripts");

    public static string RaizLocalAppDataLegada => RaizLocalAppData;

    public static string RaizDatosUsuario => Distribucion.EsPortable
        ? Path.Combine(Distribucion.RaizPortable!, "Datos")
        : Path.Combine(
            RutaUsuarios,
            PerfilAplicacion.ObtenerIdentificadorUsuarioActual());

    public static string RutaUsuarios => Path.Combine(RaizProgramData, "Usuarios");

    public static string RutaConfiguracionUsuario => Path.Combine(RaizDatosUsuario, "configuracion.dat");

    public static string RutaConfiguracionUsuarioLegadaDat => Path.Combine(RaizAppDataLegada, "configuracion.dat");

    public static string RutaConfiguracionUsuarioLegadaJson => Path.Combine(RaizAppDataLegada, "configuracion.json");

    public static string RutaConfiguracionLegada => Path.Combine(AppContext.BaseDirectory, "configuracion.json");

    public static string RutaLogsUsuario => Distribucion.EsPortable
        ? Path.Combine(Distribucion.RaizPortable!, "Logs")
        : Path.Combine(RaizDatosUsuario, "Logs");

    public static string RutaTokensUsuario => Path.Combine(RaizDatosUsuario, "Tokens");

    public static string RutaTokensUsuarioLegada => Path.Combine(RaizAppDataLegada, "Tokens");

    public static string RutaStaging => Path.Combine(RaizDatosUsuario, "Staging");

    public static string RutaRaizWebView2Usuario => Distribucion.EsPortable
        ? Path.Combine(Distribucion.RaizPortable!, "WebView2", "Sesiones")
        : Path.Combine(
            RaizLocalAppData,
            "WebView2-v5",
            "Sesiones");

    public static string RutaRaizWebView2RecuperacionLocal => Distribucion.EsPortable
        ? Path.Combine(Distribucion.RaizPortable!, "WebView2-Recuperacion", "Sesiones")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanzadorScripts-WebView2-Recuperacion-v5",
            "Sesiones");

    public static string RutaRuntimesWebView2 => Distribucion.EsPortable
        ? Path.Combine(Distribucion.RaizPortable!, "Runtimes", "WebView2")
        : Path.Combine(
            RaizProgramFiles,
            "Runtimes",
            "WebView2");

    public static string RutaRuntimeWebView2Portable => RutaRuntimesWebView2;
}
