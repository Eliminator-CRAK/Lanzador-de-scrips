// (Autor: Alex Roman)
// Descripcion: Gestiona ejecuciones de scripts solicitadas por el cliente web.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LanzadorScripts.Servicios;

public sealed class GestorEjecucionesWeb : IDisposable
{
    private const int MaximoCaracteresEntrada = 8192;

    private static readonly JsonSerializerOptions OpcionesJsonEventos = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConcurrentDictionary<Guid, EjecucionWeb> _ejecuciones = new();
    private readonly ServicioAuditoria _servicioAuditoria;

    public GestorEjecucionesWeb(ServicioAuditoria servicioAuditoria)
    {
        _servicioAuditoria = servicioAuditoria;
    }

    public int RecuentoActivas => _ejecuciones.Values.Count(ejecucion => !ejecucion.Finalizada);

    public Guid Iniciar(ScriptInterno script, string rutaLogs, UsuarioCliente usuario)
    {
        var ejecucion = new EjecucionWeb(script, rutaLogs, usuario);
        _ejecuciones[ejecucion.Id] = ejecucion;
        ejecucion.AgregarEvento("info", $"### Script-{script.Nombre}");
        ejecucion.AgregarEvento("exito", $"> Iniciando {script.Nombre}... (#B5CEA8)", "#B5CEA8");
        ejecucion.AgregarEvento("info", "> Conectando a servidor...");
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
        try
        {
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
        if (!_ejecuciones.TryGetValue(id, out var ejecucion) || ejecucion.Proceso is null)
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

            using var proceso = CrearProceso(ejecucion.Script);
            ejecucion.Proceso = proceso;
            proceso.Start();

            var salida = LeerFlujoAsync(proceso.StandardOutput, ejecucion, log, "info", null);
            var error = LeerFlujoAsync(proceso.StandardError, ejecucion, log, "error", "#F44747");
            await proceso.WaitForExitAsync();
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

    private static async Task EscribirCabeceraLogAsync(StreamWriter log, EjecucionWeb ejecucion)
    {
        await log.WriteLineAsync($"Id ejecucion: {ejecucion.Id}");
        await log.WriteLineAsync($"Usuario: {ejecucion.Usuario.NombreUsuario}");
        await log.WriteLineAsync($"Equipo: {Environment.MachineName}");
        await log.WriteLineAsync($"Script: {ejecucion.Script.Nombre}");
        await log.WriteLineAsync($"ScriptId: {ejecucion.Script.Id}");
        await log.WriteLineAsync($"Inicio local: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        await log.WriteLineAsync($"Inicio UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        await log.WriteLineAsync();
    }

    private static async Task LeerFlujoAsync(StreamReader lector, EjecucionWeb ejecucion, StreamWriter log, string tipo, string? color)
    {
        var buffer = new char[512];
        int leidos;
        while ((leidos = await lector.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            var texto = SanitizarMensaje(ejecucion.Script, new string(buffer, 0, leidos));
            ejecucion.AgregarEvento(tipo, texto, color);
            await log.WriteAsync(texto);
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
            inicio.FileName = ObtenerRutaPowerShell();
            inicio.ArgumentList.Add("-NoLogo");
            inicio.ArgumentList.Add("-NoProfile");
            inicio.ArgumentList.Add("-ExecutionPolicy");
            inicio.ArgumentList.Add("Bypass");
            inicio.ArgumentList.Add("-Command");
            inicio.ArgumentList.Add(CrearComandoPowerShell(script.RutaCompleta));
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
        var ruta = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        return File.Exists(ruta) ? ruta : "powershell.exe";
    }

    private static string ObtenerRutaCmd()
    {
        var ruta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        return File.Exists(ruta) ? ruta : "cmd.exe";
    }

    private static string CrearComandoPowerShell(string rutaScript)
    {
        var rutaEscapada = rutaScript.Replace("'", "''");
        var parametros = ObtenerParametrosObligatorios(rutaScript);
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
        return Regex.Replace(
            texto,
            @"(?i)\b(token|password|contrasena|contraseña|clave)\b\s*[:=]\s*[^\s]+",
            "$1=[oculto]");
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

    private static int LeerUltimoIndiceEvento(HttpListenerRequest peticion)
    {
        return int.TryParse(peticion.Headers["Last-Event-ID"], out var ultimoId)
            ? Math.Max(0, ultimoId)
            : 0;
    }

    private sealed class EjecucionWeb(ScriptInterno script, string rutaLogs, UsuarioCliente usuario) : IDisposable
    {
        private readonly List<EventoCliente> _eventos = [];
        private readonly SemaphoreSlim _senal = new(0);
        private readonly object _bloqueo = new();

        public Guid Id { get; } = Guid.NewGuid();

        public ScriptInterno Script { get; } = script;

        public string RutaLogs { get; } = rutaLogs;

        public UsuarioCliente Usuario { get; } = usuario;

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

        public async Task<bool> EsperarEventoAsync(TimeSpan espera, CancellationToken cancelacion)
        {
            return await _senal.WaitAsync(espera, cancelacion);
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
}
