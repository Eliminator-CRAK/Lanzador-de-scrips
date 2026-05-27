// (Autor: Alex Roman)
// Descripcion: Prepara WebView2 y recupera perfiles locales dañados.

using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LanzadorScripts.Servicios;

public sealed class ServicioArranqueWebView2
{
    private const int MaximoCopiasDiagnostico = 3;

    private readonly ServicioInstalacionWebView2 _servicioInstalacionWebView2 = new();
    private readonly ServicioLogInicio _logInicio = new();

    public async Task<ResultadoArranqueWebView2> PrepararAsync(Func<WebView2> obtenerVista, Func<WebView2> recrearVista)
    {
        var runtimeFijoCandidato = Directory.Exists(RutasAplicacion.RutaRuntimeWebView2Fijo)
            ? RutasAplicacion.RutaRuntimeWebView2Fijo
            : null;
        var runtimeFijo = _servicioInstalacionWebView2.ObtenerVersionRuta(runtimeFijoCandidato) is null
            ? null
            : runtimeFijoCandidato;

        var instalacion = await _servicioInstalacionWebView2.AsegurarInstaladoAsync(runtimeFijo);
        if (!instalacion.Exito)
        {
            await _logInicio.RegistrarAsync("webview2.runtime.error", instalacion.Mensaje, CrearDatosBase(runtimeFijo, RutasAplicacion.RutaPerfilWebView2));
            return ResultadoArranqueWebView2.Error(instalacion.Mensaje);
        }

        var versionRuntime = _servicioInstalacionWebView2.ObtenerVersionDisponible(runtimeFijo);
        await _logInicio.RegistrarAsync(
            "webview2.inicio",
            "Preparando WebView2.",
            CrearDatosBase(runtimeFijo, RutasAplicacion.RutaPerfilWebView2, versionRuntime));

        var primerIntento = await IntentarPrepararAsync(obtenerVista(), runtimeFijo, RutasAplicacion.RutaPerfilWebView2, versionRuntime, "perfil-principal");
        if (primerIntento.Exito)
        {
            return primerIntento;
        }

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var segundoIntento = await IntentarPrepararAsync(recrearVista(), runtimeFijo, RutasAplicacion.RutaPerfilWebView2, versionRuntime, "perfil-principal-reintento");
        if (segundoIntento.Exito)
        {
            return segundoIntento;
        }

        string rutaPerfilRecuperado;
        try
        {
            rutaPerfilRecuperado = await PrepararPerfilRecuperadoAsync(RutasAplicacion.RutaPerfilWebView2);
        }
        catch (Exception ex)
        {
            await _logInicio.RegistrarExcepcionAsync(
                "webview2.perfil.error",
                "preparar-perfil-recuperacion",
                RutasAplicacion.RutaPerfilWebView2,
                ex,
                CrearDatosBase(runtimeFijo, RutasAplicacion.RutaPerfilWebView2, versionRuntime));

            return ResultadoArranqueWebView2.Error("No se pudo preparar el perfil local de WebView2. Revisa permisos sobre %LocalAppData%\\LanzadorScripts.");
        }

        var tercerIntento = await IntentarPrepararAsync(recrearVista(), runtimeFijo, rutaPerfilRecuperado, versionRuntime, "perfil-recuperado");
        if (tercerIntento.Exito)
        {
            var mensaje = rutaPerfilRecuperado == RutasAplicacion.RutaPerfilWebView2
                ? "WebView2 se recupero creando un perfil limpio."
                : "WebView2 se inicio con un perfil temporal de recuperacion.";
            await _logInicio.RegistrarAsync("webview2.recuperado", mensaje, CrearDatosBase(runtimeFijo, rutaPerfilRecuperado, versionRuntime));
            return tercerIntento;
        }

        return ResultadoArranqueWebView2.Error(
            "No se pudo iniciar Microsoft Edge WebView2. Revisa las politicas corporativas de Edge/WebView2 o el log de arranque en %LocalAppData%\\LanzadorScripts\\Logs.");
    }

    private async Task<ResultadoArranqueWebView2> IntentarPrepararAsync(WebView2 vista, string? runtimeFijo, string rutaPerfil, string? versionRuntime, string fase)
    {
        try
        {
            Directory.CreateDirectory(rutaPerfil);
            var entorno = await CoreWebView2Environment.CreateAsync(runtimeFijo, rutaPerfil);
            await vista.EnsureCoreWebView2Async(entorno);

            vista.CoreWebView2.ProcessFailed += (_, e) => RegistrarFalloProcesoWebView2(entorno, rutaPerfil, e);
            entorno.BrowserProcessExited += (_, e) => RegistrarSalidaProcesoNavegador(entorno, rutaPerfil, e);

            await _logInicio.RegistrarAsync(
                "webview2.correcto",
                "WebView2 iniciado correctamente.",
                CrearDatosBase(runtimeFijo, rutaPerfil, entorno.BrowserVersionString, entorno.FailureReportFolderPath, fase));

            return ResultadoArranqueWebView2.Correcto(rutaPerfil);
        }
        catch (Exception ex)
        {
            await _logInicio.RegistrarExcepcionAsync(
                "webview2.error",
                fase,
                rutaPerfil,
                ex,
                CrearDatosBase(runtimeFijo, rutaPerfil, versionRuntime));

            return ResultadoArranqueWebView2.Error(ex.Message);
        }
    }

