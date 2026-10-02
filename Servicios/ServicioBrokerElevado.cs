// (Autor: Alex Roman)
// Descripcion: Ejecuta scripts elevados mediante un broker minimo y autenticado.

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace LanzadorScripts.Servicios;

public sealed class ServicioBrokerElevado
{
    private const string ArgumentoBroker = "--broker-elevado";
    private const string ArgumentoPipe = "--pipe";
    private const string ArgumentoToken = "--token";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = false
    };

    public static bool EstaDisponible()
    {
        return OperatingSystem.IsWindows() &&
            !string.IsNullOrWhiteSpace(ServicioEjecutableAplicacion.ResolverRutaRelanzable());
    }

    public static bool EsSolicitudBroker(string[] argumentos)
    {
        return argumentos.Any(argumento => string.Equals(argumento, ArgumentoBroker, StringComparison.OrdinalIgnoreCase));
    }

    public static int EjecutarModoBroker(string[] argumentos)
    {
        try
        {
            var nombrePipe = LeerArgumento(argumentos, ArgumentoPipe);
            var token = LeerArgumento(argumentos, ArgumentoToken);
            if (string.IsNullOrWhiteSpace(nombrePipe) || string.IsNullOrWhiteSpace(token))
            {
                return 2;
            }

            return EjecutarBrokerAsync(nombrePipe, token).GetAwaiter().GetResult();
        }
        catch
        {
            return 3;
        }
    }

    public async IAsyncEnumerable<EventoBrokerElevado> EjecutarAsync(
        ScriptInterno script, bool permitirExecutionPolicyBypass,
        [EnumeratorCancellation] CancellationToken cancelacion, ChannelReader<string>? entradas = null)
    {
        // No solicita elevacion para una ejecucion cancelada antes del arranque.
        cancelacion.ThrowIfCancellationRequested();
        if (!EstaDisponible())
        {
            yield return EventoBrokerElevado.ErrorFinal("Broker elevado no disponible en este equipo.", null);
            yield break;
        }
        var nombrePipe = $"LanzadorScriptsBroker_{Guid.NewGuid():N}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var pipe = CrearServidorPipe(nombrePipe);
        using var procesoBroker = IniciarBroker(nombrePipe, token);
        // Evita un BOM sincronico que puede bloquear ambos extremos antes de la primera lectura.
        await using var escritor = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var lector = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var bloqueoEnvio = new SemaphoreSlim(1, 1);
        using var vidaEntradas = CancellationTokenSource.CreateLinkedTokenSource(cancelacion);
        var tareaEntradas = Task.CompletedTask;
        try
        {
            using var conexion = CancellationTokenSource.CreateLinkedTokenSource(cancelacion);
            conexion.CancelAfter(TimeSpan.FromSeconds(20));
            await pipe.WaitForConnectionAsync(conexion.Token);
            await EnviarComandoAsync(escritor, bloqueoEnvio, new ComandoBrokerElevado(
                "ejecutar", token, script.Id, script.Nombre, script.Tipo,
                script.RutaValidada.RaizAutorizada, script.RutaCompleta, permitirExecutionPolicyBypass), cancelacion);
            if (entradas is not null)
                tareaEntradas = EnviarEntradasBrokerAsync(entradas, escritor, bloqueoEnvio, token, vidaEntradas.Token);
            while (!cancelacion.IsCancellationRequested)
            {
                var linea = await LeerLineaLimitadaAsync(lector, cancelacion);
                if (linea is null) break;
                var evento = JsonSerializer.Deserialize<EventoBrokerElevado>(linea, OpcionesJson);
                if (evento is null) continue;
                yield return evento;
                if (evento.Finalizado) break;
            }
        }
        finally
        {
            vidaEntradas.Cancel();
            try { await tareaEntradas; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            try { if (!procesoBroker.HasExited) procesoBroker.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task EnviarEntradasBrokerAsync(ChannelReader<string> entradas, StreamWriter escritor, SemaphoreSlim bloqueo, string token, CancellationToken cancelacion)
    {
        // El pipe transporta respuestas al stdin del script ya autorizado, nunca comandos nuevos.
        await foreach (var texto in entradas.ReadAllAsync(cancelacion))
            await EnviarComandoAsync(escritor, bloqueo,
                new ComandoBrokerElevado("entrada", token, "", "", "", "", "", false, texto), cancelacion);
    }

    private static async Task EnviarComandoAsync(StreamWriter escritor, SemaphoreSlim bloqueo, ComandoBrokerElevado comando, CancellationToken cancelacion)
    {
        await bloqueo.WaitAsync(cancelacion);
        try { await escritor.WriteLineAsync(JsonSerializer.Serialize(comando, OpcionesJson).AsMemory(), cancelacion); }
        finally { bloqueo.Release(); }
    }

    internal static async Task<string?> LeerLineaLimitadaAsync(StreamReader lector, CancellationToken cancelacion)
    {
        // Limita el mensaje completo, incluidos escapes JSON y salida del proceso.
        var linea = new StringBuilder();
        var caracter = new char[1];
        while (await lector.ReadAsync(caracter.AsMemory(), cancelacion) > 0)
        {
            if (caracter[0] == '\n') return linea.ToString().TrimEnd('\r');
            if (linea.Length >= 131072) throw new InvalidDataException("Mensaje de broker demasiado grande.");
            linea.Append(caracter[0]);
        }
        return linea.Length == 0 ? null : linea.ToString();
    }

    private static async Task<int> EjecutarBrokerAsync(string nombrePipe, string tokenEsperado)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            nombrePipe,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await pipe.ConnectAsync(15000);
        await using var escritorFlujo = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var lector = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var bloqueoEnvio = new SemaphoreSlim(1, 1);
        var primeraLinea = await LeerLineaLimitadaAsync(lector, CancellationToken.None);
        if (primeraLinea is null)
        {
            return 4;
        }

        var comando = JsonSerializer.Deserialize<ComandoBrokerElevado>(primeraLinea, OpcionesJson);
        if (comando is null
            || !string.Equals(comando.Tipo, "ejecutar", StringComparison.OrdinalIgnoreCase)
            || !CompararTextoSeguro(comando.Token, tokenEsperado))
        {
            await EnviarAsync(escritorFlujo, bloqueoEnvio, EventoBrokerElevado.ErrorFinal("Comando de broker no autorizado.", null));
            return 5;
        }

        var validacion = new ServicioValidacionScripts().ValidarRutaConocida(
            comando.RaizAutorizada,
            comando.RutaCompleta,
            comando.ScriptId,
            comando.Nombre,
            comando.TipoScript);
        if (!validacion.EsValido)
        {
            await EnviarAsync(
                escritorFlujo,
                bloqueoEnvio,
                EventoBrokerElevado.ErrorFinal(
                    $"La ruta recibida por el broker no es valida: {validacion.Mensaje}",
                    null));
            return 5;
        }

        var script = validacion.Script!;
        using var ejecutor = GestorEjecucionesWeb.CrearProceso(script, comando.PermitirExecutionPolicyBypass);
        var proceso = ejecutor.Proceso;
        using var cancelacionProceso = new CancellationTokenSource();
        var lectorComandos = Task.CompletedTask;

        try
        {
            proceso.Start();
            lectorComandos = EscucharComandosAsync(lector, tokenEsperado, proceso, cancelacionProceso);
            var salida = LeerFlujoAsync(proceso.StandardOutput, escritorFlujo, bloqueoEnvio, "info", null, cancelacionProceso.Token);
            var error = LeerFlujoAsync(proceso.StandardError, escritorFlujo, bloqueoEnvio, "error", "#F44747", cancelacionProceso.Token);
            var control = ejecutor.LeerControlAsync(e => EnviarAsync(escritorFlujo, bloqueoEnvio,
                new EventoBrokerElevado(e.Tipo, "", null, false, null, "", "", e.Progreso, e.EntradaProtegida)), cancelacionProceso.Token);
            await proceso.WaitForExitAsync(cancelacionProceso.Token);
            await Task.WhenAll(salida, error, control);

            var resultado = proceso.ExitCode == 0 ? "correcto" : "error";
            var mensaje = proceso.ExitCode == 0
                ? $"> Finalizada correctamente por broker elevado. Codigo de salida: {proceso.ExitCode}"
                : $"> Error en broker elevado. Codigo de salida: {proceso.ExitCode}";
            await EnviarAsync(escritorFlujo, bloqueoEnvio, new EventoBrokerElevado(
                resultado == "correcto" ? "exito" : "error",
                mensaje,
                resultado == "correcto" ? "#B5CEA8" : "#F44747",
                true,
                proceso.ExitCode,
                resultado,
                mensaje));
            return proceso.ExitCode;
        }
        catch (OperationCanceledException)
        {
            await EnviarAsync(escritorFlujo, bloqueoEnvio, EventoBrokerElevado.ErrorFinal("Ejecucion elevada cancelada.", null));
            return 6;
        }
        catch (Exception ex)
        {
            await EnviarAsync(escritorFlujo, bloqueoEnvio, EventoBrokerElevado.ErrorFinal($"Error del broker elevado: {ex.Message}", null));
            return 7;
        }
        finally
        {
            cancelacionProceso.Cancel();
            try
            {
                if (!proceso.HasExited)
                {
                    proceso.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try
            {
                await lectorComandos;
            }
            catch
            {
            }
        }
    }

    private static NamedPipeServerStream CrearServidorPipe(string nombrePipe)
    {
        return new NamedPipeServerStream(
            nombrePipe,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static Process IniciarBroker(string nombrePipe, string token)
    {
        var rutaExe = ServicioEjecutableAplicacion.ResolverRutaRelanzable() ??
            throw new InvalidOperationException("No se pudo resolver la ruta del ejecutable.");
        var inicio = new ProcessStartInfo
        {
            FileName = rutaExe,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        inicio.ArgumentList.Add(ArgumentoBroker);
        inicio.ArgumentList.Add(ArgumentoPipe);
        inicio.ArgumentList.Add(nombrePipe);
        inicio.ArgumentList.Add(ArgumentoToken);
        inicio.ArgumentList.Add(token);

        return Process.Start(inicio) ?? throw new InvalidOperationException("No se pudo iniciar el broker elevado.");
    }

    private static async Task LeerFlujoAsync(StreamReader lector, StreamWriter escritor, SemaphoreSlim bloqueoEnvio, string tipo, string? color, CancellationToken cancelacion)
    {
        var buffer = new char[4096];
        int leidos;
        while ((leidos = await lector.ReadAsync(buffer.AsMemory(0, buffer.Length), cancelacion)) > 0)
        {
            await EnviarAsync(escritor, bloqueoEnvio, new EventoBrokerElevado(tipo, new string(buffer, 0, leidos), color, false, null, string.Empty, string.Empty));
        }
    }

    private static async Task EscucharComandosAsync(StreamReader lector, string tokenEsperado, Process proceso, CancellationTokenSource vida)
    {
        var cancelacion = vida.Token;
        try
        {
        while (!cancelacion.IsCancellationRequested)
        {
            var linea = await LeerLineaLimitadaAsync(lector, cancelacion);
            if (linea is null)
            {
                return;
            }

            var comando = JsonSerializer.Deserialize<ComandoBrokerElevado>(linea, OpcionesJson);
            if (comando is null || !CompararTextoSeguro(comando.Token, tokenEsperado))
            {
                continue;
            }

            if (proceso.HasExited) return;
            if (string.Equals(comando.Tipo, "entrada", StringComparison.OrdinalIgnoreCase))
            {
                var texto = comando.Texto ?? "";
                if (texto.Length > 8192 || texto.Contains('\r') || texto.Contains('\n') || texto.Contains('\0'))
                    throw new InvalidDataException("Respuesta del broker no valida.");
                await proceso.StandardInput.WriteLineAsync(texto.AsMemory(), cancelacion);
                await proceso.StandardInput.FlushAsync(cancelacion);
            }
            else if (string.Equals(comando.Tipo, "cancelar", StringComparison.OrdinalIgnoreCase))
            {
                proceso.Kill(entireProcessTree: true);
            }
        }
        }
        finally
        {
            // Una conexion perdida o un mensaje invalido detiene tambien el proceso hijo.
            vida.Cancel();
            try { if (!proceso.HasExited) proceso.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task EnviarAsync(StreamWriter escritor, SemaphoreSlim bloqueoEnvio, EventoBrokerElevado evento)
    {
        await bloqueoEnvio.WaitAsync();
        try
        {
            await escritor.WriteLineAsync(JsonSerializer.Serialize(evento, OpcionesJson));
        }
        finally
        {
            bloqueoEnvio.Release();
        }
    }

    private static string LeerArgumento(string[] argumentos, string nombre)
    {
        for (var indice = 0; indice < argumentos.Length - 1; indice++)
        {
            if (string.Equals(argumentos[indice], nombre, StringComparison.OrdinalIgnoreCase))
            {
                return argumentos[indice + 1];
            }
        }

        return string.Empty;
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
}

public sealed record EventoBrokerElevado(
    string Tipo,
    string Mensaje,
    string? Color,
    bool Finalizado,
    int? CodigoSalida,
    string Resultado,
    string Detalle,
    ProgresoScript? Progreso = null,
    bool? EntradaProtegida = null)
{
    public static EventoBrokerElevado ErrorFinal(string mensaje, int? codigoSalida)
    {
        return new EventoBrokerElevado("error", mensaje, "#F44747", true, codigoSalida, "error", mensaje);
    }
}

public sealed record ComandoBrokerElevado(
    string Tipo,
    string Token,
    string ScriptId,
    string Nombre,
    string TipoScript,
    string RaizAutorizada,
    string RutaCompleta,
    bool PermitirExecutionPolicyBypass,
    string? Texto = null);
