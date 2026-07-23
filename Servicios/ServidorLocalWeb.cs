// (Autor: Alex Roman)
// Descripcion: Servidor local que entrega el cliente web y la API de ejecucion.

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LanzadorScripts.Modelos;

namespace LanzadorScripts.Servicios;

public sealed class ServidorLocalWeb : IDisposable
{
    private const string NombreCookieSesion = "LanzadorScriptsSesion";
    private const int LongitudMaximaDetalleErrorBackend = 360;
    internal const string MensajeBackendLocalNoDisponible = "El backend local no pudo procesar la solicitud.";
    internal const string MensajeCarpetaPermisosNoDisponible = "La carpeta remota de permisos no esta disponible.";
    internal const string MensajeCarpetaScriptsNoDisponible = "La carpeta remota de scripts no esta disponible.";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> IndiceRecursosCliente = new(CrearIndiceRecursosCliente);
    private static readonly Regex EtiquetaVersionCliente = new(
        "children:\"v[0-9]+[.][0-9]+[.][0-9]+(?:[.][0-9]+)?\"",
        RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpListener _escuchador = new();
    private readonly CancellationTokenSource _cancelacion = new();
    private readonly ServicioConfiguracion _servicioConfiguracion = new();
    private readonly ServicioTokensAdmin _servicioTokensAdmin = new();
    private readonly ServicioTokenMaestro _servicioTokenMaestro;
    private readonly ServicioPaquetesConfiguracion _servicioPaquetesConfiguracion;
    private readonly ServicioValidacionScripts _servicioValidacionScripts = new();
    private readonly ServicioSeguridadScripts _servicioSeguridadScripts = new();
    private readonly ServicioArtefactosProtegidos _servicioArtefactos;
    private readonly ServicioCatalogoScripts _servicioCatalogoScripts;
    private readonly ServicioAuditoria _servicioAuditoria = new();
    private readonly ConfiguracionLanzador? _configuracionFija;
    private readonly GestorEjecucionesWeb _gestorEjecuciones;
    private readonly object _bloqueoEmergencia = new();
    private readonly object _bloqueoDiagnosticoAjustes = new();
    private readonly string _tokenSesion = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _tokenApiInterno = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private SesionEmergencia? _sesionEmergencia;
    private Task<DiagnosticoPermisos>? _tareaDiagnosticoAjustes;
    private DiagnosticoPermisos? _diagnosticoAjustesReciente;
    private DateTimeOffset _diagnosticoAjustesValidoHasta;
    private volatile bool _modoDesarrolloFirmas;

    private ServidorLocalWeb(int puerto)
        : this(puerto, new ServicioTokenMaestro())
    {
    }

    private ServidorLocalWeb(
        int puerto,
        ServicioTokenMaestro servicioTokenMaestro,
        string? rutaStaging = null,
        ServicioArtefactosProtegidos? servicioArtefactos = null)
    {
        UrlBase = new Uri($"http://127.0.0.1:{puerto}/");
        _escuchador.Prefixes.Add(UrlBase.ToString());
        _servicioTokenMaestro = servicioTokenMaestro;
        _servicioArtefactos = servicioArtefactos ?? new ServicioArtefactosProtegidos();
        _servicioPaquetesConfiguracion = new ServicioPaquetesConfiguracion(
            new ServicioCifradoAplicacion(),
            _servicioArtefactos);
        _servicioCatalogoScripts = new ServicioCatalogoScripts(_servicioArtefactos);
        _gestorEjecuciones = new GestorEjecucionesWeb(
            _servicioAuditoria,
            _servicioSeguridadScripts,
            rutaStaging);
    }

    private ServidorLocalWeb(int puerto, ConfiguracionLanzador configuracion)
        : this(
            puerto,
            new ServicioTokenMaestro(),
            Path.Combine(configuracion.RutaLogs, ".staging-pruebas"))
    {
        _configuracionFija = configuracion;
    }

    private ServidorLocalWeb(int puerto, ConfiguracionLanzador configuracion, ServicioTokenMaestro servicioTokenMaestro)
        : this(
            puerto,
            servicioTokenMaestro,
            Path.Combine(configuracion.RutaLogs, ".staging-pruebas"))
    {
        _configuracionFija = configuracion;
    }

    private ServidorLocalWeb(
        int puerto,
        ConfiguracionLanzador configuracion,
        ServicioTokenMaestro servicioTokenMaestro,
        ServicioArtefactosProtegidos servicioArtefactos)
        : this(
            puerto,
            servicioTokenMaestro,
            Path.Combine(configuracion.RutaLogs, ".staging-pruebas"),
            servicioArtefactos)
    {
        _configuracionFija = configuracion;
    }

    public Uri UrlBase { get; }

    public string TokenApiInterno => _tokenApiInterno;

    public static ServidorLocalWeb Iniciar()
    {
        var servidor = new ServidorLocalWeb(ReservarPuertoLibre());
        servidor._escuchador.Start();
        _ = servidor.EscucharAsync();
        return servidor;
    }

    internal static ServidorLocalWeb IniciarParaPruebas(ConfiguracionLanzador configuracion)
    {
        // Inicia el servidor con configuracion aislada de pruebas.
        var servidor = new ServidorLocalWeb(ReservarPuertoLibre(), configuracion);
        servidor._escuchador.Start();
        _ = servidor.EscucharAsync();
        return servidor;
    }

    internal static ServidorLocalWeb IniciarParaPruebas(ConfiguracionLanzador configuracion, ServicioTokenMaestro servicioTokenMaestro)
    {
        // Inicia el servidor con servicios aislados de pruebas.
        var servidor = new ServidorLocalWeb(ReservarPuertoLibre(), configuracion, servicioTokenMaestro);
        servidor._escuchador.Start();
        _ = servidor.EscucharAsync();
        return servidor;
    }

    internal static ServidorLocalWeb IniciarParaPruebas(
        ConfiguracionLanzador configuracion,
        ServicioArtefactosProtegidos servicioArtefactos)
    {
        // Inicia el servidor con claves aisladas de pruebas.
        var servidor = new ServidorLocalWeb(
            ReservarPuertoLibre(),
            configuracion,
            new ServicioTokenMaestro(),
            servicioArtefactos);
        servidor._escuchador.Start();
        _ = servidor.EscucharAsync();
        return servidor;
    }

    internal static ServidorLocalWeb IniciarParaPruebas(
        ConfiguracionLanzador configuracion,
        ServicioTokenMaestro servicioTokenMaestro,
        ServicioArtefactosProtegidos servicioArtefactos)
    {
        // Inicia el servidor con todos los servicios aislados de pruebas.
        var servidor = new ServidorLocalWeb(
            ReservarPuertoLibre(),
            configuracion,
            servicioTokenMaestro,
            servicioArtefactos);
        servidor._escuchador.Start();
        _ = servidor.EscucharAsync();
        return servidor;
    }

    public void Dispose()
    {
        _cancelacion.Cancel();
        _gestorEjecuciones.Dispose();

        if (_escuchador.IsListening)
        {
            _escuchador.Stop();
        }

        _escuchador.Close();
        _cancelacion.Dispose();
    }

    private async Task EscucharAsync()
    {
        while (!_cancelacion.IsCancellationRequested)
        {
            try
            {
                var contexto = await _escuchador.GetContextAsync();
                _ = Task.Run(() => ProcesarPeticionAsync(contexto));
            }
            catch when (_cancelacion.IsCancellationRequested)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private async Task ProcesarPeticionAsync(HttpListenerContext contexto)
    {
        try
        {
            var ruta = contexto.Request.Url?.AbsolutePath ?? "/";

            if (ruta.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                if (!SesionApiValida(contexto.Request, ruta))
                {
                    await EscribirJsonAsync(contexto, 403, new { error = "Sesion local no valida." });
                    return;
                }

                await ProcesarApiAsync(contexto, ruta);
                return;
            }

            await EntregarClienteAsync(contexto, ruta);
        }
        catch (Exception ex)
        {
            if (contexto.Response.OutputStream.CanWrite)
            {
                var ruta = contexto.Request.Url?.AbsolutePath ?? "/";
                var mensaje = CrearMensajeErrorBackend(ruta, ex);
                var detalleAuditoria = $"{ex.GetType().Name}: {ServicioRedaccionSecretos.Sanitizar(ex.Message)}";
                await _servicioAuditoria.RegistrarErrorInternoAsync("api.backend_local.error", detalleAuditoria);
                await EscribirJsonAsync(contexto, 503, new
                {
                    error = mensaje,
                    avisoConexion = mensaje,
                    ruta,
                    tipo = ex.GetType().Name
                });
            }
        }
        finally
        {
            try
            {
                contexto.Response.Close();
            }
            catch
            {
            }
        }
    }

    internal static string CrearMensajeErrorBackend(string ruta, Exception ex)
    {
        var rutaSegura = string.IsNullOrWhiteSpace(ruta) ? "/" : ruta.Trim();
        var detalle = CrearDetalleErrorBackend(ex);
        return $"{MensajeBackendLocalNoDisponible} Ruta: {rutaSegura}. Detalle: {detalle}";
    }

    private static string CrearDetalleErrorBackend(Exception ex)
    {
        var mensaje = ServicioRedaccionSecretos.Sanitizar(ex.Message).Trim();
        if (string.IsNullOrWhiteSpace(mensaje))
        {
            mensaje = ex.GetType().Name;
        }

        mensaje = Regex.Replace(mensaje, "\\s+", " ");
        return mensaje.Length <= LongitudMaximaDetalleErrorBackend
            ? mensaje
            : mensaje[..LongitudMaximaDetalleErrorBackend] + "...";
    }

    private async Task ProcesarApiAsync(HttpListenerContext contexto, string ruta)
    {
        var metodo = contexto.Request.HttpMethod.ToUpperInvariant();
        var partes = ruta.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (metodo == "GET" && ruta.Equals("/api/diagnostico", StringComparison.OrdinalIgnoreCase))
        {
            var ensamblado = Assembly.GetExecutingAssembly();
            var recursos = ensamblado.GetManifestResourceNames().Where(r => r.Contains("ClienteWeb")).OrderBy(r => r).ToList();
            await EscribirJsonAsync(contexto, 200, new { recursos });
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/salud", StringComparison.OrdinalIgnoreCase))
        {
            var configuracion = CargarConfiguracion();
            var diagnosticoPermisos = ObtenerDiagnosticoPermisos();
            var emergencia = ObtenerEmergenciaActiva();
            var auditoriaCorrecta = string.IsNullOrWhiteSpace(_servicioAuditoria.UltimoError);
            var saludAutenticada = SesionApiValidaPrivada(contexto.Request);
            var politica = diagnosticoPermisos.EstaDisponible
                ? ServicioSeguridadScripts.LeerPolitica(diagnosticoPermisos.Permisos)
                : null;
            var scriptsElevadosConfigurados = politica?.ScriptsElevadosPermitidos.Count ?? 0;
            var brokerDisponible = ServicioBrokerElevado.EstaDisponible();
            var brokerCorrecto = scriptsElevadosConfigurados == 0 || brokerDisponible;
            var estadoSalud = diagnosticoPermisos.EstaDisponible && auditoriaCorrecta && brokerCorrecto ? "ok" : "degradado";
            if (!saludAutenticada)
            {
                await EscribirJsonAsync(contexto, 200, new
                {
                    estado = estadoSalud,
                    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "desconocida"
                });
                return;
            }

            await EscribirJsonAsync(contexto, 200, new
            {
                estado = estadoSalud,
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "desconocida",
                equipo = Environment.MachineName,
                rutas = new
                {
                    scripts = configuracion.RutaScripts,
                    permisos = diagnosticoPermisos.Ruta,
                    logs = configuracion.RutaLogs,
                    auditoria = RutasAplicacion.RutaAuditoria,
                    perfilWebView2 = RutasAplicacion.RutaPerfilWebView2
                },
                permisos = new
                {
                    estado = diagnosticoPermisos.Estado.ToString(),
                    disponible = diagnosticoPermisos.EstaDisponible,
                    mensaje = diagnosticoPermisos.Mensaje
                },
                auditoria = new
                {
                    disponible = auditoriaCorrecta,
                    ultimoError = _servicioAuditoria.UltimoError
                },
                webView2 = new
                {
                    perfil = RutasAplicacion.RutaPerfilWebView2,
                    runtimeInstalado = new ServicioDisponibilidadWebView2().Comprobar().Exito
                },
                ejecuciones = new
                {
                    activas = _gestorEjecuciones.RecuentoActivas
                },
                broker = new
                {
                    estado = brokerDisponible ? "disponible" : "no_disponible",
                    scriptsElevadosConfigurados,
                    mensaje = scriptsElevadosConfigurados == 0
                        ? "Sin scripts elevados configurados."
                        : brokerDisponible
                            ? "Broker elevado minimo disponible bajo demanda."
                            : "Broker elevado no disponible para scripts allowlistados."
                },
                emergencia = new
                {
                    activa = emergencia is not null,
                    venceUtc = emergencia?.VenceUtc,
                    motivo = emergencia?.Motivo ?? string.Empty
                },
                ultimoErrorCritico = auditoriaCorrecta ? string.Empty : _servicioAuditoria.UltimoError
            });
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/usuario", StringComparison.OrdinalIgnoreCase))
        {
            var configuracion = CargarConfiguracion();
            var tareaPermisos = ObtenerDiagnosticoAjustesAsync();
            var tareaScripts = RutaScriptsInaccesibleAsync(configuracion.RutaScripts);
            await Task.WhenAll(tareaPermisos, tareaScripts);
            var diagnosticoPermisos = await tareaPermisos;
            var usuario = ObtenerUsuarioActual(diagnosticoPermisos);
            var tokenAdmin = AsegurarTokenAdmin(usuario);
            var scriptsInaccesibles = await tareaScripts;
            var avisoConexion = CrearAvisoConexion(diagnosticoPermisos.ModoOffline, scriptsInaccesibles);
            await EscribirJsonAsync(
                contexto,
                200,
                CrearUsuarioClienteSesion(
                    usuario,
                    diagnosticoPermisos,
                    tokenAdmin,
                    avisoConexion,
                    diagnosticoPermisos.ModoOffline || scriptsInaccesibles));
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/token-maestro/generar", StringComparison.OrdinalIgnoreCase))
        {
            if (!_servicioTokenMaestro.PuedeGenerar())
            {
                await EscribirJsonAsync(contexto, 409, new { error = "No se encontro el certificado privado de Alex Roman con clave RSA para generar el token maestro." });
                return;
            }

            await EscribirJsonAsync(contexto, 200, new { token = _servicioTokenMaestro.Generar() });
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/token-maestro/desbloquear", StringComparison.OrdinalIgnoreCase))
        {
            if (ObtenerEmergenciaActiva() is not null)
            {
                await EscribirJsonAsync(contexto, 409, new { error = "Ya existe una sesion de emergencia activa." });
                return;
            }

            var diagnosticoPermisos = await ObtenerDiagnosticoAjustesAsync();
            if (diagnosticoPermisos.EstaDisponible)
            {
                await EscribirJsonAsync(contexto, 403, new { error = "El token maestro solo esta disponible si no se puede leer el archivo de permisos." });
                return;
            }

            var cuerpo = await LeerJsonAsync(contexto.Request);
            var token = LeerTexto(cuerpo, "token", string.Empty);

            var usuarioActual = WindowsIdentity.GetCurrent().Name;
            if (!_servicioTokenMaestro.Validar(token, out var payload, out var motivoToken))
            {
                await _servicioAuditoria.RegistrarEventoSeguridadAsync(
                    "seguridad.emergencia",
                    usuarioActual,
                    null,
                    "denegado",
                    motivoToken);
                await EscribirJsonAsync(contexto, 403, new { error = "Token maestro no valido." });
                return;
            }

            var emergencia = ActivarEmergencia(payload!, usuarioActual, diagnosticoPermisos.Estado);

            await _servicioAuditoria.RegistrarEventoSeguridadAsync(
                "seguridad.emergencia",
                usuarioActual,
                null,
                "activado",
                $"VenceUtc: {emergencia.VenceUtc:O}; Emisor: {payload!.UsuarioEmisor}");

            var tokenAdmin = _servicioTokensAdmin.ObtenerOCrear(usuarioActual);
            await EscribirJsonAsync(contexto, 200, new
            {
                exito = true,
                mensaje = "Acceso maestro desbloqueado para esta sesion.",
                venceUtc = emergencia.VenceUtc,
                tokenAdmin = tokenAdmin.Valor,
                emisor = payload
            });
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/scripts", StringComparison.OrdinalIgnoreCase))
        {
            var carpeta = contexto.Request.QueryString["carpeta"] ?? string.Empty;
            await EscribirJsonAsync(contexto, 200, ObtenerScriptsParaCliente(carpeta));
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/diagnostico-ejecucion", StringComparison.OrdinalIgnoreCase))
        {
            await ProcesarDiagnosticoEjecucionAsync(contexto);
            return;
        }

        if (ruta.Equals("/api/desarrollo-firmas", StringComparison.OrdinalIgnoreCase))
        {
            await ProcesarModoDesarrolloFirmasAsync(contexto, metodo);
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/ajustes", StringComparison.OrdinalIgnoreCase))
        {
            var diagnosticoAjustes = await ObtenerDiagnosticoAjustesAsync();
            if (!await RequerirAdministradorAsync(contexto, diagnosticoAjustes))
            {
                return;
            }

            await EscribirJsonAsync(contexto, 200, new
            {
                permisos = diagnosticoAjustes.Permisos,
                mensaje = diagnosticoAjustes.EstaDisponible
                    ? "Datos de ajustes cargados exitosamente."
                    : diagnosticoAjustes.Mensaje,
                avisoConexion = diagnosticoAjustes.EstaDisponible ? string.Empty : diagnosticoAjustes.Mensaje
            });
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/ajustes", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            if (SesionEmergenciaSinAccesoRemoto())
            {
                await EscribirJsonAsync(contexto, 409, new
                {
                    error = "La sesion de emergencia se activo con la carpeta de permisos inaccesible. Reinicia la aplicacion cuando la carpeta vuelva a estar disponible antes de guardar permisos."
                });
                return;
            }

            var cuerpo = await LeerJsonAsync(contexto.Request);
            var resultado = GuardarPermisos(cuerpo ?? new JsonObject());
            if (resultado.PermisosGuardados)
            {
                InvalidarDiagnosticoAjustes();
            }

            await EscribirJsonAsync(contexto, 200, new
            {
                exito = true,
                mensaje = resultado.PermisosGuardados
                    ? "Ajustes guardados exitosamente."
                    : "Los permisos no se pudieron publicar en la carpeta configurada.",
                avisoConexion = resultado.AvisoConexion
            });
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/configuracion-app", StringComparison.OrdinalIgnoreCase))
        {
            var diagnosticoAjustes = await ObtenerDiagnosticoAjustesAsync();
            if (!await RequerirAdministradorAsync(contexto, diagnosticoAjustes))
            {
                return;
            }

            var configuracion = CargarConfiguracion();
            await EscribirJsonAsync(contexto, 200, new
            {
                rutaPermisos = configuracion.RutaPermisos,
                carpetaScripts = configuracion.RutaScripts
            });
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/configuracion-app", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            if (SesionEmergenciaSinAccesoRemoto())
            {
                await EscribirJsonAsync(contexto, 409, new
                {
                    error = "La sesion de emergencia se activo con la carpeta de permisos inaccesible. Reinicia la aplicacion cuando la carpeta vuelva a estar disponible antes de cambiar las rutas."
                });
                return;
            }

            var cuerpo = await LeerJsonAsync(contexto.Request);
            var configuracion = CargarConfiguracion();
            var nuevaRutaPermisos = LeerTexto(cuerpo, "rutaPermisos", configuracion.RutaPermisos).Trim();
            var nuevaRutaScripts = LeerTexto(cuerpo, "carpetaScripts", configuracion.RutaScripts).Trim();
            var validacion = _servicioValidacionScripts.ValidarConfiguracionBasica(nuevaRutaScripts, nuevaRutaPermisos);
            if (!validacion.EsValida)
            {
                await EscribirJsonAsync(contexto, 400, new { error = validacion.Mensaje });
                return;
            }

            configuracion.RutaPermisos = nuevaRutaPermisos;
            configuracion.RutaScripts = nuevaRutaScripts;
            _servicioConfiguracion.Guardar(configuracion);
            InvalidarDiagnosticoAjustes();
            var avisoConfiguracion = _servicioValidacionScripts.CrearAvisoConfiguracionNoDisponible(nuevaRutaScripts, nuevaRutaPermisos);
            await EscribirJsonAsync(contexto, 200, new
            {
                exito = true,
                mensaje = string.IsNullOrWhiteSpace(avisoConfiguracion)
                    ? "Configuracion de la aplicacion guardada exitosamente."
                    : "Configuracion de la aplicacion guardada con advertencias.",
                avisoConfiguracion
            });
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/configuracion-paquete/exportar", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            if (SesionEmergenciaSinAccesoRemoto())
            {
                await EscribirJsonAsync(contexto, 409, new
                {
                    error = "No se puede exportar configuracion desde una sesion de emergencia iniciada con la carpeta de permisos inaccesible."
                });
                return;
            }

            try
            {
                var paquete = _servicioPaquetesConfiguracion.Exportar(CargarConfiguracion(), ObtenerPermisos());
                await EscribirJsonAsync(contexto, 200, paquete);
            }
            catch (Exception ex)
            {
                await _servicioAuditoria.RegistrarErrorInternoAsync("configuracion.exportar.error", ex.GetType().Name);
                await EscribirJsonAsync(contexto, 500, new
                {
                    error = "No se pudo exportar la configuracion. " + ServicioRedaccionSecretos.Sanitizar(ex.Message)
                });
            }

            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/configuracion-paquete/importar", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            if (SesionEmergenciaSinAccesoRemoto())
            {
                await EscribirJsonAsync(contexto, 409, new
                {
                    error = "No se puede importar configuracion desde una sesion de emergencia iniciada con la carpeta de permisos inaccesible."
                });
                return;
            }

            await ProcesarImportacionPaqueteConfiguracionAsync(contexto);
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/subcarpetas-scripts", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            if (SesionEmergenciaSinAccesoRemoto())
            {
                await EscribirJsonAsync(contexto, 409, new
                {
                    error = "No se pueden leer las subcarpetas desde una sesion de emergencia iniciada sin acceso remoto."
                });
                return;
            }

            await EscribirJsonAsync(contexto, 200, ObtenerSubcarpetasScripts());
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/catalogo-scripts", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            if (SesionEmergenciaSinAccesoRemoto())
            {
                await EscribirJsonAsync(contexto, 409, new
                {
                    error = "No se puede leer el catalogo desde una sesion de emergencia iniciada sin acceso remoto."
                });
                return;
            }

            var diagnosticoCatalogo = ObtenerDiagnosticoCatalogo();
            await EscribirJsonAsync(contexto, 200, new
            {
                valido = diagnosticoCatalogo.EstaDisponible,
                mensaje = diagnosticoCatalogo.Mensaje,
                keyId = diagnosticoCatalogo.Catalogo?.KeyId ?? _servicioArtefactos.KeyId,
                generadoUtc = diagnosticoCatalogo.Catalogo?.GeneradoUtc,
                scripts = _servicioCatalogoScripts.ObtenerEstados(
                    ObtenerScriptsInternos(),
                    diagnosticoCatalogo.Catalogo)
            });
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/catalogo-scripts", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            if (SesionEmergenciaSinAccesoRemoto())
            {
                await EscribirJsonAsync(contexto, 409, new
                {
                    error = "No se puede publicar el catalogo desde una sesion de emergencia iniciada con la carpeta de permisos inaccesible."
                });
                return;
            }

            var cuerpo = await LeerJsonAsync(contexto.Request);
            var seleccionados = LeerArrayTexto(cuerpo?["scriptIds"] as JsonArray);
            try
            {
                var configuracion = CargarConfiguracion();
                var catalogo = _servicioCatalogoScripts.Crear(
                    _servicioValidacionScripts.DescubrirScripts(configuracion.RutaScripts),
                    seleccionados);
                var rutaCatalogo = ServicioCatalogoScripts.ObtenerRuta(
                    ObtenerRutaPermisosCompleta(configuracion));
                _servicioCatalogoScripts.Guardar(rutaCatalogo, catalogo);
                await _servicioAuditoria.RegistrarEventoSeguridadAsync(
                    "seguridad.catalogo.publicado",
                    WindowsIdentity.GetCurrent().Name,
                    null,
                    "publicado",
                    $"Catalogo publicado con {catalogo.Scripts.Count} scripts.");
                await EscribirJsonAsync(contexto, 200, new
                {
                    exito = true,
                    mensaje = "Catalogo cifrado y firmado correctamente.",
                    totalScripts = catalogo.Scripts.Count,
                    catalogo.KeyId,
                    catalogo.GeneradoUtc
                });
            }
            catch (Exception ex)
            {
                await EscribirJsonAsync(contexto, 400, new
                {
                    error = ServicioRedaccionSecretos.Sanitizar(ex.Message)
                });
            }

            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/ejecuciones", StringComparison.OrdinalIgnoreCase))
        {
            await ProcesarInicioEjecucionAsync(contexto);
            return;
        }

        if (metodo == "GET" && partes.Length == 4 && partes[1] == "ejecuciones" && partes[3] == "eventos")
        {
            if (!Guid.TryParse(partes[2], out var ejecucionId))
            {
                await EscribirJsonAsync(contexto, 400, new { error = "Identificador de ejecucion no valido." });
                return;
            }

            await _gestorEjecuciones.EnviarEventosAsync(ejecucionId, contexto.Request, contexto.Response, _cancelacion.Token);
            return;
        }

        if (metodo == "POST" && partes.Length == 4 && partes[1] == "ejecuciones" && partes[3] == "cancelar")
        {
            if (Guid.TryParse(partes[2], out var ejecucionId))
            {
                _gestorEjecuciones.Cancelar(ejecucionId);
            }

            await EscribirJsonAsync(contexto, 200, new { exito = true });
            return;
        }

        if (metodo == "POST" && partes.Length == 4 && partes[1] == "ejecuciones" && partes[3] == "entrada")
        {
            var cuerpo = await LeerJsonAsync(contexto.Request);
            if (Guid.TryParse(partes[2], out var ejecucionId))
            {
                await _gestorEjecuciones.EnviarEntradaAsync(ejecucionId, LeerTexto(cuerpo, "texto", string.Empty));
            }

            await EscribirJsonAsync(contexto, 200, new { exito = true });
            return;
        }

        await EscribirJsonAsync(contexto, 404, new { error = "Ruta no encontrada." });
    }

    private async Task ProcesarImportacionPaqueteConfiguracionAsync(HttpListenerContext contexto)
    {
        var cuerpo = await LeerJsonAsync(
            contexto.Request,
            ServicioPaquetesConfiguracion.LongitudMaximaBase64 + 4096);
        var contenidoBase64 = LeerTexto(cuerpo, "contenidoBase64", string.Empty);
        if (string.IsNullOrWhiteSpace(contenidoBase64))
        {
            await EscribirJsonAsync(contexto, 400, new { error = "No se recibio el paquete de configuracion." });
            return;
        }

        try
        {
            if (contenidoBase64.Length > ServicioPaquetesConfiguracion.LongitudMaximaBase64)
            {
                await EscribirJsonAsync(contexto, 413, new { error = "El paquete de configuracion supera el limite de 16 MiB." });
                return;
            }

            var datos = Convert.FromBase64String(contenidoBase64);
            try
            {
                if (datos.Length > ServicioPaquetesConfiguracion.LongitudMaximaContenido)
                {
                    await EscribirJsonAsync(contexto, 413, new { error = "El paquete de configuracion supera el limite de 16 MiB." });
                    return;
                }

                var contenido = new UTF8Encoding(false, true).GetString(datos);
                var configuracion = CargarConfiguracion();
                var importacion = _servicioPaquetesConfiguracion.ImportarContenido(contenido, configuracion);
                _servicioConfiguracion.Guardar(importacion.Configuracion);
                InvalidarDiagnosticoAjustes();
                if (importacion.Permisos is not null)
                {
                    _servicioPaquetesConfiguracion.GuardarPermisosImportados(importacion.Configuracion, importacion.Permisos);
                }

                await EscribirJsonAsync(contexto, 200, new { exito = true, mensaje = "Configuracion importada correctamente en el servicio." });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(datos);
            }
        }
        catch (FormatException)
        {
            await EscribirJsonAsync(contexto, 400, new { error = "El contenido del paquete no esta en Base64 valido." });
        }
        catch (Exception ex)
        {
            await EscribirJsonAsync(contexto, 400, new { error = ServicioRedaccionSecretos.Sanitizar(ex.Message) });
        }
    }

    private async Task ProcesarInicioEjecucionAsync(HttpListenerContext contexto)
    {
        var cuerpo = await LeerJsonAsync(contexto.Request);
        var scriptId = LeerTexto(cuerpo, "scriptId", string.Empty);
        var configuracion = CargarConfiguracion();
        var validacion = _servicioValidacionScripts.ValidarScriptParaEjecucion(configuracion.RutaScripts, scriptId);

        if (!validacion.EsValido)
        {
            var mensajeValidacion = validacion.Codigo == CodigoValidacionScript.RutaScriptsNoDisponible
                ? MensajeCarpetaScriptsNoDisponible
                : validacion.Mensaje;
            var usuarioDenegado = WindowsIdentity.GetCurrent().Name;
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.validacion", usuarioDenegado, scriptId, mensajeValidacion);
            await EscribirJsonAsync(contexto, ServicioValidacionScripts.ObtenerCodigoHttp(validacion.Codigo), new { error = mensajeValidacion });
            return;
        }

        var script = validacion.Script!;
        var diagnosticoPermisos = ObtenerDiagnosticoPermisos();
        if (PermisosInaccesiblesSinDesbloqueo(diagnosticoPermisos))
        {
            var mensajePermisos = ObtenerMensajePermisosNoDisponibles(diagnosticoPermisos);
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.permisos_no_disponibles", WindowsIdentity.GetCurrent().Name, script.Id, mensajePermisos);
            await EscribirJsonAsync(contexto, 403, new { error = mensajePermisos, avisoConexion = mensajePermisos });
            return;
        }

        var usuario = ObtenerUsuarioActual(diagnosticoPermisos);
        if (!usuario.EstaAutorizado)
        {
            var motivo = string.IsNullOrWhiteSpace(usuario.MotivoBloqueo)
                ? "Acceso denegado. El usuario no esta autorizado."
                : usuario.MotivoBloqueo;
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.usuario", usuario.NombreUsuario, script.Id, motivo);
            await EscribirJsonAsync(contexto, 403, new { error = motivo });
            return;
        }

        if (ScriptBloqueado(script.Id, usuario, diagnosticoPermisos))
        {
            var usuarioDenegado = WindowsIdentity.GetCurrent().Name;
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.permisos", usuarioDenegado, script.Id, "Acceso denegado para este script.");
            await EscribirJsonAsync(contexto, 403, new { error = "Acceso denegado para este script." });
            return;
        }

        var diagnosticoCatalogo = ObtenerDiagnosticoCatalogo();
        var diagnosticoSeguridad = _servicioSeguridadScripts.Diagnosticar(
            script,
            diagnosticoPermisos.Permisos,
            diagnosticoCatalogo.Catalogo,
            diagnosticoCatalogo.Mensaje,
            _modoDesarrolloFirmas);
        if (!diagnosticoSeguridad.Permitido)
        {
            var motivo = string.IsNullOrWhiteSpace(diagnosticoSeguridad.MotivoBloqueo)
                ? "El script no cumple la politica de seguridad."
                : diagnosticoSeguridad.MotivoBloqueo;
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.seguridad", usuario.NombreUsuario, script.Id, motivo);
            await EscribirJsonAsync(contexto, 403, new { error = motivo });
            return;
        }

        if (_gestorEjecuciones.RecuentoActivas >= usuario.MaxScriptsSimultaneos)
        {
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.limite", usuario.NombreUsuario, script.Id, "Limite de ejecuciones simultaneas alcanzado.");
            await EscribirJsonAsync(contexto, 429, new { error = $"Has alcanzado el limite maximo de {usuario.MaxScriptsSimultaneos} scripts simultaneos permitido por tu usuario." });
            return;
        }

        if (diagnosticoSeguridad.ExecutionPolicyBypassPermitido)
        {
            await _servicioAuditoria.RegistrarEventoSeguridadAsync(
                "ejecucion.execution_policy_bypass",
                usuario.NombreUsuario,
                script.Id,
                "permitido",
                "ExecutionPolicy Bypass habilitado por politica admin.");
        }

        if (_modoDesarrolloFirmas)
        {
            await _servicioAuditoria.RegistrarEventoSeguridadAsync(
                "ejecucion.modo_desarrollo_firmas",
                usuario.NombreUsuario,
                script.Id,
                "permitido",
                "Validacion del catalogo omitida por modo desarrollo temporal.");
        }

        var catalogoEjecucion = diagnosticoCatalogo.Catalogo
            ?? new CatalogoScripts(1, DateTimeOffset.UtcNow, _servicioArtefactos.KeyId, []);
        var ejecucionId = _gestorEjecuciones.Iniciar(
            script,
            configuracion.RutaLogs,
            usuario,
            diagnosticoSeguridad.ExecutionPolicyBypassPermitido,
            diagnosticoPermisos.Permisos,
            catalogoEjecucion,
            _modoDesarrolloFirmas);
        await EscribirJsonAsync(contexto, 200, new { id = ejecucionId });
    }

    private async Task ProcesarModoDesarrolloFirmasAsync(HttpListenerContext contexto, string metodo)
    {
        if (!await RequerirAdministradorAsync(contexto))
        {
            return;
        }

        if (metodo == "GET")
        {
            await EscribirJsonAsync(contexto, 200, new { activo = _modoDesarrolloFirmas });
            return;
        }

        if (metodo == "POST")
        {
            var cuerpo = await LeerJsonAsync(contexto.Request);
            var activo = LeerBooleano(cuerpo, "activo", false);
            _modoDesarrolloFirmas = activo;

            await _servicioAuditoria.RegistrarEventoSeguridadAsync(
                "seguridad.modo_desarrollo_firmas",
                WindowsIdentity.GetCurrent().Name,
                null,
                activo ? "activado" : "desactivado",
                "Modo desarrollo de firmas cambiado para la sesion local.");

            await EscribirJsonAsync(contexto, 200, new { activo = _modoDesarrolloFirmas });
            return;
        }

        await EscribirJsonAsync(contexto, 405, new { error = "Metodo no permitido." });
    }

    private async Task ProcesarDiagnosticoEjecucionAsync(HttpListenerContext contexto)
    {
        var scriptId = contexto.Request.QueryString["scriptId"] ?? string.Empty;
            var configuracion = CargarConfiguracion();
        var validacion = _servicioValidacionScripts.ValidarScriptParaEjecucion(configuracion.RutaScripts, scriptId);
        if (!validacion.EsValido)
        {
            await EscribirJsonAsync(contexto, 200, new
            {
                scriptId,
                permitido = false,
                motivoBloqueo = validacion.Mensaje,
                powerShellDisponible = new ServicioFirmaAuthenticode().PowerShellDisponible(),
                executionPolicy = new ServicioFirmaAuthenticode().ObtenerExecutionPolicy(),
                modoDesarrolloFirmas = _modoDesarrolloFirmas
            });
            return;
        }

        var diagnosticoCatalogo = ObtenerDiagnosticoCatalogo();
        await EscribirJsonAsync(
            contexto,
            200,
            _servicioSeguridadScripts.Diagnosticar(
                validacion.Script!,
                ObtenerPermisos(),
                diagnosticoCatalogo.Catalogo,
                diagnosticoCatalogo.Mensaje,
                _modoDesarrolloFirmas));
    }

    private async Task EntregarClienteAsync(HttpListenerContext contexto, string ruta)
    {
        var recurso = ruta == "/" ? "index.html" : Uri.UnescapeDataString(ruta.TrimStart('/'));

        if (recurso.Contains("..", StringComparison.Ordinal))
        {
            contexto.Response.StatusCode = 400;
            return;
        }

        await using var flujo = AbrirRecursoCliente(recurso);

        if (flujo is null)
        {
            contexto.Response.StatusCode = 404;
            return;
        }

        contexto.Response.ContentType = ObtenerTipoContenido(recurso);
        contexto.Response.StatusCode = 200;
        EstablecerCookieSesion(contexto.Response);

        if (recurso.StartsWith("assets/index-", StringComparison.OrdinalIgnoreCase)
            && recurso.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            // Sincroniza la version visible con la version real del ejecutable.
            using var lector = new StreamReader(flujo, Encoding.UTF8, true, 4096, leaveOpen: true);
            var contenido = await lector.ReadToEndAsync();
            var versionado = AplicarVersionVisualCliente(
                contenido,
                Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
            var datos = Encoding.UTF8.GetBytes(versionado);
            contexto.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            contexto.Response.ContentLength64 = datos.Length;
            await contexto.Response.OutputStream.WriteAsync(datos);
            return;
        }

        await flujo.CopyToAsync(contexto.Response.OutputStream);
    }

    internal static string AplicarVersionVisualCliente(string contenido, Version version)
    {
        // Sustituye la etiqueta fija del cliente por la version del ensamblado.
        var compilacion = Math.Max(version.Build, 0);
        var versionVisual = $"{version.Major}.{version.Minor}.{compilacion}";
        return EtiquetaVersionCliente.Replace(contenido, $"children:\"v{versionVisual}\"", 1);
    }

    private static Stream? AbrirRecursoCliente(string recurso)
    {
        // Abre un recurso embebido del cliente web.
        var clave = NormalizarRecursoCliente("ClienteWeb/" + recurso);
        if (!IndiceRecursosCliente.Value.TryGetValue(clave, out var nombreRecurso))
        {
            return null;
        }

        return Assembly.GetExecutingAssembly().GetManifestResourceStream(nombreRecurso);
    }

    private static IReadOnlyDictionary<string, string> CrearIndiceRecursosCliente()
    {
        // Crea el indice de recursos embebidos del cliente web.
        return Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .Where(nombre => nombre.StartsWith("ClienteWeb", StringComparison.OrdinalIgnoreCase))
            .GroupBy(NormalizarRecursoCliente, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grupo => grupo.Key, grupo => grupo.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizarRecursoCliente(string recurso)
    {
        // Normaliza separadores de rutas y recursos.
        return recurso
            .Replace('\\', '/')
            .Replace('.', '/')
            .TrimStart('/');
    }

    private UsuarioCliente ObtenerUsuarioActual()
    {
        // Evita consultar la red durante una sesion de emergencia activa.
        if (ObtenerEmergenciaActiva() is not null)
        {
            var identidad = WindowsIdentity.GetCurrent().Name;
            return new UsuarioCliente(identidad, "admin", 50, true, string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return ObtenerUsuarioActual(ObtenerDiagnosticoPermisos());
    }

    private UsuarioCliente ObtenerUsuarioActual(DiagnosticoPermisos diagnosticoPermisos)
    {
        var identidad = WindowsIdentity.GetCurrent().Name;
        if (ObtenerEmergenciaActiva() is not null)
        {
            return new UsuarioCliente(identidad, "admin", 50, true, string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var permisos = diagnosticoPermisos.Permisos;
        var usuarioCorto = identidad.Contains('\\') ? identidad.Split('\\').Last() : identidad;
        var usuarios = permisos["usuarios"] as JsonArray;
        JsonObject? usuario = null;

        if (usuarios is not null)
        {
            usuario = usuarios.OfType<JsonObject>().FirstOrDefault(item =>
                string.Equals(LeerTexto(item, "nombreUsuario", string.Empty), identidad, StringComparison.OrdinalIgnoreCase)
                || string.Equals(LeerTexto(item, "nombreUsuario", string.Empty), usuarioCorto, StringComparison.OrdinalIgnoreCase));
        }

        if (diagnosticoPermisos.Estado != EstadoPermisos.Disponible)
        {
            return new UsuarioCliente(identidad, "nominal", 1, false, diagnosticoPermisos.Mensaje, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        if (usuario is null)
        {
            return new UsuarioCliente(identidad, "nominal", 1, false, "Usuario no incluido en el archivo de permisos.", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        var rol = usuario is null
            ? LeerTexto(permisos, "rolUsuarioActual", "nominal")
            : LeerTexto(usuario, "rol", "nominal");
        var maximo = usuario is null
            ? LeerEntero(permisos, "maxScriptsSimultaneos", 5)
            : LeerEntero(usuario, "maxScriptsSimultaneos", 5);
        var carpetasPermitidas = usuario is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : LeerCarpetasPermitidas(usuario["carpetasPermitidas"] as JsonArray);

        return new UsuarioCliente(
            usuario is null ? identidad : LeerTexto(usuario, "nombreUsuario", identidad),
            NormalizarRol(rol),
            Math.Clamp(maximo, 1, 50),
            true,
            string.Empty,
            carpetasPermitidas);
    }

    private object CrearUsuarioClienteSesion(
        UsuarioCliente usuario,
        DiagnosticoPermisos diagnosticoPermisos,
        TokenAdmin? tokenAdmin,
        string avisoConexion,
        bool modoOffline)
    {
        // Aplica el desbloqueo maestro solo a la sesion actual.
        var emergencia = ObtenerEmergenciaActiva();
        if (emergencia is not null)
        {
            return new
            {
                usuario.NombreUsuario,
                Rol = "admin",
                usuario.MaxScriptsSimultaneos,
                UsuarioAutorizado = true,
                Bloqueado = false,
                MotivoBloqueo = string.Empty,
                PermisosEncontrados = diagnosticoPermisos.EstaDisponible,
                PermisosAccesibles = diagnosticoPermisos.EstaDisponible,
                PermiteDesbloqueoEmergencia = false,
                TokenMaestroActivo = true,
                EmergenciaVenceUtc = emergencia.VenceUtc,
                EmergenciaMotivo = emergencia.Motivo,
                TokenAdmin = tokenAdmin?.Valor,
                CarpetasPermitidas = Array.Empty<string>(),
                ModoDesarrolloFirmas = _modoDesarrolloFirmas,
                ModoOffline = modoOffline,
                AvisoConexion = avisoConexion
            };
        }

        return new
        {
            usuario.NombreUsuario,
            usuario.Rol,
            usuario.MaxScriptsSimultaneos,
            UsuarioAutorizado = usuario.EstaAutorizado,
            Bloqueado = !usuario.EstaAutorizado,
            MotivoBloqueo = usuario.EstaAutorizado ? string.Empty : usuario.MotivoBloqueo,
            PermisosEncontrados = diagnosticoPermisos.EstaDisponible,
            PermisosAccesibles = diagnosticoPermisos.EstaDisponible,
            PermiteDesbloqueoEmergencia = diagnosticoPermisos.PermiteDesbloqueoEmergencia,
            TokenMaestroActivo = false,
            EmergenciaVenceUtc = (DateTimeOffset?)null,
            EmergenciaMotivo = string.Empty,
            TokenAdmin = tokenAdmin?.Valor,
            CarpetasPermitidas = usuario.CarpetasPermitidas ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ModoDesarrolloFirmas = _modoDesarrolloFirmas,
            ModoOffline = modoOffline,
            AvisoConexion = avisoConexion
        };
    }

    private TokenAdmin? AsegurarTokenAdmin(UsuarioCliente usuario)
    {
        // Genera el token local si el usuario actual es administrador.
        if (usuario.EstaAutorizado && string.Equals(usuario.Rol, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return _servicioTokensAdmin.ObtenerOCrear(WindowsIdentity.GetCurrent().Name);
        }

        return null;
    }

    private async Task<bool> RequerirAdministradorAsync(
        HttpListenerContext contexto,
        DiagnosticoPermisos? diagnosticoPermisos = null)
    {
        var autorizacion = ValidarAdministrador(contexto.Request, diagnosticoPermisos);
        if (autorizacion.Autorizado)
        {
            return true;
        }

        var codigo = autorizacion.Codigo == CodigoAutorizacionAdmin.FaltaBearer ? 401 : 403;
        await EscribirJsonAsync(contexto, codigo, new { error = autorizacion.Mensaje });
        return false;
    }

    private ResultadoAutorizacionAdmin ValidarAdministrador(
        HttpListenerRequest peticion,
        DiagnosticoPermisos? diagnosticoPermisos = null)
    {
        var token = LeerTokenAutorizacion(peticion);
        if (string.IsNullOrWhiteSpace(token))
        {
            return ResultadoAutorizacionAdmin.FaltaBearer();
        }

        var usuario = diagnosticoPermisos is null
            ? ObtenerUsuarioActual()
            : ObtenerUsuarioActual(diagnosticoPermisos);
        if (!usuario.EstaAutorizado)
        {
            return ResultadoAutorizacionAdmin.Denegado("Acceso denegado. El usuario no esta autorizado.");
        }

        if (!string.Equals(usuario.Rol, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return ResultadoAutorizacionAdmin.Denegado("Acceso denegado. Solo administradores.");
        }

        AsegurarTokenAdmin(usuario);
        return _servicioTokensAdmin.Validar(WindowsIdentity.GetCurrent().Name, token)
            ? ResultadoAutorizacionAdmin.Permitido()
            : ResultadoAutorizacionAdmin.Denegado("Token de administrador no valido.");
    }

    private static string? LeerTokenAutorizacion(HttpListenerRequest? peticion)
    {
        // Lee el token Bearer enviado por el cliente web.
        var cabecera = peticion?.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(cabecera))
        {
            return null;
        }

        if (!cabecera.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return cabecera["Bearer ".Length..].Trim();
    }

    private JsonObject ObtenerPermisos()
    {
        return ObtenerDiagnosticoPermisos().Permisos;
    }

    private async Task<DiagnosticoPermisos> ObtenerDiagnosticoAjustesAsync()
    {
        // Limita y agrupa las lecturas remotas usadas al abrir Ajustes.
        if (ObtenerEmergenciaActiva() is not null)
        {
            return CrearDiagnosticoPermisosNoDisponible();
        }

        Task<DiagnosticoPermisos> tarea;
        lock (_bloqueoDiagnosticoAjustes)
        {
            if (_diagnosticoAjustesReciente is not null
                && _diagnosticoAjustesValidoHasta > DateTimeOffset.UtcNow)
            {
                return _diagnosticoAjustesReciente;
            }

            _tareaDiagnosticoAjustes ??= Task.Run(ObtenerDiagnosticoPermisos);
            tarea = _tareaDiagnosticoAjustes;
        }

        var completada = await Task.WhenAny(tarea, Task.Delay(TimeSpan.FromSeconds(2)));
        if (completada != tarea)
        {
            return CrearDiagnosticoPermisosNoDisponible();
        }

        DiagnosticoPermisos resultado;
        try
        {
            resultado = await tarea;
        }
        catch
        {
            resultado = CrearDiagnosticoPermisosNoDisponible();
        }

        lock (_bloqueoDiagnosticoAjustes)
        {
            if (ReferenceEquals(_tareaDiagnosticoAjustes, tarea))
            {
                _tareaDiagnosticoAjustes = null;
                if (resultado.EstaDisponible)
                {
                    _diagnosticoAjustesReciente = resultado;
                    _diagnosticoAjustesValidoHasta = DateTimeOffset.UtcNow.AddSeconds(2);
                }
                else
                {
                    _diagnosticoAjustesReciente = null;
                    _diagnosticoAjustesValidoHasta = DateTimeOffset.MinValue;
                }
            }
        }

        return resultado;
    }

    private DiagnosticoPermisos CrearDiagnosticoPermisosNoDisponible()
    {
        // Devuelve un estado seguro cuando la ruta remota no responde a tiempo.
        var ruta = ObtenerRutaPermisosCompleta(CargarConfiguracion());
        return new DiagnosticoPermisos(
            EstadoPermisos.Inaccesible,
            ruta,
            CrearPermisosPorDefecto(),
            MensajeCarpetaPermisosNoDisponible);
    }

    private void InvalidarDiagnosticoAjustes()
    {
        // Descarta lecturas anteriores despues de publicar o cambiar rutas.
        lock (_bloqueoDiagnosticoAjustes)
        {
            _tareaDiagnosticoAjustes = null;
            _diagnosticoAjustesReciente = null;
            _diagnosticoAjustesValidoHasta = DateTimeOffset.MinValue;
        }
    }

    private DiagnosticoPermisos ObtenerDiagnosticoPermisos()
    {
        var ruta = ObtenerRutaPermisosCompleta(CargarConfiguracion());

        try
        {
            if (!_servicioArtefactos.IntentarCargarTextoProtegido(
                ruta,
                ServicioArtefactosProtegidos.TipoPermisos,
                out var jsonPermisos,
                out var errorProteccion,
                out _))
            {
                if (RutaPermisosInaccesible(ruta))
                {
                    return new DiagnosticoPermisos(
                        EstadoPermisos.Inaccesible,
                        ruta,
                        CrearPermisosPorDefecto(),
                        MensajeCarpetaPermisosNoDisponible);
                }

                if (!ArchivoProtegidoExiste(ruta))
                {
                    return new DiagnosticoPermisos(
                        EstadoPermisos.NoEncontrado,
                        ruta,
                        CrearPermisosPorDefecto(),
                        "No se encontro el archivo de permisos.");
                }

                return new DiagnosticoPermisos(
                    EstadoPermisos.Corrupto,
                    ruta,
                    CrearPermisosPorDefecto(),
                    errorProteccion);
            }

            var permisos = JsonNode.Parse(jsonPermisos) as JsonObject;
            if (permisos is null)
            {
                return new DiagnosticoPermisos(
                    EstadoPermisos.Corrupto,
                    ruta,
                    CrearPermisosPorDefecto(),
                    "El archivo de permisos esta corrupto.");
            }

            return new DiagnosticoPermisos(EstadoPermisos.Disponible, ruta, NormalizarPermisos(permisos), string.Empty);
        }
        catch
        {
            return new DiagnosticoPermisos(
                EstadoPermisos.Corrupto,
                ruta,
                CrearPermisosPorDefecto(),
                "El archivo de permisos no se pudo validar.");
        }
    }

    private static bool ArchivoProtegidoExiste(string ruta)
    {
        // Comprueba el archivo principal y su respaldo sin lanzar errores de red.
        try
        {
            return File.Exists(ruta) || File.Exists(ruta + ".bak");
        }
        catch
        {
            return false;
        }
    }

    private DiagnosticoCatalogo ObtenerDiagnosticoCatalogo()
    {
        var configuracion = CargarConfiguracion();
        var rutaPermisos = ObtenerRutaPermisosCompleta(configuracion);
        var rutaCatalogo = ServicioCatalogoScripts.ObtenerRuta(rutaPermisos);
        if (RutaPermisosInaccesible(rutaCatalogo))
        {
            return new DiagnosticoCatalogo(
                EstadoCatalogo.Inaccesible,
                rutaCatalogo,
                null,
                MensajeCarpetaPermisosNoDisponible);
        }

        if (!_servicioCatalogoScripts.IntentarCargar(
            rutaCatalogo,
            out var catalogo,
            out var error))
        {
            return new DiagnosticoCatalogo(
                ArchivoProtegidoExiste(rutaCatalogo)
                    ? EstadoCatalogo.Corrupto
                    : EstadoCatalogo.NoEncontrado,
                rutaCatalogo,
                null,
                error);
        }

        return new DiagnosticoCatalogo(
            EstadoCatalogo.Disponible,
            rutaCatalogo,
            catalogo,
            string.Empty);
    }

    internal static bool RutaPermisosInaccesible(string ruta)
    {
        // Marca offline rutas cuya carpeta de permisos no responde.
        var carpeta = Path.GetDirectoryName(ruta);
        return string.IsNullOrWhiteSpace(carpeta) || !Directory.Exists(carpeta);
    }

    internal static bool RutaScriptsInaccesible(string ruta)
    {
        // Comprueba la carpeta de scripts sin crearla.
        if (string.IsNullOrWhiteSpace(ruta))
        {
            return true;
        }

        try
        {
            var completa = Path.GetFullPath(Environment.ExpandEnvironmentVariables(ruta.Trim()));
            return !Directory.Exists(completa);
        }
        catch
        {
            return true;
        }
    }

    internal static async Task<bool> RutaScriptsInaccesibleAsync(string ruta)
    {
        // Limita la comprobacion remota para mantener la interfaz disponible.
        var comprobacion = Task.Run(() => RutaScriptsInaccesible(ruta));
        var completada = await Task.WhenAny(comprobacion, Task.Delay(TimeSpan.FromSeconds(2)));
        return completada != comprobacion || await comprobacion;
    }

    internal static string CrearAvisoConexion(bool permisosInaccesibles, bool scriptsInaccesibles)
    {
        // Diferencia las rutas remotas que no responden.
        var avisos = new List<string>();
        if (permisosInaccesibles)
        {
            avisos.Add(MensajeCarpetaPermisosNoDisponible);
        }

        if (scriptsInaccesibles)
        {
            avisos.Add(MensajeCarpetaScriptsNoDisponible);
        }

        return string.Join(" ", avisos);
    }

    private static string ObtenerMensajePermisosNoDisponibles(DiagnosticoPermisos diagnostico)
    {
        // Conserva la causa concreta de la denegacion por permisos.
        return string.IsNullOrWhiteSpace(diagnostico.Mensaje)
            ? "Los permisos no estan disponibles."
            : diagnostico.Mensaje;
    }

    private bool PermisosInaccesiblesSinDesbloqueo(DiagnosticoPermisos diagnosticoPermisos)
    {
        // Bloquea ejecucion si no se puede validar permisos.
        return !diagnosticoPermisos.EstaDisponible && ObtenerEmergenciaActiva() is null;
    }

    private ResultadoGuardarPermisos GuardarPermisos(JsonNode permisos)
    {
        var permisosNormalizados = NormalizarPermisos(permisos);
        var ruta = ObtenerRutaPermisosCompleta(CargarConfiguracion());
        if (RutaPermisosInaccesible(ruta))
        {
            return new ResultadoGuardarPermisos(false, MensajeCarpetaPermisosNoDisponible);
        }

        var carpeta = Path.GetDirectoryName(ruta);
        if (!string.IsNullOrWhiteSpace(carpeta))
        {
            Directory.CreateDirectory(carpeta);
        }

        var json = permisosNormalizados.ToJsonString(OpcionesJson);
        try
        {
            _servicioArtefactos.GuardarTextoProtegido(
                ruta,
                ServicioArtefactosProtegidos.TipoPermisos,
                json);
            return new ResultadoGuardarPermisos(true, string.Empty);
        }
        catch (Exception ex)
        {
            return new ResultadoGuardarPermisos(false, ServicioRedaccionSecretos.Sanitizar(ex.Message));
        }
    }

    private JsonObject NormalizarPermisos(JsonNode permisos)
    {
        // Limpia valores antes de guardar permisos.
        var objeto = permisos as JsonObject ?? new JsonObject();

        return new JsonObject
        {
            ["scriptsAdmin"] = NormalizarScriptsAdmin(objeto["scriptsAdmin"] as JsonArray),
            ["usuarios"] = NormalizarUsuarios(objeto["usuarios"] as JsonArray),
            ["seguridadScripts"] = ServicioSeguridadScripts.NormalizarPolitica(objeto["seguridadScripts"] as JsonObject),
            ["rolUsuarioActual"] = NormalizarRol(LeerTexto(objeto, "rolUsuarioActual", "nominal")),
            ["maxScriptsSimultaneos"] = Math.Clamp(LeerEntero(objeto, "maxScriptsSimultaneos", 5), 1, 50)
        };
    }

    private static JsonArray NormalizarScriptsAdmin(JsonArray? scriptsAdmin)
    {
        var resultado = new JsonArray();
        if (scriptsAdmin is null)
        {
            return resultado;
        }

        foreach (var item in scriptsAdmin)
        {
            var valor = item?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(valor) && EsIdentificadorScriptSeguro(valor))
            {
                resultado.Add(valor.Replace('\\', '/'));
            }
        }

        return resultado;
    }

    private static JsonArray NormalizarUsuarios(JsonArray? usuarios)
    {
        var resultado = new JsonArray();
        if (usuarios is null)
        {
            return resultado;
        }

        foreach (var item in usuarios.OfType<JsonObject>())
        {
            var nombre = LeerTexto(item, "nombreUsuario", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(nombre) || nombre.Length > 256)
            {
                continue;
            }

            resultado.Add(new JsonObject
            {
                ["id"] = LeerTexto(item, "id", Guid.NewGuid().ToString("N")),
                ["nombreUsuario"] = nombre,
                ["rol"] = NormalizarRol(LeerTexto(item, "rol", "nominal")),
                ["maxScriptsSimultaneos"] = Math.Clamp(LeerEntero(item, "maxScriptsSimultaneos", 5), 1, 50),
                ["carpetasPermitidas"] = NormalizarCarpetasPermitidas(item["carpetasPermitidas"] as JsonArray)
            });
        }

        return resultado;
    }

    private static JsonArray NormalizarCarpetasPermitidas(JsonArray? carpetas)
    {
        var resultado = new JsonArray();
        if (carpetas is null)
        {
            return resultado;
        }

        foreach (var item in carpetas)
        {
            var carpeta = (item?.GetValue<string>() ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
            if (EsIdentificadorCarpetaSeguro(carpeta)
                && !resultado.Any(valor => string.Equals(valor?.GetValue<string>(), carpeta, StringComparison.OrdinalIgnoreCase)))
            {
                resultado.Add(carpeta);
            }
        }

        return resultado;
    }

    private IReadOnlyList<ScriptCliente> ObtenerScriptsParaCliente(string carpetaSolicitada = "")
    {
        var carpetaActual = NormalizarCarpetaSolicitada(carpetaSolicitada);
        if (carpetaActual is null)
        {
            return [];
        }

        var diagnosticoPermisos = ObtenerDiagnosticoPermisos();
        var usuario = ObtenerUsuarioActual(diagnosticoPermisos);
        var permisos = diagnosticoPermisos.Permisos;
        var scripts = ObtenerScriptsInternos();
        var diagnosticoCatalogo = ObtenerDiagnosticoCatalogo();
        if (PermisosInaccesiblesSinDesbloqueo(diagnosticoPermisos))
        {
            var mensajePermisos = ObtenerMensajePermisosNoDisponibles(diagnosticoPermisos);
            return scripts
                .Select(script => new ScriptCliente(script.Id, script.Nombre, script.Tipo, true, mensajePermisos))
                .ToList();
        }

        if (!usuario.EstaAutorizado)
        {
            return [];
        }

        var scriptsVisibles = scripts
            .Where(script => !ScriptBloqueado(script.Id, usuario, diagnosticoPermisos))
            .ToList();
        var resultado = new List<ScriptCliente>();

        foreach (var carpeta in ObtenerCarpetasDirectas(scriptsVisibles, carpetaActual))
        {
            resultado.Add(new ScriptCliente(
                $"carpeta:{carpeta}",
                Path.GetFileName(carpeta),
                "carpeta",
                false,
                "Abrir carpeta",
                true,
                carpeta));
        }

        foreach (var script in scriptsVisibles.Where(script => string.Equals(ObtenerCarpetaScript(script.Id), carpetaActual, StringComparison.OrdinalIgnoreCase)))
        {
            var diagnosticoSeguridad = _servicioSeguridadScripts.Diagnosticar(
                script,
                permisos,
                diagnosticoCatalogo.Catalogo,
                diagnosticoCatalogo.Mensaje,
                _modoDesarrolloFirmas);
            resultado.Add(new ScriptCliente(
                script.Id,
                script.Nombre,
                script.Tipo,
                !diagnosticoSeguridad.Permitido,
                diagnosticoSeguridad.MotivoBloqueo,
                false,
                carpetaActual));
        }

        return resultado
            .OrderBy(script => script.EsCarpeta ? 0 : 1)
            .ThenBy(script => script.Nombre, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ObtenerCarpetasDirectas(IReadOnlyList<ScriptInterno> scripts, string carpetaActual)
    {
        var carpetas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts)
        {
            var carpetaScript = ObtenerCarpetaScript(script.Id);
            var rutaHija = ObtenerRutaHija(carpetaActual, carpetaScript);
            if (string.IsNullOrWhiteSpace(rutaHija))
            {
                continue;
            }

            var primerSegmento = rutaHija.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(primerSegmento))
            {
                continue;
            }

            var carpetaDirecta = string.IsNullOrWhiteSpace(carpetaActual)
                ? primerSegmento
                : $"{carpetaActual}/{primerSegmento}";
            carpetas.Add(carpetaDirecta);
        }

        return carpetas
            .OrderBy(carpeta => carpeta, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ObtenerRutaHija(string carpetaActual, string carpetaScript)
    {
        if (string.IsNullOrWhiteSpace(carpetaScript))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(carpetaActual))
        {
            return carpetaScript;
        }

        if (string.Equals(carpetaScript, carpetaActual, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return carpetaScript.StartsWith(carpetaActual + "/", StringComparison.OrdinalIgnoreCase)
            ? carpetaScript[(carpetaActual.Length + 1)..]
            : null;
    }

    private IReadOnlyList<ScriptInterno> ObtenerScriptsInternos()
    {
        var configuracion = CargarConfiguracion();
        return _servicioValidacionScripts.DescubrirScripts(configuracion.RutaScripts);
    }

    private IReadOnlyList<CarpetaScriptCliente> ObtenerSubcarpetasScripts()
    {
        var configuracion = CargarConfiguracion();
        var scriptsPorCarpeta = ObtenerScriptsInternos()
            .Select(script => ObtenerCarpetaScript(script.Id))
            .Where(carpeta => !string.IsNullOrWhiteSpace(carpeta))
            .GroupBy(carpeta => carpeta, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(grupo => grupo.Key, grupo => grupo.Count(), StringComparer.OrdinalIgnoreCase);

        return _servicioValidacionScripts.DescubrirCarpetasScripts(configuracion.RutaScripts)
            .Select(carpeta => new CarpetaScriptCliente(
                carpeta,
                carpeta,
                scriptsPorCarpeta.TryGetValue(carpeta, out var total) ? total : 0))
            .OrderBy(carpeta => carpeta.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool ScriptBloqueado(string scriptId, UsuarioCliente usuario, DiagnosticoPermisos? diagnosticoPermisos = null)
    {
        if (diagnosticoPermisos is not null && PermisosInaccesiblesSinDesbloqueo(diagnosticoPermisos))
        {
            return true;
        }

        if (!usuario.EstaAutorizado)
        {
            return true;
        }

        if (string.Equals(usuario.Rol, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ObtenerEmergenciaActiva() is not null)
        {
            return !string.IsNullOrWhiteSpace(ObtenerCarpetaScript(scriptId));
        }

        var carpeta = ObtenerCarpetaScript(scriptId);
        if (string.IsNullOrWhiteSpace(carpeta))
        {
            return false;
        }

        return !UsuarioTienePermisoCarpeta(usuario, carpeta);
    }

    private static string ObtenerMotivoBloqueoScript(string scriptId, UsuarioCliente usuario)
    {
        if (!usuario.EstaAutorizado)
        {
            return usuario.MotivoBloqueo;
        }

        return string.IsNullOrWhiteSpace(ObtenerCarpetaScript(scriptId))
            ? "Acceso denegado para este script."
            : "El script esta en una subcarpeta y requiere permiso adicional.";
    }

    private static bool UsuarioTienePermisoCarpeta(UsuarioCliente usuario, string carpetaScript)
    {
        var permisos = usuario.CarpetasPermitidas ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return permisos.Any(permiso =>
        {
            var normalizado = permiso.Replace('\\', '/').Trim().Trim('/');
            return string.Equals(carpetaScript, normalizado, StringComparison.OrdinalIgnoreCase)
                || carpetaScript.StartsWith(normalizado + "/", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string ObtenerCarpetaScript(string scriptId)
    {
        var normalizado = scriptId.Replace('\\', '/').Trim('/');
        var indice = normalizado.LastIndexOf('/');
        return indice <= 0 ? string.Empty : normalizado[..indice];
    }

    private static string? NormalizarCarpetaSolicitada(string carpeta)
    {
        // Normaliza la carpeta recibida desde la interfaz.
        var valor = (carpeta ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(valor))
        {
            return string.Empty;
        }

        if (valor.Contains('\0')
            || Path.IsPathRooted(valor)
            || Path.IsPathFullyQualified(valor)
            || ServicioSeguridadScripts.ContieneMetacaracteresPeligrosos(valor))
        {
            return null;
        }

        var segmentos = valor.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segmentos.Length == 0)
        {
            return string.Empty;
        }

        var carpetasExcluidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            "PERMISOS",
            "node_modules",
            "bin",
            "obj"
        };

        return segmentos.Any(segmento => segmento is "." or ".." || carpetasExcluidas.Contains(segmento))
            ? null
            : string.Join('/', segmentos);
    }

    private static JsonObject CrearPermisosPorDefecto()
    {
        return new JsonObject
        {
            ["scriptsAdmin"] = new JsonArray(),
            ["usuarios"] = new JsonArray(),
            ["seguridadScripts"] = new JsonObject
            {
                ["scriptsElevadosPermitidos"] = new JsonArray(),
                ["permitirExecutionPolicyBypass"] = false
            },
            ["rolUsuarioActual"] = "nominal",
            ["maxScriptsSimultaneos"] = 5
        };
    }

    private string ObtenerRutaPermisosCompleta(ConfiguracionLanzador configuracion)
    {
        return _servicioValidacionScripts.ResolverRutaPermisos(configuracion.RutaScripts, configuracion.RutaPermisos);
    }

    private ConfiguracionLanzador CargarConfiguracion()
    {
        // Devuelve configuracion fija solo en pruebas automatizadas.
        return _configuracionFija ?? _servicioConfiguracion.Cargar();
    }

    private SesionEmergencia ActivarEmergencia(
        TokenMaestroPayload payload,
        string usuario,
        EstadoPermisos estadoInicial)
    {
        lock (_bloqueoEmergencia)
        {
            var emergencia = new SesionEmergencia(
                usuario,
                string.Empty,
                DateTimeOffset.UtcNow.AddMinutes(10),
                payload.Id,
                estadoInicial);
            _sesionEmergencia = emergencia;
            return emergencia;
        }
    }

    private bool SesionEmergenciaSinAccesoRemoto()
    {
        // Evita lecturas y escrituras remotas tras una apertura sin acceso a permisos.
        return ObtenerEmergenciaActiva()?.EstadoInicial == EstadoPermisos.Inaccesible;
    }

    private SesionEmergencia? ObtenerEmergenciaActiva()
    {
        lock (_bloqueoEmergencia)
        {
            if (_sesionEmergencia is null)
            {
                return null;
            }

            if (_sesionEmergencia.VenceUtc <= DateTimeOffset.UtcNow)
            {
                _sesionEmergencia = null;
                return null;
            }

            return _sesionEmergencia;
        }
    }

    private bool SesionApiValida(HttpListenerRequest peticion, string ruta)
    {
        if (ruta.Equals("/api/salud", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var cookie = peticion.Cookies[NombreCookieSesion]?.Value;
        var tokenApi = peticion.Headers["X-LanzadorScripts-ApiToken"];
        return CompararTextoSeguro(cookie, _tokenSesion)
            && CompararTextoSeguro(tokenApi, _tokenApiInterno);
    }

    private bool SesionApiValidaPrivada(HttpListenerRequest peticion)
    {
        var cookie = peticion.Cookies[NombreCookieSesion]?.Value;
        var tokenApi = peticion.Headers["X-LanzadorScripts-ApiToken"];
        return CompararTextoSeguro(cookie, _tokenSesion)
            && CompararTextoSeguro(tokenApi, _tokenApiInterno);
    }

    private void EstablecerCookieSesion(HttpListenerResponse respuesta)
    {
        respuesta.Headers["Set-Cookie"] = $"{NombreCookieSesion}={_tokenSesion}; Path=/; HttpOnly; SameSite=Strict";
    }

    private static bool CompararTextoSeguro(string? valor, string esperado)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return false;
        }

        var valorBytes = Encoding.UTF8.GetBytes(valor);
        var esperadoBytes = Encoding.UTF8.GetBytes(esperado);
        return valorBytes.Length == esperadoBytes.Length
            && CryptographicOperations.FixedTimeEquals(valorBytes, esperadoBytes);
    }

    private static bool EsIdentificadorScriptSeguro(string scriptId)
    {
        if (Path.IsPathRooted(scriptId) || Path.IsPathFullyQualified(scriptId))
        {
            return false;
        }

        var segmentos = scriptId.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segmentos.Length == 0 || segmentos.Any(segmento => segmento == "." || segmento == ".."))
        {
            return false;
        }

        var extension = Path.GetExtension(scriptId);
        return extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EsIdentificadorCarpetaSeguro(string carpeta)
    {
        if (string.IsNullOrWhiteSpace(carpeta)
            || Path.IsPathRooted(carpeta)
            || Path.IsPathFullyQualified(carpeta)
            || ServicioSeguridadScripts.ContieneMetacaracteresPeligrosos(carpeta))
        {
            return false;
        }

        var segmentos = carpeta.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segmentos.Length > 0
            && segmentos.All(segmento => segmento != "." && segmento != "..")
            && string.IsNullOrWhiteSpace(Path.GetExtension(carpeta));
    }

    private static HashSet<string> LeerCarpetasPermitidas(JsonArray? carpetas)
    {
        return NormalizarCarpetasPermitidas(carpetas)
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .Where(valor => !string.IsNullOrWhiteSpace(valor))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizarRol(string rol)
    {
        return string.Equals(rol, "admin", StringComparison.OrdinalIgnoreCase)
            ? "admin"
            : "nominal";
    }

    private static Task<JsonNode?> LeerJsonAsync(HttpListenerRequest peticion)
    {
        return LeerJsonAsync(peticion, 1024 * 1024);
    }

    private static async Task<JsonNode?> LeerJsonAsync(
        HttpListenerRequest peticion,
        int longitudMaximaCaracteres)
    {
        if (peticion.ContentLength64 > longitudMaximaCaracteres)
        {
            throw new InvalidOperationException("La solicitud supera el tamano permitido.");
        }

        using var lector = new StreamReader(peticion.InputStream, peticion.ContentEncoding);
        var contenido = new StringBuilder(Math.Min(longitudMaximaCaracteres, 4096));
        var buffer = new char[4096];
        int leidos;
        while ((leidos = await lector.ReadAsync(buffer)) > 0)
        {
            if (contenido.Length + leidos > longitudMaximaCaracteres)
            {
                throw new InvalidOperationException("La solicitud supera el tamano permitido.");
            }

            contenido.Append(buffer, 0, leidos);
        }

        var texto = contenido.ToString();
        return string.IsNullOrWhiteSpace(texto) ? null : JsonNode.Parse(texto);
    }

    private static async Task EscribirJsonAsync(HttpListenerContext contexto, int codigo, object valor)
    {
        var json = JsonSerializer.Serialize(valor, OpcionesJson);
        var bytes = Encoding.UTF8.GetBytes(json);
        contexto.Response.StatusCode = codigo;
        contexto.Response.ContentType = "application/json; charset=utf-8";
        contexto.Response.ContentLength64 = bytes.Length;
        await contexto.Response.OutputStream.WriteAsync(bytes);
    }

    private static string LeerTexto(JsonNode? nodo, string propiedad, string valorDefecto)
    {
        return nodo?[propiedad]?.GetValue<string>() ?? valorDefecto;
    }

    private static int LeerEntero(JsonNode? nodo, string propiedad, int valorDefecto)
    {
        return nodo?[propiedad]?.GetValue<int>() ?? valorDefecto;
    }

    private static bool LeerBooleano(JsonNode? nodo, string propiedad, bool valorDefecto)
    {
        return nodo?[propiedad]?.GetValue<bool>() ?? valorDefecto;
    }

    private static IReadOnlyList<string> LeerArrayTexto(JsonArray? valores)
    {
        return valores is null
            ? []
            : valores
                .Select(valor => valor?.GetValue<string>() ?? string.Empty)
                .Where(valor => !string.IsNullOrWhiteSpace(valor))
                .ToList();
    }

    private static string ObtenerTipoContenido(string recurso)
    {
        return Path.GetExtension(recurso).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
    }

    private static int ReservarPuertoLibre()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var puerto = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return puerto;
    }

    private enum EstadoPermisos
    {
        Disponible,
        NoEncontrado,
        Inaccesible,
        Corrupto
    }

    private enum EstadoCatalogo
    {
        Disponible,
        NoEncontrado,
        Inaccesible,
        Corrupto
    }

    private enum CodigoAutorizacionAdmin
    {
        Permitido,
        FaltaBearer,
        Denegado
    }

    private sealed record DiagnosticoPermisos(EstadoPermisos Estado, string Ruta, JsonObject Permisos, string Mensaje)
    {
        public bool EstaDisponible => Estado == EstadoPermisos.Disponible;

        public bool PermiteDesbloqueoEmergencia => Estado != EstadoPermisos.Disponible;

        public bool ModoOffline => Estado == EstadoPermisos.Inaccesible;
    }

    private sealed record DiagnosticoCatalogo(
        EstadoCatalogo Estado,
        string Ruta,
        CatalogoScripts? Catalogo,
        string Mensaje)
    {
        public bool EstaDisponible => Estado == EstadoCatalogo.Disponible;
    }

    private sealed record SesionEmergencia(
        string Usuario,
        string Motivo,
        DateTimeOffset VenceUtc,
        string TokenId,
        EstadoPermisos EstadoInicial);

    private sealed record ResultadoAutorizacionAdmin(CodigoAutorizacionAdmin Codigo, string Mensaje)
    {
        public bool Autorizado => Codigo == CodigoAutorizacionAdmin.Permitido;

        public static ResultadoAutorizacionAdmin Permitido()
        {
            return new ResultadoAutorizacionAdmin(CodigoAutorizacionAdmin.Permitido, string.Empty);
        }

        public static ResultadoAutorizacionAdmin FaltaBearer()
        {
            return new ResultadoAutorizacionAdmin(CodigoAutorizacionAdmin.FaltaBearer, "Falta Authorization: Bearer.");
        }

        public static ResultadoAutorizacionAdmin Denegado(string mensaje)
        {
            return new ResultadoAutorizacionAdmin(CodigoAutorizacionAdmin.Denegado, mensaje);
        }
    }

    private sealed record ResultadoGuardarPermisos(bool PermisosGuardados, string AvisoConexion);

    private sealed record ScriptCliente(
        string Id,
        string Nombre,
        string Tipo,
        bool EstaBloqueado,
        string MotivoBloqueo,
        bool EsCarpeta = false,
        string Carpeta = "");

    private sealed record CarpetaScriptCliente(string Id, string Nombre, int TotalScripts);
}
