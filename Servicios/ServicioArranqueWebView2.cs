// (Autor: Alex Roman)
// Descripcion: Prepara WebView2 con perfiles locales aislados por sesion.

using System.IO;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using LanzadorScripts.Monitorizacion;

namespace LanzadorScripts.Servicios;

public sealed class ServicioArranqueWebView2
{
    private const int MaximoSesionesAnteriores = 0;
    private static readonly TimeSpan TiempoMaximoLimpiezaPerfil = TimeSpan.FromSeconds(15);
    private const string EjecutableWebView2 = "msedgewebview2.exe";

    private readonly ServicioDisponibilidadWebView2 _servicioDisponibilidadWebView2 = new();
    private readonly ServicioLogInicio _logInicio = new();

    public async Task<ResultadoArranqueWebView2> PrepararAsync(Func<WebView2> obtenerVista, Func<WebView2> recrearVista)
    {
        using var medicion = MonitorizacionAplicacion.Actual.Medir("webview2.inicio");
        var resultado = await PrepararInternoAsync(obtenerVista, recrearVista);
        medicion.Completar(resultado.Exito);
        return resultado;
    }

    private async Task<ResultadoArranqueWebView2> PrepararInternoAsync(Func<WebView2> obtenerVista, Func<WebView2> recrearVista)
    {
        var cronometroRuntime = Stopwatch.StartNew();
        var disponibilidad = ResultadoDisponibilidadWebView2.Error(null);
        string? runtimeFijo = null;

        try
        {
            await ServicioWebView2Evergreen.AsegurarAsync();
            disponibilidad = _servicioDisponibilidadWebView2.Comprobar();
        }
        catch (Exception ex)
        {
            await _logInicio.RegistrarExcepcionAsync("webview2.runtime.error", "instalar-evergreen",
                string.Empty, ex);
            return ResultadoArranqueWebView2.Error(ex.Message);
        }

        cronometroRuntime.Stop();
        if (!disponibilidad.Exito)
        {
            await _logInicio.RegistrarAsync(
                "webview2.runtime.error",
                disponibilidad.Mensaje,
                CrearDatosBase(
                    runtimeFijo,
                    RutasAplicacion.RutaRaizWebView2Usuario,
                    duracionRuntimeMs: cronometroRuntime.ElapsedMilliseconds));
            return ResultadoArranqueWebView2.Error(disponibilidad.Mensaje);
        }

        var versionRuntime = disponibilidad.Version;
        await _logInicio.RegistrarAsync(
            "webview2.runtime.sistema",
            "WebView2 usara el runtime compartido de Windows.",
            CrearDatosBase(null, RutasAplicacion.RutaRaizWebView2Usuario, versionRuntime,
                duracionRuntimeMs: cronometroRuntime.ElapsedMilliseconds));

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
                RutasAplicacion.RutaRaizWebView2Usuario,
                ex,
                CrearDatosBase(runtimeFijo, RutasAplicacion.RutaRaizWebView2Usuario, versionRuntime));
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

        string rutaPerfilRecuperacion;
        try
        {
            rutaPerfilRecuperacion = CrearPerfilRecuperacion();
        }
        catch (Exception ex)
        {
            await _logInicio.RegistrarExcepcionAsync(
                "webview2.perfil.error",
                "preparar-perfil-recuperacion",
                rutaPerfilPrincipal,
                ex,
                CrearDatosBase(runtimeFijo, rutaPerfilPrincipal, versionRuntime));

            return ResultadoArranqueWebView2.Error(
                $"No se pudo preparar el perfil local de WebView2. Revisa las politicas sobre {RutasAplicacion.RutaRaizWebView2Usuario}.");
        }

        var segundoIntento = await IntentarPrepararAsync(
            recrearVista(),
            runtimeFijo,
            rutaPerfilRecuperacion,
            versionRuntime,
            "perfil-recuperacion-local");
        if (segundoIntento.Exito)
        {
            await _logInicio.RegistrarAsync(
                "webview2.recuperado",
                "WebView2 se inicio con un perfil local alternativo y limpio.",
                CrearDatosBase(runtimeFijo, rutaPerfilRecuperacion, versionRuntime));
            return segundoIntento;
        }

