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
using LanzadorScripts.Modelos;

namespace LanzadorScripts.Servicios;

public sealed class ServidorLocalWeb : IDisposable
{
    private const string NombreCookieSesion = "LanzadorScriptsSesion";
    private const string MensajeServidorNoDisponible = "No se puede conectar al servidor.";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> IndiceRecursosCliente = new(CrearIndiceRecursosCliente);

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
    private readonly ServicioCifradoAplicacion _servicioCifradoAplicacion;
    private readonly ServicioPaquetesConfiguracion _servicioPaquetesConfiguracion = new();
    private readonly ServicioValidacionScripts _servicioValidacionScripts = new();
    private readonly ServicioSeguridadScripts _servicioSeguridadScripts = new();
    private readonly ServicioAuditoria _servicioAuditoria = new();
    private readonly ConfiguracionLanzador? _configuracionFija;
    private readonly GestorEjecucionesWeb _gestorEjecuciones;
    private readonly object _bloqueoEmergencia = new();
    private readonly string _tokenSesion = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _tokenApiInterno = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private SesionEmergencia? _sesionEmergencia;
    private volatile bool _modoDesarrolloFirmas;

    private ServidorLocalWeb(int puerto)
        : this(puerto, new ServicioCifradoAplicacion(), new ServicioTokenMaestro())
    {
    }

    private ServidorLocalWeb(int puerto, ServicioCifradoAplicacion servicioCifradoAplicacion)
        : this(puerto, servicioCifradoAplicacion, new ServicioTokenMaestro())
    {
    }

    private ServidorLocalWeb(int puerto, ServicioCifradoAplicacion servicioCifradoAplicacion, ServicioTokenMaestro servicioTokenMaestro)
    {
        UrlBase = new Uri($"http://127.0.0.1:{puerto}/");
        _escuchador.Prefixes.Add(UrlBase.ToString());
        _servicioCifradoAplicacion = servicioCifradoAplicacion;
        _servicioTokenMaestro = servicioTokenMaestro;
        _gestorEjecuciones = new GestorEjecucionesWeb(_servicioAuditoria, _servicioSeguridadScripts);
    }

    private ServidorLocalWeb(int puerto, ConfiguracionLanzador configuracion)
        : this(puerto, new ServicioCifradoAplicacion())
    {
        _configuracionFija = configuracion;
    }

    private ServidorLocalWeb(int puerto, ConfiguracionLanzador configuracion, ServicioCifradoAplicacion servicioCifradoAplicacion)
        : this(puerto, servicioCifradoAplicacion)
    {
        _configuracionFija = configuracion;
    }

    private ServidorLocalWeb(int puerto, ConfiguracionLanzador configuracion, ServicioCifradoAplicacion servicioCifradoAplicacion, ServicioTokenMaestro servicioTokenMaestro)
        : this(puerto, servicioCifradoAplicacion, servicioTokenMaestro)
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

    internal static ServidorLocalWeb IniciarParaPruebas(ConfiguracionLanzador configuracion, ServicioCifradoAplicacion servicioCifradoAplicacion)
    {
        // Inicia el servidor con servicios aislados de pruebas.
        var servidor = new ServidorLocalWeb(ReservarPuertoLibre(), configuracion, servicioCifradoAplicacion);
        servidor._escuchador.Start();
        _ = servidor.EscucharAsync();
        return servidor;
    }

