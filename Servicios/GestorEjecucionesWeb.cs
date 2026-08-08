// (Autor: Alex Roman)
// Descripcion: Gestiona ejecuciones de scripts solicitadas por el cliente web.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LanzadorScripts.Servicios;

public sealed class GestorEjecucionesWeb : IDisposable
{
    private const int MaximoCaracteresEntrada = 8192;
    private const int MaximoEventosPorEjecucion = 5000;
    private static readonly TimeSpan TiempoMaximoEjecucion = TimeSpan.FromHours(2);
    private static readonly TimeSpan TtlEjecucionesFinalizadas = TimeSpan.FromMinutes(30);
    private static readonly Lazy<string> RutaPowerShell = new(ResolverRutaPowerShell);
    private static readonly Lazy<string> RutaCmd = new(ResolverRutaCmd);

    private static readonly JsonSerializerOptions OpcionesJsonEventos = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConcurrentDictionary<Guid, EjecucionWeb> _ejecuciones = new();
    private readonly ServicioAuditoria _servicioAuditoria;
    private readonly ServicioSeguridadScripts _servicioSeguridadScripts;
    private readonly ServicioBrokerElevado _servicioBrokerElevado = new();
    private readonly string _rutaStaging;

    public GestorEjecucionesWeb(
        ServicioAuditoria servicioAuditoria,
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

    public Guid Iniciar(
        ScriptInterno script,
        string rutaLogs,
        UsuarioCliente usuario,
        bool permitirExecutionPolicyBypass,
        JsonObject permisos,
        CatalogoScripts catalogo,
        bool modoDesarrolloFirmas)
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
            modoDesarrolloFirmas);
        _ejecuciones[ejecucion.Id] = ejecucion;
        ejecucion.AgregarEvento("exito", $"> Iniciando {script.Nombre}...", "#B5CEA8");
        _ = _servicioAuditoria.RegistrarInicioEjecucionAsync(ejecucion.Id, script, usuario);
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
        _ = _servicioAuditoria.RegistrarEventoSeguridadAsync(
            "ejecucion.cancelacion",
            ejecucion.Usuario.NombreUsuario,
            ejecucion.Script.Id,
            "solicitado",
            "Cancelacion solicitada por el usuario.");
        try
        {
            if (ejecucion.CancelarBroker is not null)
            {
                _ = ejecucion.CancelarBroker();
                return;
            }

            if (ejecucion.Proceso is not null && !ejecucion.Proceso.HasExited)
            {
                ejecucion.Proceso.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            var mensaje = SanitizarMensaje(ejecucion.Script, ex.Message);
            ejecucion.AgregarEvento("error", $"> Error al cancelar: {mensaje}", "#F44747");
        }
    }

