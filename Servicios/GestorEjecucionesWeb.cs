// (Autor: Alex Roman)
// Descripcion: Gestiona ejecuciones, respuestas y cancelaciones del cliente nativo.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using LanzadorScripts.Monitorizacion;

namespace LanzadorScripts.Servicios;

public sealed class GestorEjecucionesWeb : IDisposable
{
    private const int MaximoCaracteresEntrada = 8192;
    private const int MaximoEventosPorEjecucion = 5000;
    private const int MaximoCaracteresSalida = 2_000_000;
    private static readonly TimeSpan TiempoMaximoEjecucion = TimeSpan.FromHours(2);
    private static readonly TimeSpan TtlEjecucionesFinalizadas = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions OpcionesJsonEventos = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConcurrentDictionary<Guid, EjecucionWeb> _ejecuciones = new();
    private readonly IServicioAuditoria _servicioAuditoria;
    private readonly ServicioSeguridadScripts _servicioSeguridadScripts;
    private readonly ServicioBrokerElevado _servicioBrokerElevado = new();
    private readonly string _rutaStaging;
    private int _desechado;

    public GestorEjecucionesWeb(
        IServicioAuditoria servicioAuditoria,
        ServicioSeguridadScripts servicioSeguridadScripts,
        string? rutaStaging = null)
    {
        _servicioAuditoria = servicioAuditoria;
        _servicioSeguridadScripts = servicioSeguridadScripts;
        _rutaStaging = rutaStaging ?? RutasAplicacion.RutaStaging;
    }

    public int RecuentoActivas
    {
        get
        {
            PurgarFinalizadasAntiguas();
            return _ejecuciones.Values.Count(ejecucion => !ejecucion.Finalizada);
        }
    }

    public IReadOnlyList<EjecucionActivaResumen> ObtenerEjecucionesActivas()
    {
        // Devuelve una instantanea estable para confirmar el cierre.
        PurgarFinalizadasAntiguas();
        return _ejecuciones.Values
            .Where(ejecucion => !ejecucion.Finalizada)
            .Select(ejecucion => new EjecucionActivaResumen(
                ejecucion.Id,
                ejecucion.Script.Nombre))
            .OrderBy(ejecucion => ejecucion.NombreScript, StringComparer.OrdinalIgnoreCase)
            .ThenBy(ejecucion => ejecucion.Id)
            .ToArray();
    }

    public async Task<ResultadoInicioEjecucion> IniciarAsync(
        ScriptInterno script,
        string rutaLogs,
        UsuarioCliente usuario,
        bool permitirExecutionPolicyBypass,
        JsonObject permisos,
        CatalogoScripts catalogo,
        bool modoDesarrolloFirmas,
        string sha256)
    {
        PurgarFinalizadasAntiguas();
        var permisosCongelados = JsonNode.Parse(permisos.ToJsonString()) as JsonObject ?? new JsonObject();
        var catalogoCongelado = catalogo with
        {
            Scripts = catalogo.Scripts.ToArray()
        };
        var ejecucion = new EjecucionWeb(
            script,
            rutaLogs,
            usuario,
            permitirExecutionPolicyBypass,
            permisosCongelados,
            catalogoCongelado,
            modoDesarrolloFirmas,
            sha256);
        var auditoria = await _servicioAuditoria.RegistrarInicioEjecucionAsync(
            ejecucion.Id,
            script,
            usuario,
            sha256);
        if (!auditoria.Exito)
        {
            ejecucion.Dispose();
            return ResultadoInicioEjecucion.Error(auditoria.Mensaje);
        }

        _ejecuciones[ejecucion.Id] = ejecucion;
        ejecucion.AgregarEvento("exito", $"> Iniciando {script.Nombre}...", "#B5CEA8");
        ejecucion.TareaEjecucion = Task.Run(() => EjecutarAsync(ejecucion));
        return ResultadoInicioEjecucion.Correcto(ejecucion.Id);
    }

    public async Task CancelarAsync(Guid id)
    {
        if (!_ejecuciones.TryGetValue(id, out var ejecucion) || ejecucion.Finalizada)
        {
            return;
        }

        // La cancelacion se conserva aunque el proceso todavia no haya arrancado.
        ejecucion.Cancelacion.Cancel();
        await _servicioAuditoria.RegistrarEventoSeguridadAsync(
            "ejecucion.cancelacion",
            ejecucion.Usuario.NombreUsuario,
            ejecucion.Script.Id,
            "solicitado",
            "Cancelacion solicitada por el usuario.");
    }

