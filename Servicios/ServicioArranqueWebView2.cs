// (Autor: Alex Roman)
// Descripcion: Prepara WebView2 y recupera perfiles locales dañados.

using System.IO;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LanzadorScripts.Servicios;

public sealed class ServicioArranqueWebView2
{
    private const int MaximoCopiasDiagnostico = 3;
    private const string EjecutableWebView2 = "msedgewebview2.exe";

    private readonly ServicioDisponibilidadWebView2 _servicioDisponibilidadWebView2 = new();
    private readonly ServicioRuntimeWebView2Embebido _servicioRuntimeEmbebido = new();
    private readonly ServicioLogInicio _logInicio = new();

    public async Task<ResultadoArranqueWebView2> PrepararAsync(Func<WebView2> obtenerVista, Func<WebView2> recrearVista)
    {
        var cronometroRuntime = Stopwatch.StartNew();
        ResultadoRuntimeWebView2Embebido runtimeEmbebido;
        try
        {
            runtimeEmbebido = await PrepararRuntimeEnSegundoPlanoAsync(_servicioRuntimeEmbebido.Preparar);
        }
        catch (Exception ex)
        {
            cronometroRuntime.Stop();
            await _logInicio.RegistrarExcepcionAsync(
                "webview2.runtime.embebido.error",
                "preparar-runtime-embebido",
                RutasAplicacion.RutaRuntimesWebView2,
                ex,
                CrearDatosBase(
                    null,
                    RutasAplicacion.RutaPerfilWebView2,
                    duracionRuntimeMs: cronometroRuntime.ElapsedMilliseconds));
            return ResultadoArranqueWebView2.Error("No se pudo preparar WebView2 Fixed Runtime embebido.");
        }

        cronometroRuntime.Stop();
        if (!runtimeEmbebido.Exito && runtimeEmbebido.RecursoEncontrado)
        {
            await _logInicio.RegistrarAsync(
                "webview2.runtime.embebido.error",
                runtimeEmbebido.Mensaje,
                CrearDatosBase(
                    null,
                    RutasAplicacion.RutaPerfilWebView2,
                    duracionRuntimeMs: cronometroRuntime.ElapsedMilliseconds));
            return ResultadoArranqueWebView2.Error(runtimeEmbebido.Mensaje);
        }

        var runtimeFijo = runtimeEmbebido.RutaRuntime ?? ResolverRuntimeFijoPortable();
        var disponibilidad = _servicioDisponibilidadWebView2.Comprobar(runtimeFijo);
        if (!disponibilidad.Exito)
        {
            await _logInicio.RegistrarAsync(
                "webview2.runtime.error",
                disponibilidad.Mensaje,
                CrearDatosBase(
                    runtimeFijo,
                    RutasAplicacion.RutaPerfilWebView2,
                    duracionRuntimeMs: cronometroRuntime.ElapsedMilliseconds));
            return ResultadoArranqueWebView2.Error(disponibilidad.Mensaje);
        }

        var versionRuntime = disponibilidad.Version;
        if (runtimeEmbebido.Exito)
        {
            await _logInicio.RegistrarAsync(
                runtimeEmbebido.ExtraidoAhora ? "webview2.runtime.embebido.extraido" : "webview2.runtime.embebido.reutilizado",
                "WebView2 usara el runtime embebido autoextraido.",
                CrearDatosBase(
                    runtimeFijo,
                    RutasAplicacion.RutaPerfilWebView2,
                    versionRuntime,
                    hashRuntime: runtimeEmbebido.Hash,
                    duracionRuntimeMs: cronometroRuntime.ElapsedMilliseconds));
        }
        else if (!string.IsNullOrWhiteSpace(runtimeFijo))
        {
            await _logInicio.RegistrarAsync(
                "webview2.runtime.portable",
                "WebView2 usara el runtime portable.",
                CrearDatosBase(
                    runtimeFijo,
                    RutasAplicacion.RutaPerfilWebView2,
                    versionRuntime,
                    duracionRuntimeMs: cronometroRuntime.ElapsedMilliseconds));
        }
        else
        {
            await _logInicio.RegistrarAsync(
                "webview2.runtime.instalado",
                "WebView2 usara el runtime instalado del sistema.",
                CrearDatosBase(
                    runtimeFijo,
                    RutasAplicacion.RutaPerfilWebView2,
                    versionRuntime,
                    duracionRuntimeMs: cronometroRuntime.ElapsedMilliseconds));
        }

        string rutaPerfilPrincipal;
        try
        {
            rutaPerfilPrincipal = await PrepararPerfilPrincipalAsync(runtimeFijo, versionRuntime);
        }
        catch (Exception ex)
        {
            await _logInicio.RegistrarExcepcionAsync(
                "webview2.perfil.local.error",
                "preparar-perfil-local",
                RutasAplicacion.RutaPerfilWebView2,
                ex,
                CrearDatosBase(runtimeFijo, RutasAplicacion.RutaPerfilWebView2, versionRuntime));
            return ResultadoArranqueWebView2.Error(
                "No se pudo preparar una carpeta local segura para el perfil de WebView2.");
        }

        await _logInicio.RegistrarAsync(
            "webview2.inicio",
            "Preparando WebView2.",
            CrearDatosBase(runtimeFijo, rutaPerfilPrincipal, versionRuntime));

        var primerIntento = await IntentarPrepararAsync(obtenerVista(), runtimeFijo, rutaPerfilPrincipal, versionRuntime, "perfil-principal");
        if (primerIntento.Exito)
        {
            return primerIntento;
        }

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var segundoIntento = await IntentarPrepararAsync(recrearVista(), runtimeFijo, rutaPerfilPrincipal, versionRuntime, "perfil-principal-reintento");
        if (segundoIntento.Exito)
        {
            return segundoIntento;
        }

        string rutaPerfilRecuperado;
        try
        {
            rutaPerfilRecuperado = await PrepararPerfilRecuperadoAsync(rutaPerfilPrincipal);
        }
        catch (Exception ex)
        {
            await _logInicio.RegistrarExcepcionAsync(
                "webview2.perfil.error",
                "preparar-perfil-recuperacion",
                rutaPerfilPrincipal,
                ex,
                CrearDatosBase(runtimeFijo, rutaPerfilPrincipal, versionRuntime));

            return ResultadoArranqueWebView2.Error($"No se pudo preparar el perfil de WebView2. Revisa permisos sobre {RutasAplicacion.RutaPerfilWebView2}.");
        }

        var tercerIntento = await IntentarPrepararAsync(
            recrearVista(),
            runtimeFijo,
            rutaPerfilRecuperado,
            versionRuntime,
            "perfil-recuperado");
        if (tercerIntento.Exito)
        {
            var mensaje = rutaPerfilRecuperado == RutasAplicacion.RutaPerfilWebView2
                ? "WebView2 se recupero creando un perfil local limpio."
                : "WebView2 se inicio con un perfil alternativo de recuperacion.";
            await _logInicio.RegistrarAsync(
                "webview2.recuperado",
                mensaje,
                CrearDatosBase(runtimeFijo, rutaPerfilRecuperado, versionRuntime));
            return tercerIntento;
        }

        return ResultadoArranqueWebView2.Error(
            "No se pudo iniciar Microsoft Edge WebView2. Revisa las politicas corporativas de Edge/WebView2 o el log de arranque de LanzadorScripts.");
    }