    private async Task<string> PrepararPerfilRecuperadoAsync(string rutaPerfil)
    {
        var raiz = Path.GetDirectoryName(rutaPerfil) ?? RutasAplicacion.RaizLocalAppData;
        Directory.CreateDirectory(raiz);

        if (!Directory.Exists(rutaPerfil))
        {
            Directory.CreateDirectory(rutaPerfil);
            return rutaPerfil;
        }

        var marcaTiempo = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var rutaDanada = Path.Combine(raiz, $"WebView2_Danado_{marcaTiempo}");

        try
        {
            Directory.Move(rutaPerfil, rutaDanada);
            Directory.CreateDirectory(rutaPerfil);
            await _logInicio.RegistrarAsync("webview2.perfil.renombrado", "Perfil WebView2 renombrado para diagnostico.", new Dictionary<string, string?>
            {
                ["origen"] = rutaPerfil,
                ["destino"] = rutaDanada
            });
            LimpiarCopiasDiagnostico(raiz);
            return rutaPerfil;
        }
        catch (Exception ex)
        {
            var rutaRecuperacion = Path.Combine(raiz, $"WebView2_Recuperacion_{marcaTiempo}");
            Directory.CreateDirectory(rutaRecuperacion);
            await _logInicio.RegistrarExcepcionAsync(
                "webview2.perfil.bloqueado",
                "renombrar-perfil",
                rutaPerfil,
                ex,
                new Dictionary<string, string?>
                {
                    ["rutaRecuperacion"] = rutaRecuperacion
                });
            LimpiarCopiasDiagnostico(raiz);
            return rutaRecuperacion;
        }
    }

    private static void LimpiarCopiasDiagnostico(string raiz)
    {
        foreach (var patron in new[] { "WebView2_Danado_*", "WebView2_Recuperacion_*" })
        {
            var directorios = Directory.GetDirectories(raiz, patron)
                .Select(ruta => new DirectoryInfo(ruta))
                .OrderByDescending(directorio => directorio.LastWriteTimeUtc)
                .Skip(MaximoCopiasDiagnostico);

            foreach (var directorio in directorios)
            {
                try
                {
                    directorio.Delete(recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    private void RegistrarFalloProcesoWebView2(CoreWebView2Environment entorno, string rutaPerfil, CoreWebView2ProcessFailedEventArgs evento)
    {
        _ = _logInicio.RegistrarAsync("webview2.proceso.fallo", "Proceso WebView2 fallido.", new Dictionary<string, string?>
        {
            ["rutaPerfil"] = rutaPerfil,
            ["versionRuntime"] = entorno.BrowserVersionString,
            ["carpetaInformes"] = entorno.FailureReportFolderPath,
            ["tipoFallo"] = evento.ProcessFailedKind.ToString(),
            ["motivo"] = evento.Reason.ToString(),
            ["codigoSalida"] = evento.ExitCode.ToString(),
            ["descripcionProceso"] = evento.ProcessDescription,
            ["moduloBloqueado"] = evento.FailureSourceModulePath
        });
    }

    private void RegistrarSalidaProcesoNavegador(CoreWebView2Environment entorno, string rutaPerfil, CoreWebView2BrowserProcessExitedEventArgs evento)
    {
        _ = _logInicio.RegistrarAsync("webview2.navegador.salida", "Proceso navegador WebView2 cerrado.", new Dictionary<string, string?>
        {
            ["rutaPerfil"] = rutaPerfil,
            ["versionRuntime"] = entorno.BrowserVersionString,
            ["carpetaInformes"] = entorno.FailureReportFolderPath,
            ["tipoSalida"] = evento.BrowserProcessExitKind.ToString(),
            ["idProceso"] = evento.BrowserProcessId.ToString()
        });
    }

    private static Dictionary<string, string?> CrearDatosBase(string? runtimeFijo, string rutaPerfil, string? versionRuntime = null, string? carpetaInformes = null, string? fase = null)
    {
        return new Dictionary<string, string?>
        {
            ["runtimeFijo"] = runtimeFijo,
            ["rutaPerfil"] = rutaPerfil,
            ["versionRuntime"] = versionRuntime,
            ["carpetaInformes"] = carpetaInformes,
            ["fase"] = fase
        };
    }
}

public sealed record ResultadoArranqueWebView2(bool Exito, string Mensaje, string? RutaPerfil)
{
    public static ResultadoArranqueWebView2 Correcto(string rutaPerfil)
    {
        return new ResultadoArranqueWebView2(true, string.Empty, rutaPerfil);
    }

    public static ResultadoArranqueWebView2 Error(string mensaje)
    {
        return new ResultadoArranqueWebView2(false, mensaje, null);
    }
}