        return ResultadoArranqueWebView2.Error(
            "No se pudo iniciar Microsoft Edge WebView2. Revisa las politicas corporativas de Edge/WebView2 o el log de arranque de LanzadorScripts.");
    }

    internal static bool EsVersionSistemaCompatible(string? version)
    {
        // Usa el runtime del sistema solo si no es anterior al runtime de recuperacion.
        var versionLimpia = version?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return Version.TryParse(versionLimpia, out var versionActual)
            && Version.TryParse(
                ServicioWebView2Evergreen.VersionMinima,
                out var versionMinima)
            && versionActual >= versionMinima;
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
            ServicioDirectoriosAplicacion.PrepararDatosWebView2();
            return CrearPerfilSesionSeguro(RutasAplicacion.RutaRaizWebView2Usuario);
        }
        catch (Exception ex)
        {
            var rutaRecuperacion = CrearPerfilRecuperacion();
            await _logInicio.RegistrarExcepcionAsync(
                "webview2.perfil.no_escribible",
                "preparar-perfil-principal",
                RutasAplicacion.RutaRaizWebView2Usuario,
                ex,
                CrearDatosBase(runtimeFijo, rutaRecuperacion, versionRuntime));

            return rutaRecuperacion;
        }
    }

    private static string CrearPerfilRecuperacion()
    {
        ServicioDirectoriosAplicacion.PrepararRecuperacionWebView2Local();
        return CrearPerfilSesionSeguro(RutasAplicacion.RutaRaizWebView2RecuperacionLocal);
    }

    internal static string CrearPerfilSesionSeguro(string raiz)
    {
        // Crea una sesion nueva con permisos explicitos para todos los procesos de WebView2.
        ServicioDirectoriosAplicacion.PrepararDirectorioWebView2(raiz);
        ProbarEscrituraDirectorio(raiz);
        LimpiarSesionesAnteriores(raiz);

        for (var intento = 0; intento < 10; intento++)
        {
            var rutaPerfil = Path.Combine(raiz, $"Sesion-{Guid.NewGuid():N}");
            if (!Directory.Exists(rutaPerfil) && !File.Exists(rutaPerfil))
            {
                ServicioDirectoriosAplicacion.PrepararPerfilWebView2(rutaPerfil);
                ProbarEscrituraDirectorio(rutaPerfil);
                return rutaPerfil;
            }
        }

        throw new IOException("No se pudo reservar una ruta nueva para el perfil de WebView2.");
    }

    public Task LimpiarPerfilSesionAsync(string? rutaPerfil)
    {
        // Espera la salida de WebView2 antes de retirar su perfil temporal.
        return Task.Run(() => LimpiarPerfilSesionConReintentos(rutaPerfil, TiempoMaximoLimpiezaPerfil));
    }

    public void LimpiarPerfilSesionSinEspera(string? rutaPerfil)
    {
        // Aplica un unico intento durante el cierre forzado de Windows.
        LimpiarPerfilSesionConReintentos(rutaPerfil, TimeSpan.Zero);
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
        // Verifica la escritura del directorio padre sin crear el perfil final.
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

    private static void LimpiarSesionesAnteriores(string raiz)
    {
        IEnumerable<DirectoryInfo> sesiones;
        try
        {
            sesiones = Directory
                .EnumerateDirectories(raiz, "Sesion-*", SearchOption.TopDirectoryOnly)
                .Where(EsRutaSesionWebView2)
                .Select(ruta => new DirectoryInfo(ruta))
                .OrderByDescending(directorio => directorio.LastWriteTimeUtc)
                .Skip(MaximoSesionesAnteriores)
                .ToList();
        }
        catch
        {
            return;
        }

        foreach (var directorio in sesiones)
        {
            try
            {
                ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(
                    raiz,
                    directorio.FullName);
            }
            catch
            {
            }
        }
    }

    private static bool EsRutaSesionWebView2(string ruta)
    {
        // Limita la limpieza a carpetas creadas por este servicio.
        var nombre = Path.GetFileName(ruta);
        return nombre.StartsWith("Sesion-", StringComparison.Ordinal)
            && Guid.TryParseExact(nombre["Sesion-".Length..], "N", out _);
    }

    private static void LimpiarPerfilSesionConReintentos(string? rutaPerfil, TimeSpan espera)
    {
        if (string.IsNullOrWhiteSpace(rutaPerfil))
        {
            return;
        }

        var raizGestionada = ResolverRaizPerfilGestionado(rutaPerfil);
        if (raizGestionada is null)
        {
            return;
        }

        var limite = DateTime.UtcNow + espera;
        do
        {
            try
            {
                if (!Directory.Exists(rutaPerfil))
                {
                    return;
                }

                ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(
                    raizGestionada,
                    rutaPerfil);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= limite)
                {
                    return;
                }

                Thread.Sleep(250);
            }
        } while (DateTime.UtcNow <= limite);
    }

    private static string? ResolverRaizPerfilGestionado(string rutaPerfil)
    {
        try
        {
            var completa = Path.GetFullPath(rutaPerfil);
            if (!EsRutaSesionWebView2(completa))
            {
                return null;
            }

            if (ServicioRutasSeguras.EstaDentroDeCarpeta(
                    RutasAplicacion.RutaRaizWebView2Usuario,
                    completa))
            {
                return RutasAplicacion.RutaRaizWebView2Usuario;
            }

            return ServicioRutasSeguras.EstaDentroDeCarpeta(
                    RutasAplicacion.RutaRaizWebView2RecuperacionLocal,
                    completa)
                ? RutasAplicacion.RutaRaizWebView2RecuperacionLocal
                : null;
        }
        catch
        {
            return null;
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