    internal static Task<ResultadoRuntimeWebView2Embebido> PrepararRuntimeEnSegundoPlanoAsync(
        Func<ResultadoRuntimeWebView2Embebido> preparar)
    {
        // Ejecuta la extraccion sin bloquear el hilo de interfaz.
        return Task.Run(preparar);
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

    private async Task<string> PrepararPerfilPrincipalAsync(string? runtimeFijo, string? versionRuntime)
    {
        try
        {
            ServicioDirectoriosAplicacion.PrepararDatosUsuario();
            Directory.CreateDirectory(RutasAplicacion.RutaPerfilWebView2);
            ProbarEscrituraDirectorio(RutasAplicacion.RutaPerfilWebView2);
            return RutasAplicacion.RutaPerfilWebView2;
        }
        catch (Exception ex)
        {
            var rutaRecuperacion = CrearPerfilRecuperacion();
            await _logInicio.RegistrarExcepcionAsync(
                "webview2.perfil.no_escribible",
                "preparar-perfil-principal",
                RutasAplicacion.RutaPerfilWebView2,
                ex,
                CrearDatosBase(runtimeFijo, rutaRecuperacion, versionRuntime));

            return rutaRecuperacion;
        }
    }

    private async Task<string> PrepararPerfilRecuperadoAsync(string rutaPerfil)
    {
        var raiz = Path.GetDirectoryName(rutaPerfil) ?? RutasAplicacion.RutaPerfilesWebView2Recuperacion;
        Directory.CreateDirectory(raiz);

        if (!Directory.Exists(rutaPerfil))
        {
            Directory.CreateDirectory(rutaPerfil);
            return rutaPerfil;
        }

        var marcaTiempo = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
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

    private static string CrearPerfilRecuperacion()
    {
        var errores = new List<string>();
        foreach (var candidato in new[]
        {
            (Raiz: RutasAplicacion.RutaPerfilesWebView2Recuperacion, EsSistema: false),
            (Raiz: RutasAplicacion.RutaRaizWebView2RecuperacionSistema, EsSistema: true)
        })
        {
            try
            {
                if (candidato.EsSistema)
                {
                    ServicioDirectoriosAplicacion.PrepararRecuperacionWebView2Sistema();
                }
                else
                {
                    ServicioDirectoriosAplicacion.PrepararDatosUsuario();
                }

                var ruta = Path.Combine(candidato.Raiz, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ruta);
                ProbarEscrituraDirectorio(ruta);
                return ruta;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                errores.Add(ex.Message);
            }
        }

        throw new IOException("No se pudo crear el perfil de recuperacion de WebView2. " + string.Join(" ", errores));
    }

    internal static string? ResolverRuntimeFijoPortable()
    {
        // Localiza un runtime fijo copiado junto al EXE.
        var candidatos = EnumerarCandidatosRuntimePortable();
        foreach (var candidato in candidatos)
        {
            var ruta = ResolverCarpetaEjecutableWebView2(candidato);
            if (!string.IsNullOrWhiteSpace(ruta))
            {
                return ruta;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerarCandidatosRuntimePortable()
    {
        var baseAplicacion = AppContext.BaseDirectory;
        yield return RutasAplicacion.RutaRuntimeWebView2Portable;
        yield return Path.Combine(baseAplicacion, "WebView2FixedRuntime");

        IEnumerable<string> carpetas;
        try
        {
            carpetas = Directory.EnumerateDirectories(baseAplicacion, "Microsoft.WebView2.FixedVersionRuntime*");
        }
        catch
        {
            yield break;
        }

        foreach (var carpeta in carpetas)
        {
            yield return carpeta;
        }
    }

    private static string? ResolverCarpetaEjecutableWebView2(string carpeta)
    {
        if (!Directory.Exists(carpeta))
        {
            return null;
        }

        var ejecutableDirecto = Path.Combine(carpeta, EjecutableWebView2);
        if (File.Exists(ejecutableDirecto))
        {
            return Path.GetFullPath(carpeta);
        }

        try
        {
            var ejecutable = Directory
                .EnumerateFiles(carpeta, EjecutableWebView2, SearchOption.AllDirectories)
                .OrderBy(ruta => ruta.Length)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(ejecutable)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(ejecutable));
        }
        catch
        {
            return null;
        }
    }

    private static void ProbarEscrituraDirectorio(string ruta)
    {
        // Verifica escritura antes de entregar la ruta a Edge WebView2.
        var prueba = Path.Combine(ruta, $".lanzador_write_{Guid.NewGuid():N}.tmp");
        using (var flujo = new FileStream(prueba, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            flujo.WriteByte(1);
        }

        try
        {
            File.Delete(prueba);
        }
        catch
        {
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

    private static Dictionary<string, string?> CrearDatosBase(
        string? runtimeFijo,
        string rutaPerfil,
        string? versionRuntime = null,
        string? carpetaInformes = null,
        string? fase = null,
        string? hashRuntime = null,
        long? duracionRuntimeMs = null)
    {
        return new Dictionary<string, string?>
        {
            ["runtimeFijo"] = runtimeFijo,
            ["rutaPerfil"] = rutaPerfil,
            ["versionRuntime"] = versionRuntime,
            ["carpetaInformes"] = carpetaInformes,
            ["fase"] = fase,
            ["hashRuntime"] = hashRuntime,
            ["duracionRuntimeMs"] = duracionRuntimeMs?.ToString(CultureInfo.InvariantCulture)
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