    public async Task EnviarEntradaAsync(Guid id, string texto)
    {
        if (!_ejecuciones.TryGetValue(id, out var ejecucion))
        {
            return;
        }

        if (ejecucion.CancelarBroker is not null)
        {
            ejecucion.AgregarEvento("error", "> Entrada interactiva no disponible para ejecuciones elevadas por broker.", "#F44747");
            return;
        }

        if (ejecucion.Proceso is null)
        {
            return;
        }

        if (texto.Length > MaximoCaracteresEntrada)
        {
            ejecucion.AgregarEvento("error", "> Entrada rechazada por exceder el tamano maximo permitido.", "#F44747");
            return;
        }

        try
        {
            await ejecucion.Proceso.StandardInput.WriteLineAsync(texto);
            await ejecucion.Proceso.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            var mensaje = SanitizarMensaje(ejecucion.Script, ex.Message);
            ejecucion.AgregarEvento("error", $"> Error al enviar entrada: {mensaje}", "#F44747");
        }
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

    public void Dispose()
    {
        foreach (var ejecucion in _ejecuciones.Values)
        {
            try
            {
                if (ejecucion.CancelarBroker is not null)
                {
                    _ = ejecucion.CancelarBroker();
                }

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

    private async Task EjecutarAsync(EjecucionWeb ejecucion)
    {
        var resultadoAuditoria = "error";
        int? codigoSalida = null;
        string? detalleAuditoria = null;

        try
        {
            Directory.CreateDirectory(ejecucion.RutaLogs);
            var rutaLog = ConstruirRutaLog(ejecucion);
            await using var log = new StreamWriter(rutaLog, append: false, Encoding.UTF8)
            {
                AutoFlush = true
            };

            await EscribirCabeceraLogAsync(log, ejecucion);
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
                await log.WriteLineAsync($"Bloqueo pre-ejecucion: {detalleAuditoria}");
                return;
            }

            await EscribirIntegridadValidadaAsync(log, diagnostico);
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
                await log.WriteLineAsync($"Bloqueo staging: {detalleAuditoria}");
                return;
            }

            await EscribirIntegridadStagingAsync(log, scriptPreparado.Script, diagnosticoPreparado);
            if (!ProcesoActualElevado() && ServicioSeguridadScripts.RequiereBrokerElevado(ejecucion.Script, ejecucion.Permisos))
            {
                var resultadoBroker = await EjecutarConBrokerAsync(ejecucion, scriptPreparado.Script, log);
                resultadoAuditoria = resultadoBroker.Resultado;
                codigoSalida = resultadoBroker.CodigoSalida;
                detalleAuditoria = resultadoBroker.Detalle;
                return;
            }

            using var proceso = CrearProceso(scriptPreparado.Script, ejecucion.PermitirExecutionPolicyBypass);
            ejecucion.Proceso = proceso;
            proceso.Start();

            var salida = LeerFlujoAsync(proceso.StandardOutput, ejecucion, log, "info", null);
            var error = LeerFlujoAsync(proceso.StandardError, ejecucion, log, "error", "#F44747");
            using var tiempoMaximo = new CancellationTokenSource(TiempoMaximoEjecucion);
            try
            {
                await proceso.WaitForExitAsync(tiempoMaximo.Token);
            }
            catch (OperationCanceledException)
            {
                resultadoAuditoria = "timeout";
                detalleAuditoria = $"Tiempo maximo de ejecucion superado: {TiempoMaximoEjecucion.TotalMinutes:0} minutos.";
                ejecucion.AgregarEvento("error", $"> {detalleAuditoria}", "#F44747", finalizado: true);
                await log.WriteLineAsync(detalleAuditoria);
                try
                {
                    proceso.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return;
            }

            await Task.WhenAll(salida, error);

            codigoSalida = proceso.ExitCode;
            if (ejecucion.Cancelada)
            {
                resultadoAuditoria = "cancelado";
                detalleAuditoria = "Cancelada por el usuario.";
                ejecucion.AgregarEvento("error", "> Ejecucion cancelada por el usuario.", "#F44747", finalizado: true);
                await log.WriteLineAsync("Cancelada por el usuario.");
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

            await log.WriteLineAsync();
            await log.WriteLineAsync($"Fin local: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            await log.WriteLineAsync($"Fin UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            await log.WriteLineAsync($"Codigo de salida: {proceso.ExitCode}");
        }
        catch (Exception ex)
        {
            detalleAuditoria = SanitizarMensaje(ejecucion.Script, ex.Message);
            ejecucion.AgregarEvento("error", $"> Error: {detalleAuditoria}", "#F44747", finalizado: true);
        }
        finally
        {
            ejecucion.MarcarFinalizada();
            await _servicioAuditoria.RegistrarFinEjecucionAsync(
                ejecucion.Id,
                ejecucion.Script,
                ejecucion.Usuario,
                resultadoAuditoria,
                codigoSalida,
                detalleAuditoria);
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

    private async Task<ResultadoEjecucionBroker> EjecutarConBrokerAsync(EjecucionWeb ejecucion, ScriptInterno scriptPreparado, StreamWriter log)
    {
        await log.WriteLineAsync("Ejecucion elevada: broker minimo solicitado por allowlist.");
        ejecucion.AgregarEvento("info", "> Solicitando broker elevado para script autorizado...", "#9CDCFE");
        using var tiempoMaximo = new CancellationTokenSource(TiempoMaximoEjecucion);
        ejecucion.CancelarBroker = () =>
        {
            tiempoMaximo.Cancel();
            return Task.CompletedTask;
        };

        var resultado = new ResultadoEjecucionBroker("error", null, "Broker elevado sin resultado final.");
        try
        {
            await foreach (var evento in _servicioBrokerElevado.EjecutarAsync(scriptPreparado, ejecucion.PermitirExecutionPolicyBypass, tiempoMaximo.Token))
            {
                if (!string.IsNullOrWhiteSpace(evento.Mensaje))
                {
                    var mensaje = SanitizarMensaje(ejecucion, evento.Mensaje);
                    ejecucion.AgregarEvento(evento.Tipo, mensaje, evento.Color, evento.Finalizado);
                    await log.WriteAsync(mensaje);
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
            await log.WriteLineAsync(resultado.Detalle);
        }
        finally
        {
            ejecucion.CancelarBroker = null;
            await log.WriteLineAsync();
            await log.WriteLineAsync($"Fin broker UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        }

        return resultado;
    }

    private static async Task EscribirCabeceraLogAsync(StreamWriter log, EjecucionWeb ejecucion)
    {
        await log.WriteLineAsync($"Id ejecucion: {ejecucion.Id}");
        await log.WriteLineAsync($"Usuario: {ejecucion.Usuario.NombreUsuario}");
        await log.WriteLineAsync($"Equipo: {Environment.MachineName}");
        await log.WriteLineAsync($"Script: {ejecucion.Script.Nombre}");
        await log.WriteLineAsync($"ScriptId: {ejecucion.Script.Id}");
        await log.WriteLineAsync($"Inicio local: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        await log.WriteLineAsync($"Inicio UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        await log.WriteLineAsync($"ExecutionPolicyBypass: {ejecucion.PermitirExecutionPolicyBypass}");
        await log.WriteLineAsync();
    }

    private static async Task EscribirIntegridadValidadaAsync(StreamWriter log, DiagnosticoEjecucionScript diagnostico)
    {
        await log.WriteLineAsync("Integridad validada antes de ejecutar:");
        await log.WriteLineAsync($"Catalogo estado: {diagnostico.CatalogoEstado}");
        await log.WriteLineAsync($"Catalogo ConjuntoId: {diagnostico.CatalogoConjuntoId}");
        await log.WriteLineAsync($"SHA-256: {diagnostico.Sha256}");
        await log.WriteLineAsync();
    }

    private static async Task EscribirIntegridadStagingAsync(StreamWriter log, ScriptInterno script, DiagnosticoEjecucionScript diagnostico)
    {
        await log.WriteLineAsync("Copia temporal validada:");
        await log.WriteLineAsync($"Ruta staging: {script.RutaCompleta}");
        await log.WriteLineAsync($"Catalogo estado: {diagnostico.CatalogoEstado}");
        await log.WriteLineAsync($"Catalogo ConjuntoId: {diagnostico.CatalogoConjuntoId}");
        await log.WriteLineAsync($"SHA-256 final: {ServicioSeguridadScripts.CalcularSha256(script.RutaValidada)}");
        await log.WriteLineAsync();
    }

    private static async Task LeerFlujoAsync(StreamReader lector, EjecucionWeb ejecucion, StreamWriter log, string tipo, string? color)
    {
        var buffer = new char[512];
        int leidos;
        while ((leidos = await lector.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            var texto = SanitizarMensaje(ejecucion, new string(buffer, 0, leidos));
            ejecucion.AgregarEvento(tipo, texto, color);
            await log.WriteAsync(texto);
        }
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

    private static Process CrearProceso(ScriptInterno script, bool permitirExecutionPolicyBypass)
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
            var plan = CrearPlanPowerShell(script.RutaCompleta);
            inicio.FileName = ObtenerRutaPowerShell();
            inicio.ArgumentList.Add("-NoLogo");
            inicio.ArgumentList.Add("-NoProfile");
            if (permitirExecutionPolicyBypass)
            {
                inicio.ArgumentList.Add("-ExecutionPolicy");
                inicio.ArgumentList.Add("Bypass");
            }

            if (plan.UsarRutaRapida)
            {
                inicio.ArgumentList.Add("-NonInteractive");
                inicio.ArgumentList.Add("-File");
                inicio.ArgumentList.Add(script.RutaCompleta);
            }
            else
            {
                inicio.ArgumentList.Add("-Command");
                inicio.ArgumentList.Add(plan.Comando);
            }
        }
        else
        {
            inicio.FileName = ObtenerRutaCmd();
            inicio.ArgumentList.Add("/d");
            inicio.ArgumentList.Add("/c");
            inicio.ArgumentList.Add(script.RutaCompleta);
        }

        return new Process { StartInfo = inicio, EnableRaisingEvents = true };
    }

    private static string ObtenerRutaPowerShell()
    {
        return RutaPowerShell.Value;
    }

    private static string ResolverRutaPowerShell()
    {
        var ruta = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        return File.Exists(ruta) ? ruta : "powershell.exe";
    }

    private static string ObtenerRutaCmd()
    {
        return RutaCmd.Value;
    }

    private static string ResolverRutaCmd()
    {
        var ruta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        return File.Exists(ruta) ? ruta : "cmd.exe";
    }

    private static PlanPowerShell CrearPlanPowerShell(string rutaScript)
    {
        var parametros = ObtenerParametrosObligatorios(rutaScript);
        if (parametros.Count == 0 && !RequiereAdaptadorInteractivo(rutaScript))
        {
            return new PlanPowerShell(true, string.Empty);
        }

        return new PlanPowerShell(false, CrearComandoPowerShell(rutaScript, parametros));
    }

    private static string CrearComandoPowerShell(string rutaScript, IReadOnlyList<string> parametros)
    {
        var rutaEscapada = rutaScript.Replace("'", "''");
        var adaptadorInteractivo = CrearAdaptadorInteractivoPowerShell();
        if (parametros.Count == 0)
        {
            return $"{adaptadorInteractivo}$ErrorActionPreference='Continue'; & '{rutaEscapada}' *>&1; exit $LASTEXITCODE";
        }

        var constructor = new StringBuilder(adaptadorInteractivo);
        constructor.Append("$ErrorActionPreference='Continue'; $__args=@{};");
        foreach (var parametro in parametros)
        {
            var nombre = parametro.Replace("'", "''");
            constructor.Append($"[Console]::Write('{nombre}: '); $__args['{nombre}'] = [Console]::ReadLine();");
        }

        constructor.Append($"& '{rutaEscapada}' @__args *>&1; exit $LASTEXITCODE");
        return constructor.ToString();
    }

    private static bool RequiereAdaptadorInteractivo(string rutaScript)
    {
        try
        {
            var texto = File.ReadAllText(rutaScript, Encoding.UTF8);
            return Regex.IsMatch(texto, @"(?im)\b(Read-Host|Pause|Get-Credential)\b");
        }
        catch
        {
            return true;
        }
    }

    private static string CrearAdaptadorInteractivoPowerShell()
    {
        // Muestra preguntas interactivas en la consola web.
        return """
function global:Read-Host {
    param(
        [Parameter(Position=0)]
        [string]$Prompt,
        [switch]$AsSecureString,
        [switch]$MaskInput
    )
    if (-not [string]::IsNullOrWhiteSpace($Prompt)) {
        [Console]::Write($Prompt + ': ')
    }
    $valor = [Console]::ReadLine()
    if ($AsSecureString -or $MaskInput) {
        return ConvertTo-SecureString ([string]$valor) -AsPlainText -Force
    }
    return $valor
}
function global:Pause {
    [Console]::Write('Presione Enter para continuar...')
    [Console]::ReadLine() | Out-Null
}
function global:Get-Credential {
    param(
        [string]$Message,
        [string]$UserName
    )
    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        [Console]::WriteLine($Message)
    }
    if ([string]::IsNullOrWhiteSpace($UserName)) {
        [Console]::Write('Usuario: ')
        $UserName = [Console]::ReadLine()
    }
    [Console]::Write('Password: ')
    $clave = [Console]::ReadLine()
    $segura = ConvertTo-SecureString ([string]$clave) -AsPlainText -Force
    return New-Object System.Management.Automation.PSCredential($UserName, $segura)
}
""";
    }

    private static IReadOnlyList<string> ObtenerParametrosObligatorios(string rutaScript)
    {
        try
        {
            var texto = File.ReadAllText(rutaScript, Encoding.UTF8);
            var bloqueParametros = ObtenerBloqueParametrosPrincipal(texto);
            if (string.IsNullOrWhiteSpace(bloqueParametros))
            {
                return [];
            }

            var coincidencias = Regex.Matches(
                bloqueParametros,
                @"(?is)\[Parameter\s*\([^\]]*\bMandatory\b(?:\s*=\s*\$?true)?[^\]]*\)\](?:(?:\s*\[[^\]]+\])*)\s*\$(?<nombre>[A-Za-z_][A-Za-z0-9_]*)");

            return coincidencias
                .Select(coincidencia => coincidencia.Groups["nombre"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? ObtenerBloqueParametrosPrincipal(string texto)
    {
        foreach (Match coincidencia in Regex.Matches(texto, @"(?im)\bparam\s*\("))
        {
            if (!PrefijoValidoParaParamPrincipal(texto[..coincidencia.Index]))
            {
                continue;
            }

            var apertura = texto.IndexOf('(', coincidencia.Index);
            var cierre = EncontrarCierreParentesis(texto, apertura);
            if (apertura >= 0 && cierre > apertura)
            {
                return texto.Substring(apertura + 1, cierre - apertura - 1);
            }
        }

        return null;
    }

    private static bool PrefijoValidoParaParamPrincipal(string prefijo)
    {
        // Permite comentarios, regiones y atributos antes del param principal.
        var sinComentariosBloque = Regex.Replace(prefijo, @"(?s)<#.*?#>", string.Empty);
        var sinComentariosLinea = Regex.Replace(sinComentariosBloque, @"(?m)#.*$", string.Empty);
        var sinAtributos = Regex.Replace(sinComentariosLinea, @"(?m)^\s*\[[^\r\n]+\]\s*$", string.Empty);
        var sinUsings = Regex.Replace(sinAtributos, @"(?im)^\s*using\s+(assembly|module|namespace)\s+.*$", string.Empty);
        return string.IsNullOrWhiteSpace(sinUsings);
    }

    private static int EncontrarCierreParentesis(string texto, int apertura)
    {
        if (apertura < 0 || apertura >= texto.Length || texto[apertura] != '(')
        {
            return -1;
        }

        var profundidad = 0;
        var comentarioLinea = false;
        var comentarioBloque = false;
        var cadenaSimple = false;
        var cadenaDoble = false;

        for (var indice = apertura; indice < texto.Length; indice++)
        {
            var actual = texto[indice];
            var siguiente = indice + 1 < texto.Length ? texto[indice + 1] : '\0';

            if (comentarioLinea)
            {
                if (actual is '\r' or '\n')
                {
                    comentarioLinea = false;
                }

                continue;
            }

            if (comentarioBloque)
            {
                if (actual == '#' && siguiente == '>')
                {
                    comentarioBloque = false;
                    indice++;
                }

                continue;
            }

            if (cadenaSimple)
            {
                if (actual == '\'' && siguiente == '\'')
                {
                    indice++;
                }
                else if (actual == '\'')
                {
                    cadenaSimple = false;
                }

                continue;
            }

            if (cadenaDoble)
            {
                if (actual == '`')
                {
                    indice++;
                }
                else if (actual == '"')
                {
                    cadenaDoble = false;
                }

                continue;
            }

            if (actual == '#')
            {
                comentarioLinea = true;
                continue;
            }

            if (actual == '<' && siguiente == '#')
            {
                comentarioBloque = true;
                indice++;
                continue;
            }

            if (actual == '\'')
            {
                cadenaSimple = true;
                continue;
            }

            if (actual == '"')
            {
                cadenaDoble = true;
                continue;
            }

            if (actual == '(')
            {
                profundidad++;
            }
            else if (actual == ')')
            {
                profundidad--;
                if (profundidad == 0)
                {
                    return indice;
                }
            }
        }

        return -1;
    }

    private static string ConstruirRutaLog(EjecucionWeb ejecucion)
    {
        var carpetaDia = Path.Combine(ejecucion.RutaLogs, DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(carpetaDia);
        var nombreSeguro = string.Concat(ejecucion.Script.Nombre.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(carpetaDia, $"{DateTime.Now:HHmmss}_{nombreSeguro}_{ejecucion.Id:N}.log");
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

    private sealed record PlanPowerShell(bool UsarRutaRapida, string Comando);

    private sealed class EjecucionWeb(
        ScriptInterno script,
        string rutaLogs,
        UsuarioCliente usuario,
        bool permitirExecutionPolicyBypass,
        JsonObject permisos,
        CatalogoScripts catalogo,
        bool modoDesarrolloFirmas) : IDisposable
    {
        private readonly List<EventoCliente> _eventos = [];
        private readonly SemaphoreSlim _senal = new(0);
        private readonly object _bloqueo = new();

        public Guid Id { get; } = Guid.NewGuid();

        public ScriptInterno Script { get; } = script;

        public string RutaLogs { get; } = rutaLogs;

        public UsuarioCliente Usuario { get; } = usuario;

        public bool PermitirExecutionPolicyBypass { get; } = permitirExecutionPolicyBypass;

        public JsonObject Permisos { get; } = permisos;

        public CatalogoScripts Catalogo { get; } = catalogo;

        public bool ModoDesarrolloFirmas { get; } = modoDesarrolloFirmas;

        public Process? Proceso { get; set; }

        public string? RutaScriptPreparado { get; set; }

        public Func<Task>? CancelarBroker { get; set; }

        public bool Cancelada { get; set; }

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
                if (_eventos.Count >= MaximoEventosPorEjecucion)
                {
                    if (finalizado)
                    {
                        _eventos[^1] = new EventoCliente(tipo, mensaje, color, finalizado);
                    }
                    else if (!_eventos.Any(evento => evento.Mensaje.Contains("limite de eventos", StringComparison.OrdinalIgnoreCase)))
                    {
                        _eventos[^1] = new EventoCliente("error", "> Salida truncada por limite de eventos.", "#F44747");
                    }

                    _senal.Release();
                    return;
                }

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