    public Task EnviarEntradaAsync(Guid id, string texto)
    {
        if (!_ejecuciones.TryGetValue(id, out var ejecucion) || ejecucion.Finalizada || ejecucion.Cancelada)
            throw new InvalidOperationException("La ejecucion no admite mas respuestas.");
        if (texto.Length > MaximoCaracteresEntrada || texto.Contains('\r') || texto.Contains('\n') || texto.Contains('\0'))
            throw new ArgumentException("La respuesta debe ser una linea de hasta 8192 caracteres.");
        // Conserva respuestas recibidas mientras el proceso o el broker termina de arrancar.
        if (!ejecucion.Entradas.Writer.TryWrite(texto))
            throw new InvalidOperationException("La cola de respuestas esta llena o la ejecucion ha terminado.");
        return Task.CompletedTask;
    }

    public async Task EnviarEventosAsync(Guid id, HttpListenerRequest peticion, HttpListenerResponse respuesta, CancellationToken cancelacion)
    {
        if (!_ejecuciones.TryGetValue(id, out var ejecucion))
        {
            respuesta.StatusCode = 404;
            return;
        }

        respuesta.StatusCode = 200;
        respuesta.ContentType = "text/event-stream; charset=utf-8";
        respuesta.Headers["Cache-Control"] = "no-cache";
        respuesta.SendChunked = true;
        respuesta.KeepAlive = true;

        var indice = LeerUltimoIndiceEvento(peticion);
        try
        {
            while (!cancelacion.IsCancellationRequested)
            {
                var eventos = ejecucion.ObtenerEventosDesde(indice);
                foreach (var evento in eventos)
                {
                    var idEvento = indice + 1;
                    var json = JsonSerializer.Serialize(evento, OpcionesJsonEventos);
                    var bytes = Encoding.UTF8.GetBytes($"id: {idEvento}\ndata: {json}\n\n");
                    await respuesta.OutputStream.WriteAsync(bytes, cancelacion);
                    await respuesta.OutputStream.FlushAsync(cancelacion);
                    indice++;
                }

                if (ejecucion.Finalizada && indice >= ejecucion.TotalEventos)
                {
                    break;
                }

                if (!await ejecucion.EsperarEventoAsync(TimeSpan.FromSeconds(10), cancelacion))
                {
                    var pulso = Encoding.UTF8.GetBytes(": keepalive\n\n");
                    await respuesta.OutputStream.WriteAsync(pulso, cancelacion);
                    await respuesta.OutputStream.FlushAsync(cancelacion);
                }
            }
        }
        catch when (cancelacion.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async IAsyncEnumerable<EventoCliente> ObservarEventosAsync(Guid id, [EnumeratorCancellation] CancellationToken cancelacion = default)
    {
        // Entrega las mismas salidas a WPF directamente, sin SSE ni servidor HTTP.
        if (!_ejecuciones.TryGetValue(id, out var ejecucion)) throw new ArgumentException("Ejecucion no encontrada.");
        var indice = 0;
        var revision = 0;
        while (!cancelacion.IsCancellationRequested)
        {
            var controles = ejecucion.ObtenerControles();
            if (controles.Revision != revision)
            {
                revision = controles.Revision;
                foreach (var evento in controles.Eventos) yield return evento;
            }
            foreach (var evento in ejecucion.ObtenerEventosDesde(indice))
            {
                indice++;
                yield return evento;
            }
            if (ejecucion.Finalizada && indice >= ejecucion.TotalEventos)
            {
                // La finalizacion se notifica incluso cuando la salida fue truncada.
                yield return new EventoCliente("fin", string.Empty, null, true);
                yield break;
            }
            await ejecucion.EsperarEventoAsync(TimeSpan.FromSeconds(1), cancelacion);
        }
    }

    public void Dispose()
    {
        Cerrar(TimeSpan.FromSeconds(30));
    }

    internal void Cerrar(TimeSpan tiempoMaximo)
    {
        if (Interlocked.Exchange(ref _desechado, 1) != 0)
        {
            return;
        }

        var espera = tiempoMaximo < TimeSpan.Zero ? TimeSpan.Zero : tiempoMaximo;
        foreach (var ejecucion in _ejecuciones.Values)
        {
            try
            {
                if (!ejecucion.Finalizada) ejecucion.Cancelacion.Cancel();
            }
            catch
            {
            }
        }

        var tareas = _ejecuciones.Values
            .Select(ejecucion => ejecucion.TareaEjecucion)
            .Where(tarea => tarea is not null)
            .Cast<Task>()
            .ToArray();
        try
        {
            _ = Task.WhenAll(tareas).Wait(espera);
        }
        catch
        {
        }

        foreach (var ejecucion in _ejecuciones.Values)
        {
            if (ejecucion.TareaEjecucion?.IsCompleted != false)
            {
                ejecucion.Dispose();
            }
        }
    }

    private async Task EjecutarAsync(EjecucionWeb ejecucion)
    {
        using var medicion = MonitorizacionAplicacion.Actual.Medir("script.ejecucion");
        var resultadoAuditoria = "error";
        int? codigoSalida = null;
        string? detalleAuditoria = null;

        try
        {
            ejecucion.Cancelacion.Token.ThrowIfCancellationRequested();
            var diagnostico = _servicioSeguridadScripts.Diagnosticar(
                ejecucion.Script,
                ejecucion.Permisos,
                ejecucion.Catalogo,
                string.Empty,
                ejecucion.ModoDesarrolloFirmas);
            if (!diagnostico.Permitido)
            {
                detalleAuditoria = diagnostico.MotivoBloqueo;
                ejecucion.AgregarEvento("error", $"> Ejecucion bloqueada antes de iniciar: {detalleAuditoria}", "#F44747", finalizado: true);
                return;
            }

            ejecucion.Cancelacion.Token.ThrowIfCancellationRequested();
            using var scriptPreparado = CrearCopiaTemporalValidada(ejecucion);
            ejecucion.RutaScriptPreparado = scriptPreparado.Script.RutaCompleta;

            var diagnosticoPreparado = _servicioSeguridadScripts.Diagnosticar(
                scriptPreparado.Script,
                ejecucion.Permisos,
                ejecucion.Catalogo,
                string.Empty,
                ejecucion.ModoDesarrolloFirmas);
            if (!diagnosticoPreparado.Permitido)
            {
                detalleAuditoria = diagnosticoPreparado.MotivoBloqueo;
                ejecucion.AgregarEvento("error", $"> Ejecucion bloqueada en staging: {detalleAuditoria}", "#F44747", finalizado: true);
                return;
            }

            ejecucion.Cancelacion.Token.ThrowIfCancellationRequested();
            if (!ProcesoActualElevado() && ServicioSeguridadScripts.RequiereBrokerElevado(ejecucion.Script, ejecucion.Permisos))
            {
                var resultadoBroker = await EjecutarConBrokerAsync(ejecucion, scriptPreparado.Script);
                resultadoAuditoria = resultadoBroker.Resultado;
                codigoSalida = resultadoBroker.CodigoSalida;
                detalleAuditoria = resultadoBroker.Detalle;
                return;
            }

            using var ejecutor = CrearProceso(scriptPreparado.Script, ejecucion.PermitirExecutionPolicyBypass);
            var proceso = ejecutor.Proceso;
            ejecucion.Proceso = proceso;
            ejecucion.Cancelacion.Token.ThrowIfCancellationRequested();
            proceso.Start();

            var salida = LeerFlujoAsync(proceso.StandardOutput, ejecucion, "info", null);
            var error = LeerFlujoAsync(proceso.StandardError, ejecucion, "error", "#F44747");
            using var tiempoMaximo = CancellationTokenSource.CreateLinkedTokenSource(ejecucion.Cancelacion.Token);
            tiempoMaximo.CancelAfter(TiempoMaximoEjecucion);
            using var cancelacionEntrada = new CancellationTokenSource();
            var entrada = EscribirEntradasProcesoAsync(ejecucion, proceso, cancelacionEntrada.Token);
            var control = ejecutor.LeerControlAsync(evento =>
            {
                ejecucion.AgregarControl(SanitizarControl(ejecucion, evento));
                return Task.CompletedTask;
            }, tiempoMaximo.Token);
            try
            {
                await proceso.WaitForExitAsync(tiempoMaximo.Token);
            }
            catch (OperationCanceledException)
            {
                resultadoAuditoria = ejecucion.Cancelada ? "cancelado" : "timeout";
                detalleAuditoria = ejecucion.Cancelada
                    ? "Cancelada por el usuario."
                    : $"Tiempo maximo de ejecucion superado: {TiempoMaximoEjecucion.TotalMinutes:0} minutos.";
                ejecucion.AgregarEvento("error", $"> {detalleAuditoria}", "#F44747", finalizado: true);
                try
                {
                    proceso.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return;
            }
            finally
            {
                cancelacionEntrada.Cancel();
                ejecucion.Entradas.Writer.TryComplete();
                await entrada;
            }

            await Task.WhenAll(salida, error, control);

            codigoSalida = proceso.ExitCode;
            if (ejecucion.Cancelada)
            {
                resultadoAuditoria = "cancelado";
                detalleAuditoria = "Cancelada por el usuario.";
                ejecucion.AgregarEvento("error", "> Ejecucion cancelada por el usuario.", "#F44747", finalizado: true);
                return;
            }

            if (proceso.ExitCode == 0)
            {
                resultadoAuditoria = "correcto";
                ejecucion.AgregarEvento("exito", $"> Finalizada correctamente. Codigo de salida: {proceso.ExitCode}", "#B5CEA8", finalizado: true);
            }
            else
            {
                resultadoAuditoria = "error";
                detalleAuditoria = $"Codigo de salida: {proceso.ExitCode}";
                ejecucion.AgregarEvento("error", $"> Error. Codigo de salida: {proceso.ExitCode}", "#F44747", finalizado: true);
            }

        }
        catch (OperationCanceledException) when (ejecucion.Cancelada)
        {
            resultadoAuditoria = "cancelado";
            detalleAuditoria = "Cancelada por el usuario.";
            ejecucion.AgregarEvento("error", "> Ejecucion cancelada por el usuario.", "#F44747", finalizado: true);
        }
        catch (Exception ex)
        {
            detalleAuditoria = SanitizarMensaje(ejecucion.Script, ex.Message);
            ejecucion.AgregarEvento("error", $"> Error: {detalleAuditoria}", "#F44747", finalizado: true);
        }
        finally
        {
            ejecucion.Entradas.Writer.TryComplete();
            var auditoria = await _servicioAuditoria.RegistrarFinEjecucionAsync(
                ejecucion.Id,
                ejecucion.Script,
                ejecucion.Usuario,
                ejecucion.Sha256,
                resultadoAuditoria,
                codigoSalida,
                detalleAuditoria);
            if (!auditoria.Exito)
            {
                ejecucion.AgregarEvento(
                    "error",
                    "> El resultado queda pendiente de confirmar en la auditoria remota. Se bloquearan nuevas ejecuciones.",
                    "#F44747", finalizado: true);
            }

            ejecucion.MarcarFinalizada();
            medicion.Completar(resultadoAuditoria == "correcto" && auditoria.Exito);
        }
    }

    private static bool ProcesoActualElevado()
    {
        try
        {
            using var identidad = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identidad).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private async Task<ResultadoEjecucionBroker> EjecutarConBrokerAsync(EjecucionWeb ejecucion, ScriptInterno scriptPreparado)
    {
        ejecucion.AgregarEvento("info", "> Solicitando broker elevado para script autorizado...", "#9CDCFE");
        using var tiempoMaximo = CancellationTokenSource.CreateLinkedTokenSource(ejecucion.Cancelacion.Token);
        tiempoMaximo.CancelAfter(TiempoMaximoEjecucion);

        var resultado = new ResultadoEjecucionBroker("error", null, "Broker elevado sin resultado final.");
        try
        {
            await foreach (var evento in _servicioBrokerElevado.EjecutarAsync(scriptPreparado, ejecucion.PermitirExecutionPolicyBypass, tiempoMaximo.Token, ejecucion.Entradas.Reader))
            {
                if (evento.Progreso is not null || evento.EntradaProtegida is not null)
                    ejecucion.AgregarControl(SanitizarControl(ejecucion, new EventoCliente(evento.Tipo, "", Progreso: evento.Progreso, EntradaProtegida: evento.EntradaProtegida)));
                if (!string.IsNullOrWhiteSpace(evento.Mensaje))
                {
                    var mensaje = SanitizarMensaje(ejecucion, evento.Mensaje);
                    ejecucion.AgregarEvento(evento.Tipo, mensaje, evento.Color, evento.Finalizado);
                }

                if (evento.Finalizado)
                {
                    resultado = new ResultadoEjecucionBroker(
                        string.IsNullOrWhiteSpace(evento.Resultado) ? "error" : evento.Resultado,
                        evento.CodigoSalida,
                        evento.Detalle);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            resultado = ejecucion.Cancelada
                ? new ResultadoEjecucionBroker("cancelado", null, "Cancelada por el usuario.")
                : new ResultadoEjecucionBroker("timeout", null, $"Tiempo maximo de ejecucion superado: {TiempoMaximoEjecucion.TotalMinutes:0} minutos.");
            ejecucion.AgregarEvento("error", $"> {resultado.Detalle}", "#F44747", finalizado: true);
        }

        return resultado;
    }

    private static EventoCliente SanitizarControl(EjecucionWeb ejecucion, EventoCliente evento)
    {
        if (evento.Progreso is not { } progreso) return evento;
        return evento with { Progreso = progreso with
        {
            Descripcion = SanitizarMensaje(ejecucion, progreso.Descripcion),
            Estado = SanitizarMensaje(ejecucion, progreso.Estado),
            Operacion = SanitizarMensaje(ejecucion, progreso.Operacion)
        }};
    }

    private static async Task LeerFlujoAsync(StreamReader lector, EjecucionWeb ejecucion, string tipo, string? color)
    {
        var buffer = new char[4096];
        int leidos;
        while ((leidos = await lector.ReadAsync(buffer.AsMemory())) > 0)
            ejecucion.AgregarEvento(tipo, SanitizarMensaje(ejecucion, new string(buffer, 0, leidos)), color);
    }

    private ScriptPreparado CrearCopiaTemporalValidada(EjecucionWeb ejecucion)
    {
        Directory.CreateDirectory(_rutaStaging);

        var directorio = Path.Combine(_rutaStaging, ejecucion.Id.ToString("N"));
        Directory.CreateDirectory(directorio);
        AplicarAclDirectorioStaging(directorio);

        var nombreArchivo = Path.GetFileName(ejecucion.Script.RutaCompleta);
        var rutaDestino = Path.Combine(directorio, nombreArchivo);
        using (var origen = ejecucion.Script.RutaValidada.AbrirLectura())
        using (var destino = new FileStream(
            rutaDestino,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough))
        {
            origen.CopyTo(destino);
            destino.Flush(flushToDisk: true);
        }

        File.SetAttributes(rutaDestino, File.GetAttributes(rutaDestino) | FileAttributes.ReadOnly);

        // Mantiene la copia abierta solo para lectura y bloquea escrituras hasta terminar.
        var bloqueoLectura = new FileStream(rutaDestino, FileMode.Open, FileAccess.Read, FileShare.Read);
        var validacion = new ServicioValidacionScripts().ValidarRutaConocida(
            directorio,
            rutaDestino,
            ejecucion.Script.Id,
            ejecucion.Script.Nombre,
            ejecucion.Script.Tipo);
        if (!validacion.EsValido)
        {
            bloqueoLectura.Dispose();
            throw new InvalidOperationException(
                $"La copia temporal no supero la validacion: {validacion.Mensaje}");
        }

        var scriptPreparado = validacion.Script!;
        return new ScriptPreparado(scriptPreparado, directorio, bloqueoLectura);
    }

    private static void AplicarAclDirectorioStaging(string directorio)
    {
        try
        {
            var usuarioActual = WindowsIdentity.GetCurrent().User;
            if (usuarioActual is null)
            {
                return;
            }

            var administradores = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var sistema = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var herencia = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var seguridad = new DirectorySecurity();
            seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            seguridad.AddAccessRule(new FileSystemAccessRule(usuarioActual, FileSystemRights.Modify | FileSystemRights.ReadAndExecute, herencia, PropagationFlags.None, AccessControlType.Allow));
            seguridad.AddAccessRule(new FileSystemAccessRule(administradores, FileSystemRights.FullControl, herencia, PropagationFlags.None, AccessControlType.Allow));
            seguridad.AddAccessRule(new FileSystemAccessRule(sistema, FileSystemRights.FullControl, herencia, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directorio).SetAccessControl(seguridad);
        }
        catch
        {
            // La ejecucion sigue fail-closed por integridad aunque la ACL local no pueda endurecerse.
        }
    }

    internal static ServicioProcesoScript CrearProceso(ScriptInterno script, bool permitirExecutionPolicyBypass)
    {
        return new ServicioProcesoScript(script, permitirExecutionPolicyBypass);
    }

    private static async Task EscribirEntradasProcesoAsync(EjecucionWeb ejecucion, Process proceso, CancellationToken cancelacion)
    {
        try
        {
            await foreach (var texto in ejecucion.Entradas.Reader.ReadAllAsync(cancelacion))
            {
                await proceso.StandardInput.WriteLineAsync(texto.AsMemory(), cancelacion);
                await proceso.StandardInput.FlushAsync(cancelacion);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            if (!cancelacion.IsCancellationRequested)
                ejecucion.AgregarEvento("error", "> El proceso dejo de admitir respuestas.", "#F44747");
        }
    }

    private static string SanitizarMensaje(ScriptInterno script, string texto)
    {
        texto = OcultarRutas(script, texto);
        return ServicioRedaccionSecretos.Sanitizar(Regex.Replace(
            texto,
            @"(?i)\b(token|password|contrasena|contraseña|clave)\b\s*[:=]\s*[^\s]+",
            "$1=[oculto]"));
    }

    private static string SanitizarMensaje(EjecucionWeb ejecucion, string texto)
    {
        texto = OcultarRutas(ejecucion.Script, texto);
        if (!string.IsNullOrWhiteSpace(ejecucion.RutaScriptPreparado))
        {
            texto = OcultarRutaPreparada(ejecucion.RutaScriptPreparado, texto);
        }

        return ServicioRedaccionSecretos.Sanitizar(Regex.Replace(
            texto,
            @"(?i)\b(token|password|contrasena|contraseña|clave)\b\s*[:=]\s*[^\s]+",
            "$1=[oculto]"));
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

    private static string OcultarRutaPreparada(string rutaScript, string texto)
    {
        var carpeta = Path.GetDirectoryName(rutaScript);
        if (!string.IsNullOrWhiteSpace(carpeta))
        {
            texto = texto.Replace(carpeta, "[staging protegido]", StringComparison.OrdinalIgnoreCase);
        }

        return texto.Replace(rutaScript, "[script staging protegido]", StringComparison.OrdinalIgnoreCase);
    }

    private static int LeerUltimoIndiceEvento(HttpListenerRequest peticion)
    {
        return int.TryParse(peticion.Headers["Last-Event-ID"], out var ultimoId)
            ? Math.Max(0, ultimoId)
            : 0;
    }

    private void PurgarFinalizadasAntiguas()
    {
        var limite = DateTimeOffset.UtcNow - TtlEjecucionesFinalizadas;
        foreach (var item in _ejecuciones.Where(item => item.Value.FinalizadaUtc is not null && item.Value.FinalizadaUtc < limite).ToList())
        {
            if (_ejecuciones.TryRemove(item.Key, out var ejecucion))
            {
                ejecucion.Dispose();
            }
        }
    }

    private sealed class EjecucionWeb : IDisposable
    {
        private readonly List<EventoCliente> _eventos = [];
        private readonly SemaphoreSlim _senal = new(0);
        private readonly object _bloqueo = new();
        private bool _salidaTruncada;
        private int _caracteresSalida;
        private int _revisionControl;
        private readonly Dictionary<(long, int), EventoCliente> _progresos = [];
        private EventoCliente? _entradaControl;

        public EjecucionWeb(
            ScriptInterno script,
            string rutaLogs,
            UsuarioCliente usuario,
            bool permitirExecutionPolicyBypass,
            JsonObject permisos,
            CatalogoScripts catalogo,
            bool modoDesarrolloFirmas,
            string sha256)
        {
            Script = script;
            RutaLogs = rutaLogs;
            Usuario = usuario;
            PermitirExecutionPolicyBypass = permitirExecutionPolicyBypass;
            Permisos = permisos;
            Catalogo = catalogo;
            ModoDesarrolloFirmas = modoDesarrolloFirmas;
            Sha256 = sha256;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public ScriptInterno Script { get; }

        public string RutaLogs { get; }

        public UsuarioCliente Usuario { get; }

        public bool PermitirExecutionPolicyBypass { get; }

        public JsonObject Permisos { get; }

        public CatalogoScripts Catalogo { get; }

        public bool ModoDesarrolloFirmas { get; }

        public string Sha256 { get; }

        public Process? Proceso { get; set; }

        public string? RutaScriptPreparado { get; set; }

        public CancellationTokenSource Cancelacion { get; } = new();
        public Channel<string> Entradas { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(32)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
        });

        public Task? TareaEjecucion { get; set; }

        public bool Cancelada => Cancelacion.IsCancellationRequested;

        public bool Finalizada { get; private set; }

        public DateTimeOffset? FinalizadaUtc { get; private set; }

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
                if (!finalizado && (_eventos.Count >= MaximoEventosPorEjecucion || _caracteresSalida + mensaje.Length > MaximoCaracteresSalida))
                {
                    // Agrega posiciones nuevas para que los observadores no pierdan el resultado final.
                    if (!_salidaTruncada)
                    {
                        _eventos.Add(new EventoCliente("error", "> Salida truncada por limite de memoria.", "#F44747"));
                        _salidaTruncada = true;
                        _senal.Release();
                    }
                    return;
                }

                if (finalizado && _eventos.Count >= MaximoEventosPorEjecucion + 3) return;
                _caracteresSalida += mensaje.Length;
                _eventos.Add(new EventoCliente(tipo, mensaje, color, finalizado));
            }

            _senal.Release();
        }

        public void AgregarControl(EventoCliente evento)
        {
            lock (_bloqueo)
            {
                if (evento.Progreso is { } progreso)
                {
                    var clave = (progreso.Origen, progreso.Actividad);
                    if (!_progresos.ContainsKey(clave) && _progresos.Count >= 128)
                    {
                        var anterior = _progresos.FirstOrDefault(p => p.Value.Progreso?.Completado == true);
                        if (anterior.Value is null) return;
                        _progresos.Remove(anterior.Key);
                    }
                    _progresos[clave] = evento;
                }
                if (evento.EntradaProtegida is not null) _entradaControl = evento;
                _revisionControl++;
            }
            if (_senal.CurrentCount == 0) _senal.Release();
        }

        public (int Revision, EventoCliente[] Eventos) ObtenerControles()
        {
            lock (_bloqueo)
                return (_revisionControl, _progresos.Values.Concat(_entradaControl is null ? [] : new[] { _entradaControl }).ToArray());
        }

        public IReadOnlyList<EventoCliente> ObtenerEventosDesde(int indice)
        {
            lock (_bloqueo)
            {
                return _eventos.Skip(indice).ToList();
            }
        }

        public async Task<bool> EsperarEventoAsync(TimeSpan espera, CancellationToken cancelacion)
        {
            return await _senal.WaitAsync(espera, cancelacion);
        }

        public void MarcarFinalizada()
        {
            Finalizada = true;
            FinalizadaUtc = DateTimeOffset.UtcNow;
            _senal.Release();
        }

        public void Dispose()
        {
            Proceso?.Dispose();
            Cancelacion.Dispose();
            _senal.Dispose();
        }
    }

    private sealed class ScriptPreparado : IDisposable
    {
        private readonly string _directorio;
        private readonly FileStream _bloqueoLectura;

        public ScriptPreparado(ScriptInterno script, string directorio, FileStream bloqueoLectura)
        {
            Script = script;
            _directorio = directorio;
            _bloqueoLectura = bloqueoLectura;
        }

        public ScriptInterno Script { get; }

        public void Dispose()
        {
            _bloqueoLectura.Dispose();
            try
            {
                if (File.Exists(Script.RutaCompleta))
                {
                    File.SetAttributes(Script.RutaCompleta, FileAttributes.Normal);
                }

                if (Directory.Exists(_directorio))
                {
                    Directory.Delete(_directorio, recursive: true);
                }
            }
            catch
            {
                // La limpieza de staging no debe ocultar el resultado operativo del script.
            }
        }
    }

    private sealed record ResultadoEjecucionBroker(string Resultado, int? CodigoSalida, string? Detalle);
}

public sealed record ResultadoInicioEjecucion(bool Exito, Guid? EjecucionId, string Mensaje)
{
    public static ResultadoInicioEjecucion Correcto(Guid ejecucionId)
    {
        return new ResultadoInicioEjecucion(true, ejecucionId, string.Empty);
    }

    public static ResultadoInicioEjecucion Error(string mensaje)
    {
        return new ResultadoInicioEjecucion(false, null, mensaje);
    }
}
