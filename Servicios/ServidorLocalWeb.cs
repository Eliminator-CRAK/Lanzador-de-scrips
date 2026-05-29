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
    private readonly ServicioTokenMaestro _servicioTokenMaestro = new();
    private readonly ServicioCifradoAplicacion _servicioCifradoAplicacion = new();
    private readonly ServicioPaquetesConfiguracion _servicioPaquetesConfiguracion = new();
    private readonly ServicioValidacionScripts _servicioValidacionScripts = new();
    private readonly ServicioAuditoria _servicioAuditoria = new();
    private readonly GestorEjecucionesWeb _gestorEjecuciones;
    private readonly string _tokenSesion = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private volatile bool _tokenMaestroSesionActiva;

    private ServidorLocalWeb(int puerto)
    {
        UrlBase = new Uri($"http://127.0.0.1:{puerto}/");
        _escuchador.Prefixes.Add(UrlBase.ToString());
        _gestorEjecuciones = new GestorEjecucionesWeb(_servicioAuditoria);
    }

    public Uri UrlBase { get; }

    public static ServidorLocalWeb Iniciar()
    {
        var servidor = new ServidorLocalWeb(ReservarPuertoLibre());
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
                await EscribirJsonAsync(contexto, 500, new { error = "Error interno de la aplicacion." });
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
            await EscribirJsonAsync(contexto, 200, new { estado = "ok" });
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/usuario", StringComparison.OrdinalIgnoreCase))
        {
            var diagnosticoPermisos = ObtenerDiagnosticoPermisos();
            var usuario = ObtenerUsuarioActual(diagnosticoPermisos);
            AsegurarTokenAdmin(usuario);
            await EscribirJsonAsync(contexto, 200, CrearUsuarioClienteSesion(usuario, diagnosticoPermisos));
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/token-maestro/desbloquear", StringComparison.OrdinalIgnoreCase))
        {
            if (ObtenerDiagnosticoPermisos().EstaDisponible)
            {
                await EscribirJsonAsync(contexto, 403, new { error = "El token maestro solo esta disponible si no se puede leer el archivo de permisos." });
                return;
            }

            var cuerpo = await LeerJsonAsync(contexto.Request);
            var token = LeerTexto(cuerpo, "token", string.Empty);
            if (!_servicioTokenMaestro.Validar(token, out var payload))
            {
                await EscribirJsonAsync(contexto, 403, new { error = "Token maestro no valido." });
                return;
            }

            _tokenMaestroSesionActiva = true;
            await EscribirJsonAsync(contexto, 200, new
            {
                exito = true,
                mensaje = "Acceso maestro desbloqueado para esta sesion.",
                emisor = payload
            });
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/scripts", StringComparison.OrdinalIgnoreCase))
        {
            await EscribirJsonAsync(contexto, 200, ObtenerScriptsParaCliente());
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/ajustes", StringComparison.OrdinalIgnoreCase))
        {
            if (!UsuarioEsAdministrador(contexto.Request))
            {
                await EscribirJsonAsync(contexto, 403, new { error = "Acceso denegado. Solo administradores." });
                return;
            }

            await EscribirJsonAsync(contexto, 200, new { permisos = ObtenerPermisos(), mensaje = "Datos de ajustes cargados exitosamente." });
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/ajustes", StringComparison.OrdinalIgnoreCase))
        {
            if (!UsuarioEsAdministrador(contexto.Request))
            {
                await EscribirJsonAsync(contexto, 403, new { error = "Acceso denegado. Solo administradores." });
                return;
            }

            var cuerpo = await LeerJsonAsync(contexto.Request);
            GuardarPermisos(cuerpo ?? new JsonObject());
            await EscribirJsonAsync(contexto, 200, new { exito = true, mensaje = "Ajustes guardados exitosamente." });
            return;
        }

        if (metodo == "GET" && ruta.Equals("/api/configuracion-app", StringComparison.OrdinalIgnoreCase))
        {
            if (!UsuarioEsAdministrador(contexto.Request))
            {
                await EscribirJsonAsync(contexto, 403, new { error = "Acceso denegado. Solo administradores." });
                return;
            }

            var configuracion = _servicioConfiguracion.Cargar();
            await EscribirJsonAsync(contexto, 200, new
            {
                rutaPermisos = configuracion.RutaPermisos,
                carpetaScripts = configuracion.RutaScripts
            });
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/configuracion-app", StringComparison.OrdinalIgnoreCase))
        {
            if (!UsuarioEsAdministrador(contexto.Request))
            {
                await EscribirJsonAsync(contexto, 403, new { error = "Acceso denegado. Solo administradores." });
                return;
            }

            var cuerpo = await LeerJsonAsync(contexto.Request);
            var configuracion = _servicioConfiguracion.Cargar();
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
            if (!UsuarioEsAdministrador(contexto.Request))
            {
                await EscribirJsonAsync(contexto, 403, new { error = "Acceso denegado. Solo administradores." });
                return;
            }

            var paquete = _servicioPaquetesConfiguracion.Exportar(_servicioConfiguracion.Cargar());
            await EscribirJsonAsync(contexto, 200, paquete);
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
        var configuracion = _servicioConfiguracion.Cargar();
        var validacion = _servicioValidacionScripts.ValidarScriptParaEjecucion(configuracion.RutaScripts, scriptId);

        if (!validacion.EsValido)
        {
            var usuarioDenegado = WindowsIdentity.GetCurrent().Name;
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.validacion", usuarioDenegado, scriptId, validacion.Mensaje);
            await EscribirJsonAsync(contexto, ServicioValidacionScripts.ObtenerCodigoHttp(validacion.Codigo), new { error = validacion.Mensaje });
            return;
        }

        var script = validacion.Script!;
        var usuario = ObtenerUsuarioActual();
        if (!usuario.EstaAutorizado)
        {
            var motivo = string.IsNullOrWhiteSpace(usuario.MotivoBloqueo)
                ? "Acceso denegado. El usuario no esta autorizado."
                : usuario.MotivoBloqueo;
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.usuario", usuario.NombreUsuario, script.Id, motivo);
            await EscribirJsonAsync(contexto, 403, new { error = motivo });
            return;
        }

        if (ScriptBloqueado(script.Id, usuario))
        {
            var usuarioDenegado = WindowsIdentity.GetCurrent().Name;
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.permisos", usuarioDenegado, script.Id, "Acceso denegado para este script.");
            await EscribirJsonAsync(contexto, 403, new { error = "Acceso denegado para este script." });
            return;
        }

        if (_gestorEjecuciones.RecuentoActivas >= usuario.MaxScriptsSimultaneos)
        {
            await _servicioAuditoria.RegistrarDenegacionAsync("ejecucion.limite", usuario.NombreUsuario, script.Id, "Limite de ejecuciones simultaneas alcanzado.");
            await EscribirJsonAsync(contexto, 429, new { error = $"Has alcanzado el limite maximo de {usuario.MaxScriptsSimultaneos} scripts simultaneos permitido por tu usuario." });
            return;
        }

        var ejecucionId = _gestorEjecuciones.Iniciar(script, configuracion.RutaLogs, usuario);
        await EscribirJsonAsync(contexto, 200, new { id = ejecucionId });
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
        if (_tokenMaestroSesionActiva)
        {
            return new UsuarioCliente(identidad, "admin", 50, true);
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

        if (diagnosticoPermisos.Estado == EstadoPermisos.Inaccesible)
        {
            return new UsuarioCliente(identidad, "nominal", 1, false, diagnosticoPermisos.Mensaje);
        }

        if (diagnosticoPermisos.Estado == EstadoPermisos.Disponible && usuario is null)
        {
            return new UsuarioCliente(identidad, "nominal", 1, false, "Usuario no incluido en el archivo de permisos.");
        }

        var rol = usuario is null
            ? LeerTexto(permisos, "rolUsuarioActual", "nominal")
            : LeerTexto(usuario, "rol", "nominal");
        var maximo = usuario is null
            ? LeerEntero(permisos, "maxScriptsSimultaneos", 5)
            : LeerEntero(usuario, "maxScriptsSimultaneos", 5);

        return new UsuarioCliente(
            usuario is null ? identidad : LeerTexto(usuario, "nombreUsuario", identidad),
            NormalizarRol(rol),
            Math.Clamp(maximo, 1, 50),
            true);
    }

    private object CrearUsuarioClienteSesion(UsuarioCliente usuario, DiagnosticoPermisos diagnosticoPermisos)
    {
        // Aplica el desbloqueo maestro solo a la sesion actual.
        if (_tokenMaestroSesionActiva)
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
            ModoOffline = diagnosticoPermisos.ModoOffline,
            AvisoConexion = diagnosticoPermisos.ModoOffline ? diagnosticoPermisos.Mensaje : string.Empty
        };
    }

    private void AsegurarTokenAdmin(UsuarioCliente usuario)
    {
        // Genera el token local si el usuario actual es administrador.
        if (string.Equals(usuario.Rol, "admin", StringComparison.OrdinalIgnoreCase))
        {
            _servicioTokensAdmin.ObtenerOCrear(WindowsIdentity.GetCurrent().Name);
        }
    }

    private bool UsuarioEsAdministrador(HttpListenerRequest? peticion = null)
    {
        if (_tokenMaestroSesionActiva)
        {
            return true;
        }

        var usuario = ObtenerUsuarioActual();
        if (!usuario.EstaAutorizado)
        {
            return false;
        }

        if (!string.Equals(usuario.Rol, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        AsegurarTokenAdmin(usuario);
        var token = LeerTokenAutorizacion(peticion);
        return string.IsNullOrWhiteSpace(token)
            || _servicioTokensAdmin.Validar(WindowsIdentity.GetCurrent().Name, token);
    }

    private static string? LeerTokenAutorizacion(HttpListenerRequest? peticion)
    {
        // Lee el token Bearer enviado por el cliente web.
        var cabecera = peticion?.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(cabecera))
        {
            return null;
        }

        return cabecera.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? cabecera["Bearer ".Length..].Trim()
            : cabecera.Trim();
    }

    private JsonObject ObtenerPermisos()
    {
        return ObtenerDiagnosticoPermisos().Permisos;
    }

    private DiagnosticoPermisos ObtenerDiagnosticoPermisos()
    {
        var ruta = ObtenerRutaPermisosCompleta(_servicioConfiguracion.Cargar());

        if (!File.Exists(ruta))
        {
            if (RutaPermisosInaccesible(ruta))
            {
                return new DiagnosticoPermisos(
                    EstadoPermisos.Inaccesible,
                    ruta,
                    CrearPermisosPorDefecto(),
                    "No se puede establecer conexion con el servidor de permisos.");
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
            if (_servicioCifradoAplicacion.IntentarDescifrarTexto("permisos", texto, out var permisosDescifrados))
            {
                texto = permisosDescifrados;
            }

            var permisos = JsonNode.Parse(texto) as JsonObject;
            if (permisos is null)
            {
                return new DiagnosticoPermisos(
                    EstadoPermisos.Inaccesible,
                    ruta,
                    CrearPermisosPorDefecto(),
                    "No se pudo interpretar el archivo de permisos.");
            }

            return new DiagnosticoPermisos(EstadoPermisos.Disponible, ruta, permisos, string.Empty);
        }
        catch
        {
            return new DiagnosticoPermisos(
                EstadoPermisos.Inaccesible,
                ruta,
                CrearPermisosPorDefecto(),
                "No se pudo leer el archivo de permisos.");
        }
    }

    private static bool RutaPermisosInaccesible(string ruta)
    {
        // Solo marca offline rutas UNC cuyo recurso compartido no responde.
        if (!ruta.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var raiz = Path.GetPathRoot(ruta);
        return string.IsNullOrWhiteSpace(raiz) || !Directory.Exists(raiz);
    }

    private void GuardarPermisos(JsonNode permisos)
    {
        var permisosNormalizados = NormalizarPermisos(permisos);
        var ruta = ObtenerRutaPermisosCompleta(_servicioConfiguracion.Cargar());
        var carpeta = Path.GetDirectoryName(ruta);
        if (!string.IsNullOrWhiteSpace(carpeta))
        {
            Directory.CreateDirectory(carpeta);
        }

        var json = permisosNormalizados.ToJsonString(OpcionesJson);
        var cifrado = _servicioCifradoAplicacion.CifrarTexto("permisos", json);
        File.WriteAllText(ruta, cifrado, Encoding.UTF8);
        ServicioInicioAutomatico.Aplicar(LeerBooleano(permisosNormalizados, "inicioAutomaticoWindows", false));
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
                ["maxScriptsSimultaneos"] = Math.Clamp(LeerEntero(item, "maxScriptsSimultaneos", 5), 1, 50)
            });
        }

        return resultado;
    }

    private IReadOnlyList<ScriptCliente> ObtenerScriptsParaCliente()
    {
        var diagnosticoPermisos = ObtenerDiagnosticoPermisos();
        var usuario = ObtenerUsuarioActual(diagnosticoPermisos);
        return ObtenerScriptsInternos()
            .Select(script => new ScriptCliente(
                script.Id,
                script.Nombre,
                script.Tipo,
                ScriptBloqueado(script.Id, usuario, diagnosticoPermisos)))
            .ToList();
    }

    private IReadOnlyList<ScriptInterno> ObtenerScriptsInternos()
    {
        var configuracion = _servicioConfiguracion.Cargar();
        return _servicioValidacionScripts.DescubrirScripts(configuracion.RutaScripts);
    }

    private bool ScriptBloqueado(string scriptId, UsuarioCliente usuario, DiagnosticoPermisos? diagnosticoPermisos = null)
    {
        if (!usuario.EstaAutorizado)
        {
            return true;
        }

        if (_tokenMaestroSesionActiva || string.Equals(usuario.Rol, "admin", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var permisos = (diagnosticoPermisos ?? ObtenerDiagnosticoPermisos()).Permisos;
        var scriptsAdmin = permisos["scriptsAdmin"] as JsonArray;
        if (scriptsAdmin is null)
        {
            return false;
        }

        return scriptsAdmin
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .Any(item => CoincideScript(item, scriptId));
    }

    private static bool CoincideScript(string permiso, string scriptId)
    {
        var permisoNormalizado = permiso.Replace('\\', '/').TrimStart('.', '/');
        var scriptNormalizado = scriptId.Replace('\\', '/').TrimStart('.', '/');
        return string.Equals(permisoNormalizado, scriptNormalizado, StringComparison.OrdinalIgnoreCase)
            || permisoNormalizado.EndsWith("/" + scriptNormalizado, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject CrearPermisosPorDefecto()
    {
        return new JsonObject
        {
            ["inicioAutomaticoWindows"] = false,
            ["scriptsAdmin"] = new JsonArray(),
            ["usuarios"] = new JsonArray(),
            ["rolUsuarioActual"] = "nominal",
            ["maxScriptsSimultaneos"] = 5
        };
    }

    private string ObtenerRutaPermisosCompleta(ConfiguracionLanzador configuracion)
    {
        return _servicioValidacionScripts.ResolverRutaPermisos(configuracion.RutaScripts, configuracion.RutaPermisos);
    }

    private bool SesionApiValida(HttpListenerRequest peticion, string ruta)
    {
        if (ruta.Equals("/api/salud", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var cookie = peticion.Cookies[NombreCookieSesion]?.Value;
        return CompararTextoSeguro(cookie, _tokenSesion);
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
        Inaccesible
    }

    private sealed record DiagnosticoPermisos(EstadoPermisos Estado, string Ruta, JsonObject Permisos, string Mensaje)
    {
        public bool EstaDisponible => Estado == EstadoPermisos.Disponible;

        public bool PermiteDesbloqueoEmergencia => Estado != EstadoPermisos.Disponible;

        public bool ModoOffline => Estado == EstadoPermisos.Inaccesible;
    }

    private sealed record ScriptCliente(string Id, string Nombre, string Tipo, bool EstaBloqueado);
}