    internal static ServidorLocalWeb IniciarParaPruebas(ConfiguracionLanzador configuracion, ServicioCifradoAplicacion servicioCifradoAplicacion, ServicioTokenMaestro servicioTokenMaestro)
    {
        // Inicia el servidor con servicios aislados de pruebas.
        var servidor = new ServidorLocalWeb(ReservarPuertoLibre(), configuracion, servicioCifradoAplicacion, servicioTokenMaestro);
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
                await _servicioAuditoria.RegistrarErrorInternoAsync("api.error", ex.GetType().Name);
                await EscribirJsonAsync(contexto, 503, new
                {
                    error = MensajeServidorNoDisponible,
                    avisoConexion = MensajeServidorNoDisponible
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
                    runtimeFijo = Directory.Exists(RutasAplicacion.RutaRuntimeWebView2Fijo)
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
            var diagnosticoPermisos = ObtenerDiagnosticoPermisos();
            var usuario = ObtenerUsuarioActual(diagnosticoPermisos);
            var tokenAdmin = AsegurarTokenAdmin(usuario);
            await EscribirJsonAsync(contexto, 200, CrearUsuarioClienteSesion(usuario, diagnosticoPermisos, tokenAdmin));
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/token-maestro/desbloquear", StringComparison.OrdinalIgnoreCase))
        {
            var diagnosticoPermisos = ObtenerDiagnosticoPermisos();
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

            var emergencia = ActivarEmergencia(payload!, usuarioActual);

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
            await EscribirJsonAsync(contexto, 200, ObtenerScriptsParaCliente());
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
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            await EscribirJsonAsync(contexto, 200, new { permisos = ObtenerPermisos(), mensaje = "Datos de ajustes cargados exitosamente." });
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/ajustes", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            var cuerpo = await LeerJsonAsync(contexto.Request);
            var resultado = GuardarPermisos(cuerpo ?? new JsonObject());
            await EscribirJsonAsync(contexto, 200, new
            {
                exito = true,
                mensaje = resultado.PermisosGuardados
                    ? "Ajustes guardados exitosamente."
                    : "La configuracion se guardo, pero no se pudo conectar al servidor de permisos.",
                avisoConexion = resultado.AvisoConexion
            });
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/configuracion-app", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
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
            await EscribirJsonAsync(contexto, 200, new { exito = true, mensaje = "Configuracion de la aplicacion guardada exitosamente." });
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/configuracion-paquete/exportar", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            var paquete = _servicioPaquetesConfiguracion.Exportar(CargarConfiguracion(), ObtenerPermisos());
            await EscribirJsonAsync(contexto, 200, paquete);
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/subcarpetas-scripts", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            await EscribirJsonAsync(contexto, 200, ObtenerSubcarpetasScripts());
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/hashes-batch-detectados", StringComparison.OrdinalIgnoreCase))
        {
            if (!await RequerirAdministradorAsync(contexto))
            {
                return;
            }

            await EscribirJsonAsync(contexto, 200, ObtenerHashesBatchDetectados());
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

    private async Task ProcesarInicioEjecucionAsync(HttpListenerContext contexto)
    {
        var cuerpo = await LeerJsonAsync(contexto.Request);
        var scriptId = LeerTexto(cuerpo, "scriptId", string.Empty);
        var configuracion = CargarConfiguracion();
        var validacion = _servicioValidacionScripts.ValidarScriptParaEjecucion(configuracion.RutaScripts, scriptId);

        if (!validacion.EsValido)
        {
            var usuarioDenegado = WindowsIdentity.GetCurrent().Name;
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.validacion", usuarioDenegado, scriptId, validacion.Mensaje);
            await EscribirJsonAsync(contexto, ServicioValidacionScripts.ObtenerCodigoHttp(validacion.Codigo), new { error = validacion.Mensaje });
            return;
        }

        var script = validacion.Script!;
        var diagnosticoPermisos = ObtenerDiagnosticoPermisos();
        if (PermisosInaccesiblesSinDesbloqueo(diagnosticoPermisos))
        {
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.permisos_offline", WindowsIdentity.GetCurrent().Name, script.Id, MensajeServidorNoDisponible);
            await EscribirJsonAsync(contexto, 403, new { error = MensajeServidorNoDisponible, avisoConexion = MensajeServidorNoDisponible });
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

        var diagnosticoSeguridad = _servicioSeguridadScripts.Diagnosticar(script, diagnosticoPermisos.Permisos, _modoDesarrolloFirmas);
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
                "Validacion de firma/hash omitida por modo desarrollo temporal.");
        }

        var ejecucionId = _gestorEjecuciones.Iniciar(
            script,
            configuracion.RutaLogs,
            usuario,
            diagnosticoSeguridad.ExecutionPolicyBypassPermitido,
            diagnosticoPermisos.Permisos,
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

        await EscribirJsonAsync(contexto, 200, _servicioSeguridadScripts.Diagnosticar(validacion.Script!, ObtenerPermisos(), _modoDesarrolloFirmas));
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
        await flujo.CopyToAsync(contexto.Response.OutputStream);
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
        return ObtenerUsuarioActual(ObtenerDiagnosticoPermisos());
    }

    private UsuarioCliente ObtenerUsuarioActual(DiagnosticoPermisos diagnosticoPermisos)
    {
        var identidad = WindowsIdentity.GetCurrent().Name;
        if (ObtenerEmergenciaActiva() is not null)
        {
            return new UsuarioCliente(identidad, "emergencia", 1, true, string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
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

    private object CrearUsuarioClienteSesion(UsuarioCliente usuario, DiagnosticoPermisos diagnosticoPermisos, TokenAdmin? tokenAdmin)
    {
        // Aplica el desbloqueo maestro solo a la sesion actual.
        var emergencia = ObtenerEmergenciaActiva();
        if (emergencia is not null)
        {
            return new
            {
                usuario.NombreUsuario,
                Rol = "emergencia",
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
                ModoOffline = diagnosticoPermisos.ModoOffline,
                AvisoConexion = diagnosticoPermisos.ModoOffline ? diagnosticoPermisos.Mensaje : string.Empty
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
            ModoOffline = diagnosticoPermisos.ModoOffline,
            AvisoConexion = diagnosticoPermisos.ModoOffline ? diagnosticoPermisos.Mensaje : string.Empty
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

    private async Task<bool> RequerirAdministradorAsync(HttpListenerContext contexto)
    {
        var autorizacion = ValidarAdministrador(contexto.Request);
        if (autorizacion.Autorizado)
        {
            return true;
        }

        var codigo = autorizacion.Codigo == CodigoAutorizacionAdmin.FaltaBearer ? 401 : 403;
        await EscribirJsonAsync(contexto, codigo, new { error = autorizacion.Mensaje });
        return false;
    }

    private ResultadoAutorizacionAdmin ValidarAdministrador(HttpListenerRequest peticion)
    {
        var token = LeerTokenAutorizacion(peticion);
        if (string.IsNullOrWhiteSpace(token))
        {
            return ResultadoAutorizacionAdmin.FaltaBearer();
        }

        var usuario = ObtenerUsuarioActual();
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

    private DiagnosticoPermisos ObtenerDiagnosticoPermisos()
    {
        var ruta = ObtenerRutaPermisosCompleta(CargarConfiguracion());

        if (!File.Exists(ruta))
        {
            if (RutaPermisosInaccesible(ruta))
            {
                return new DiagnosticoPermisos(
                    EstadoPermisos.Inaccesible,
                    ruta,
                    CrearPermisosPorDefecto(),
                    MensajeServidorNoDisponible);
            }

            return new DiagnosticoPermisos(
                EstadoPermisos.NoEncontrado,
                ruta,
                CrearPermisosPorDefecto(),
                "No se encontro el archivo de permisos.");
        }

        try
        {
            var texto = File.ReadAllText(ruta, Encoding.UTF8);
            if (!_servicioCifradoAplicacion.IntentarDescifrarTexto("permisos", texto, out var permisosDescifrados))
            {
                return new DiagnosticoPermisos(
                    EstadoPermisos.Corrupto,
                    ruta,
                    CrearPermisosPorDefecto(),
                    "El archivo de permisos no tiene una firma corporativa valida.");
            }

            texto = permisosDescifrados;

            var permisos = JsonNode.Parse(texto) as JsonObject;
            if (permisos is null)
            {
                return new DiagnosticoPermisos(
                    EstadoPermisos.Corrupto,
                    ruta,
                    CrearPermisosPorDefecto(),
                    "El archivo de permisos esta corrupto.");
            }

            return new DiagnosticoPermisos(EstadoPermisos.Disponible, ruta, permisos, string.Empty);
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

    internal static bool RutaPermisosInaccesible(string ruta)
    {
        // Marca offline rutas cuya carpeta de permisos no responde.
        var carpeta = Path.GetDirectoryName(ruta);
        return string.IsNullOrWhiteSpace(carpeta) || !Directory.Exists(carpeta);
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
            ServicioInicioAutomatico.Aplicar(LeerBooleano(permisosNormalizados, "inicioAutomaticoWindows", false));
            return new ResultadoGuardarPermisos(false, MensajeServidorNoDisponible);
        }

        var carpeta = Path.GetDirectoryName(ruta);
        if (!string.IsNullOrWhiteSpace(carpeta))
        {
            Directory.CreateDirectory(carpeta);
        }

        var json = permisosNormalizados.ToJsonString(OpcionesJson);
        try
        {
            var firmado = _servicioCifradoAplicacion.CifrarTexto("permisos", json);
            File.WriteAllText(ruta, firmado, Encoding.UTF8);
            ServicioInicioAutomatico.Aplicar(LeerBooleano(permisosNormalizados, "inicioAutomaticoWindows", false));
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
            ["inicioAutomaticoWindows"] = LeerBooleano(objeto, "inicioAutomaticoWindows", false),
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

    private IReadOnlyList<ScriptCliente> ObtenerScriptsParaCliente()
    {
        var diagnosticoPermisos = ObtenerDiagnosticoPermisos();
        var usuario = ObtenerUsuarioActual(diagnosticoPermisos);
        var permisos = diagnosticoPermisos.Permisos;
        var scripts = ObtenerScriptsInternos();
        if (PermisosInaccesiblesSinDesbloqueo(diagnosticoPermisos))
        {
            return scripts
                .Select(script => new ScriptCliente(script.Id, script.Nombre, script.Tipo, true, MensajeServidorNoDisponible))
                .ToList();
        }

        if (!_modoDesarrolloFirmas)
        {
            _servicioSeguridadScripts.PrecargarFirmas(scripts);
        }

        return scripts
            .Select(script =>
            {
                var bloqueadoPorPermisos = ScriptBloqueado(script.Id, usuario, diagnosticoPermisos);
                var diagnosticoSeguridad = _servicioSeguridadScripts.Diagnosticar(script, permisos, _modoDesarrolloFirmas);
                var bloqueado = bloqueadoPorPermisos || !diagnosticoSeguridad.Permitido;
                var motivo = bloqueadoPorPermisos
                    ? ObtenerMotivoBloqueoScript(script.Id, usuario)
                    : diagnosticoSeguridad.MotivoBloqueo;

                return new ScriptCliente(
                    script.Id,
                    script.Nombre,
                    script.Tipo,
                    bloqueado,
                    motivo);
            })
            .ToList();
    }

    private IReadOnlyList<ScriptInterno> ObtenerScriptsInternos()
    {
        var configuracion = CargarConfiguracion();
        return _servicioValidacionScripts.DescubrirScripts(configuracion.RutaScripts);
    }

    private IReadOnlyList<CarpetaScriptCliente> ObtenerSubcarpetasScripts()
    {
        return ObtenerScriptsInternos()
            .Select(script => ObtenerCarpetaScript(script.Id))
            .Where(carpeta => !string.IsNullOrWhiteSpace(carpeta))
            .GroupBy(carpeta => carpeta, StringComparer.OrdinalIgnoreCase)
            .Select(grupo => new CarpetaScriptCliente(grupo.Key, grupo.Key, grupo.Count()))
            .OrderBy(carpeta => carpeta.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<HashBatchCliente> ObtenerHashesBatchDetectados()
    {
        return ObtenerScriptsInternos()
            .Where(script => script.Tipo == "batch")
            .Select(script => new HashBatchCliente(script.Id, ServicioSeguridadScripts.CalcularSha256(script.RutaCompleta)))
            .OrderBy(script => script.ScriptId, StringComparer.OrdinalIgnoreCase)
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

    private static JsonObject CrearPermisosPorDefecto()
    {
        return new JsonObject
        {
            ["inicioAutomaticoWindows"] = false,
            ["scriptsAdmin"] = new JsonArray(),
            ["usuarios"] = new JsonArray(),
            ["seguridadScripts"] = new JsonObject
            {
                ["certificadosPowerShellPermitidos"] = new JsonArray(),
                ["hashesBatchPermitidos"] = new JsonArray(),
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

    private SesionEmergencia ActivarEmergencia(TokenMaestroPayload payload, string usuario)
    {
        lock (_bloqueoEmergencia)
        {
            var emergencia = new SesionEmergencia(usuario, string.Empty, DateTimeOffset.UtcNow.AddMinutes(10), payload.Id);
            _sesionEmergencia = emergencia;
            return emergencia;
        }
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

    private static async Task<JsonNode?> LeerJsonAsync(HttpListenerRequest peticion)
    {
        using var lector = new StreamReader(peticion.InputStream, peticion.ContentEncoding);
        var texto = await lector.ReadToEndAsync();
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

    private sealed record SesionEmergencia(string Usuario, string Motivo, DateTimeOffset VenceUtc, string TokenId);

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

    private sealed record ScriptCliente(string Id, string Nombre, string Tipo, bool EstaBloqueado, string MotivoBloqueo);

    private sealed record CarpetaScriptCliente(string Id, string Nombre, int TotalScripts);

    private sealed record HashBatchCliente(string ScriptId, string Sha256);
}
