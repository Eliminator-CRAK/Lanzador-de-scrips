// (Autor: Alex Roman)
// Descripcion: Servidor local que entrega el cliente web y la API de ejecucion.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanzadorScripts.Modelos;

namespace LanzadorScripts.Servicios;

public sealed class ServidorLocalWeb : IDisposable
{
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
    private readonly GestorEjecucionesWeb _gestorEjecuciones = new();
    private volatile bool _tokenMaestroSesionActiva;

    private ServidorLocalWeb(int puerto)
    {
        UrlBase = new Uri($"http://127.0.0.1:{puerto}/");
        _escuchador.Prefixes.Add(UrlBase.ToString());
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
                await ProcesarApiAsync(contexto, ruta);
                return;
            }

            await EntregarClienteAsync(contexto, ruta);
        }
        catch (Exception ex)
        {
            if (contexto.Response.OutputStream.CanWrite)
            {
                await EscribirJsonAsync(contexto, 500, new { error = ex.Message });
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
            var usuario = ObtenerUsuarioActual();
            AsegurarTokenAdmin(usuario);
            await EscribirJsonAsync(contexto, 200, CrearUsuarioClienteSesion(usuario));
            return;
        }

        if (metodo == "POST" && ruta.Equals("/api/token-maestro/desbloquear", StringComparison.OrdinalIgnoreCase))
        {
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
            configuracion.RutaPermisos = LeerTexto(cuerpo, "rutaPermisos", configuracion.RutaPermisos);
            configuracion.RutaScripts = LeerTexto(cuerpo, "carpetaScripts", configuracion.RutaScripts);
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
            var cuerpo = await LeerJsonAsync(contexto.Request);
            var scriptId = LeerTexto(cuerpo, "scriptId", string.Empty);
            var script = ObtenerScriptPorId(scriptId);

            if (script is null)
            {
                await EscribirJsonAsync(contexto, 404, new { error = "Script no encontrado." });
                return;
            }

            if (ScriptBloqueado(script.Id))
            {
                await EscribirJsonAsync(contexto, 403, new { error = "Acceso denegado para este script." });
                return;
            }

            var usuario = ObtenerUsuarioActual();
            if (_gestorEjecuciones.RecuentoActivas >= usuario.MaxScriptsSimultaneos)
            {
                await EscribirJsonAsync(contexto, 429, new { error = $"Has alcanzado el limite maximo de {usuario.MaxScriptsSimultaneos} scripts simultaneos permitido por tu usuario." });
                return;
            }

            var configuracion = _servicioConfiguracion.Cargar();
            var ejecucionId = _gestorEjecuciones.Iniciar(script, configuracion.RutaLogs);
            await EscribirJsonAsync(contexto, 200, new { id = ejecucionId });
            return;
        }

        if (metodo == "GET" && partes.Length == 4 && partes[1] == "ejecuciones" && partes[3] == "eventos")
        {
            if (!Guid.TryParse(partes[2], out var ejecucionId))
            {
                await EscribirJsonAsync(contexto, 400, new { error = "Identificador de ejecucion no valido." });
                return;
            }

            await _gestorEjecuciones.EnviarEventosAsync(ejecucionId, contexto.Response, _cancelacion.Token);
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
        var permisos = ObtenerPermisos();
        var identidad = WindowsIdentity.GetCurrent().Name;
        var usuarioCorto = identidad.Contains('\\') ? identidad.Split('\\').Last() : identidad;
        var usuarios = permisos["usuarios"] as JsonArray;
        JsonObject? usuario = null;

        if (usuarios is not null)
        {
            usuario = usuarios.OfType<JsonObject>().FirstOrDefault(item =>
                string.Equals(LeerTexto(item, "nombreUsuario", string.Empty), identidad, StringComparison.OrdinalIgnoreCase)
                || string.Equals(LeerTexto(item, "nombreUsuario", string.Empty), usuarioCorto, StringComparison.OrdinalIgnoreCase));
        }

        var rol = usuario is null
            ? LeerTexto(permisos, "rolUsuarioActual", "nominal")
            : LeerTexto(usuario, "rol", "nominal");
        var maximo = usuario is null
            ? LeerEntero(permisos, "maxScriptsSimultaneos", 5)
            : LeerEntero(usuario, "maxScriptsSimultaneos", 5);

        return new UsuarioCliente(
            usuario is null ? identidad : LeerTexto(usuario, "nombreUsuario", identidad),
            rol,
            Math.Clamp(maximo, 1, 50));
    }

    private object CrearUsuarioClienteSesion(UsuarioCliente usuario)
    {
        // Aplica el desbloqueo maestro solo a la sesion actual.
        if (!_tokenMaestroSesionActiva)
        {
            return usuario;
        }

        return new
        {
            usuario.NombreUsuario,
            Rol = "admin",
            usuario.MaxScriptsSimultaneos,
            TokenMaestroActivo = true
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
        var ruta = ObtenerRutaPermisosCompleta(_servicioConfiguracion.Cargar());

        if (!File.Exists(ruta))
        {
            return CrearPermisosPorDefecto();
        }

        try
        {
            var texto = File.ReadAllText(ruta, Encoding.UTF8);
            if (_servicioCifradoAplicacion.IntentarDescifrarTexto("permisos", texto, out var permisosDescifrados))
            {
                texto = permisosDescifrados;
            }

            return JsonNode.Parse(texto) as JsonObject ?? CrearPermisosPorDefecto();
        }
        catch
        {
            return CrearPermisosPorDefecto();
        }
    }

    private void GuardarPermisos(JsonNode permisos)
    {
        var ruta = ObtenerRutaPermisosCompleta(_servicioConfiguracion.Cargar());
        var carpeta = Path.GetDirectoryName(ruta);
        if (!string.IsNullOrWhiteSpace(carpeta))
        {
            Directory.CreateDirectory(carpeta);
        }

        var json = permisos.ToJsonString(OpcionesJson);
        var cifrado = _servicioCifradoAplicacion.CifrarTexto("permisos", json);
        File.WriteAllText(ruta, cifrado, Encoding.UTF8);
        ServicioInicioAutomatico.Aplicar(LeerBooleano(permisos, "inicioAutomaticoWindows", false));
    }

    private IReadOnlyList<ScriptCliente> ObtenerScriptsParaCliente()
    {
        return ObtenerScriptsInternos()
            .Select(script => new ScriptCliente(
                script.Id,
                script.Nombre,
                script.Tipo,
                ScriptBloqueado(script.Id)))
            .ToList();
    }

    private ScriptInterno? ObtenerScriptPorId(string scriptId)
    {
        return ObtenerScriptsInternos().FirstOrDefault(script =>
            string.Equals(script.Id, scriptId, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<ScriptInterno> ObtenerScriptsInternos()
    {
        var configuracion = _servicioConfiguracion.Cargar();
        var raiz = configuracion.RutaScripts;

        if (string.IsNullOrWhiteSpace(raiz) || !Directory.Exists(raiz))
        {
            return [];
        }

        var resultado = new List<ScriptInterno>();
        foreach (var ruta in EnumerarScripts(raiz))
        {
            var relativo = Path.GetRelativePath(raiz, ruta).Replace('\\', '/');
            var extension = Path.GetExtension(ruta).ToLowerInvariant();
            var tipo = extension == ".ps1" ? "powershell" : "batch";
            resultado.Add(new ScriptInterno(relativo, Path.GetFileName(ruta), tipo, ruta));
        }

        return resultado
            .OrderBy(script => script.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool ScriptBloqueado(string scriptId)
    {
        if (UsuarioEsAdministrador())
        {
            return false;
        }

        var permisos = ObtenerPermisos();
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

    private static IEnumerable<string> EnumerarScripts(string raiz)
    {
        var carpetas = new Stack<string>();
        carpetas.Push(raiz);

        while (carpetas.Count > 0)
        {
            var actual = carpetas.Pop();

            IEnumerable<string> archivos;
            try
            {
                archivos = Directory.EnumerateFiles(actual)
                    .Where(EsScriptPermitido);
            }
            catch
            {
                continue;
            }

            foreach (var archivo in archivos)
            {
                yield return archivo;
            }

            IEnumerable<string> hijas;
            try
            {
                hijas = Directory.EnumerateDirectories(actual);
            }
            catch
            {
                continue;
            }

            foreach (var carpeta in hijas)
            {
                var nombre = Path.GetFileName(carpeta);
                if (!nombre.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    && !nombre.Equals("PERMISOS", StringComparison.OrdinalIgnoreCase)
                    && !nombre.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    carpetas.Push(carpeta);
                }
            }
        }
    }

    private static bool EsScriptPermitido(string ruta)
    {
        var extension = Path.GetExtension(ruta);
        return extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase);
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
        return Path.IsPathRooted(configuracion.RutaPermisos)
            ? configuracion.RutaPermisos
            : Path.Combine(configuracion.RutaScripts, configuracion.RutaPermisos);
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

    private sealed record UsuarioCliente(string NombreUsuario, string Rol, int MaxScriptsSimultaneos);

    private sealed record ScriptCliente(string Id, string Nombre, string Tipo, bool EstaBloqueado);

    private sealed record ScriptInterno(string Id, string Nombre, string Tipo, string RutaCompleta);

    private sealed class GestorEjecucionesWeb : IDisposable
    {
        private readonly ConcurrentDictionary<Guid, EjecucionWeb> _ejecuciones = new();

        public int RecuentoActivas => _ejecuciones.Values.Count(ejecucion => !ejecucion.Finalizada);

        public Guid Iniciar(ScriptInterno script, string rutaLogs)
        {
            var ejecucion = new EjecucionWeb(script, rutaLogs);
            _ejecuciones[ejecucion.Id] = ejecucion;
            ejecucion.AgregarEvento("info", $"### Script-{script.Nombre}");
            ejecucion.AgregarEvento("exito", $"> Iniciando {script.Nombre}... (#B5CEA8)", "#B5CEA8");
            ejecucion.AgregarEvento("info", "> Conectando a servidor...");
            _ = Task.Run(() => EjecutarAsync(ejecucion));
            return ejecucion.Id;
        }

        public void Cancelar(Guid id)
        {
            if (!_ejecuciones.TryGetValue(id, out var ejecucion) || ejecucion.Finalizada)
            {
                return;
            }

            ejecucion.Cancelada = true;
            try
            {
                if (ejecucion.Proceso is not null && !ejecucion.Proceso.HasExited)
                {
                    ejecucion.Proceso.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                ejecucion.AgregarEvento("error", $"> Error al cancelar: {ex.Message}", "#F44747");
            }
        }

        public async Task EnviarEntradaAsync(Guid id, string texto)
        {
            if (!_ejecuciones.TryGetValue(id, out var ejecucion) || ejecucion.Proceso is null)
            {
                return;
            }

            try
            {
                await ejecucion.Proceso.StandardInput.WriteLineAsync(texto);
                await ejecucion.Proceso.StandardInput.FlushAsync();
            }
            catch (Exception ex)
            {
                ejecucion.AgregarEvento("error", $"> Error al enviar entrada: {ex.Message}", "#F44747");
            }
        }

        public async Task EnviarEventosAsync(Guid id, HttpListenerResponse respuesta, CancellationToken cancelacion)
        {
            if (!_ejecuciones.TryGetValue(id, out var ejecucion))
            {
                respuesta.StatusCode = 404;
                return;
            }

            respuesta.StatusCode = 200;
            respuesta.ContentType = "text/event-stream; charset=utf-8";
            respuesta.Headers["Cache-Control"] = "no-cache";

            var indice = 0;
            while (!cancelacion.IsCancellationRequested)
            {
                var eventos = ejecucion.ObtenerEventosDesde(indice);
                foreach (var evento in eventos)
                {
                    indice++;
                    var json = JsonSerializer.Serialize(evento, OpcionesJson);
                    var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
                    await respuesta.OutputStream.WriteAsync(bytes, cancelacion);
                    await respuesta.OutputStream.FlushAsync(cancelacion);
                }

                if (ejecucion.Finalizada && indice >= ejecucion.TotalEventos)
                {
                    break;
                }

                await ejecucion.EsperarEventoAsync(cancelacion);
            }
        }

        public void Dispose()
        {
            foreach (var ejecucion in _ejecuciones.Values)
            {
                try
                {
                    if (ejecucion.Proceso is not null && !ejecucion.Proceso.HasExited)
                    {
                        ejecucion.Proceso.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                ejecucion.Dispose();
            }
        }

        private static async Task EjecutarAsync(EjecucionWeb ejecucion)
        {
            try
            {
                Directory.CreateDirectory(ejecucion.RutaLogs);
                var rutaLog = ConstruirRutaLog(ejecucion);
                await using var log = new StreamWriter(rutaLog, append: false, Encoding.UTF8)
                {
                    AutoFlush = true
                };

                await log.WriteLineAsync($"Script: {ejecucion.Script.Nombre}");
                await log.WriteLineAsync($"Inicio: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                await log.WriteLineAsync();

                using var proceso = CrearProceso(ejecucion.Script);
                ejecucion.Proceso = proceso;
                proceso.Start();

                var salida = LeerSalidaAsync(proceso.StandardOutput, ejecucion, log);
                var error = LeerErrorAsync(proceso.StandardError, ejecucion, log);
                await proceso.WaitForExitAsync();
                await Task.WhenAll(salida, error);

                if (ejecucion.Cancelada)
                {
                    ejecucion.AgregarEvento("error", "> Ejecucion cancelada por el usuario.", "#F44747", finalizado: true);
                    await log.WriteLineAsync("Cancelada por el usuario.");
                    return;
                }

                if (proceso.ExitCode == 0)
                {
                    ejecucion.AgregarEvento("exito", $"> Finalizada correctamente. Codigo de salida: {proceso.ExitCode}", "#B5CEA8", finalizado: true);
                }
                else
                {
                    ejecucion.AgregarEvento("error", $"> Error. Codigo de salida: {proceso.ExitCode}", "#F44747", finalizado: true);
                }

                await log.WriteLineAsync();
                await log.WriteLineAsync($"Fin: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                await log.WriteLineAsync($"Codigo de salida: {proceso.ExitCode}");
            }
            catch (Exception ex)
            {
                ejecucion.AgregarEvento("error", $"> Error: {ex.Message}", "#F44747", finalizado: true);
            }
            finally
            {
                ejecucion.MarcarFinalizada();
            }
        }

        private static async Task LeerSalidaAsync(StreamReader lector, EjecucionWeb ejecucion, StreamWriter log)
        {
            while (await lector.ReadLineAsync() is { } linea)
            {
                var texto = OcultarRutas(ejecucion.Script, linea);
                ejecucion.AgregarEvento("info", texto);
                await log.WriteLineAsync(texto);
            }
        }

        private static async Task LeerErrorAsync(StreamReader lector, EjecucionWeb ejecucion, StreamWriter log)
        {
            while (await lector.ReadLineAsync() is { } linea)
            {
                var texto = OcultarRutas(ejecucion.Script, linea);
                ejecucion.AgregarEvento("error", texto, "#F44747");
                await log.WriteLineAsync(texto);
            }
        }

        private static Process CrearProceso(ScriptInterno script)
        {
            var inicio = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default,
                WorkingDirectory = Path.GetDirectoryName(script.RutaCompleta) ?? Environment.CurrentDirectory
            };

            if (script.Tipo == "powershell")
            {
                inicio.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                inicio.ArgumentList.Add("-NoLogo");
                inicio.ArgumentList.Add("-NoProfile");
                inicio.ArgumentList.Add("-ExecutionPolicy");
                inicio.ArgumentList.Add("Bypass");
                inicio.ArgumentList.Add("-Command");
                inicio.ArgumentList.Add($"$ErrorActionPreference='Continue'; & '{script.RutaCompleta.Replace("'", "''")}' *>&1");
            }
            else
            {
                inicio.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                inicio.ArgumentList.Add("/d");
                inicio.ArgumentList.Add("/c");
                inicio.ArgumentList.Add(script.RutaCompleta);
            }

            return new Process { StartInfo = inicio, EnableRaisingEvents = true };
        }

        private static string ConstruirRutaLog(EjecucionWeb ejecucion)
        {
            var carpetaDia = Path.Combine(ejecucion.RutaLogs, DateTime.Now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(carpetaDia);
            var nombreSeguro = string.Concat(ejecucion.Script.Nombre.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(carpetaDia, $"{DateTime.Now:HHmmss}_{nombreSeguro}_{ejecucion.Id:N}.log");
        }

        private static string OcultarRutas(ScriptInterno script, string texto)
        {
            var carpeta = Path.GetDirectoryName(script.RutaCompleta);
            if (!string.IsNullOrWhiteSpace(carpeta))
            {
                texto = texto.Replace(carpeta, "[origen protegido]", StringComparison.OrdinalIgnoreCase);
            }

            return texto.Replace(script.RutaCompleta, "[script protegido]", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class EjecucionWeb(ScriptInterno script, string rutaLogs) : IDisposable
    {
        private readonly List<EventoCliente> _eventos = [];
        private readonly SemaphoreSlim _senal = new(0);
        private readonly object _bloqueo = new();

        public Guid Id { get; } = Guid.NewGuid();

        public ScriptInterno Script { get; } = script;

        public string RutaLogs { get; } = rutaLogs;

        public Process? Proceso { get; set; }

        public bool Cancelada { get; set; }

        public bool Finalizada { get; private set; }

        public int TotalEventos
        {
            get
            {
                lock (_bloqueo)
                {
                    return _eventos.Count;
                }
            }
        }

        public void AgregarEvento(string tipo, string mensaje, string? color = null, bool finalizado = false)
        {
            lock (_bloqueo)
            {
                _eventos.Add(new EventoCliente(tipo, mensaje, color, finalizado));
            }

            _senal.Release();
        }

        public IReadOnlyList<EventoCliente> ObtenerEventosDesde(int indice)
        {
            lock (_bloqueo)
            {
                return _eventos.Skip(indice).ToList();
            }
        }

        public async Task EsperarEventoAsync(CancellationToken cancelacion)
        {
            await _senal.WaitAsync(cancelacion);
        }

        public void MarcarFinalizada()
        {
            Finalizada = true;
            _senal.Release();
        }

        public void Dispose()
        {
            Proceso?.Dispose();
            _senal.Dispose();
        }
    }

    private sealed record EventoCliente(string Tipo, string Mensaje, string? Color = null, bool Finalizado = false);
}
