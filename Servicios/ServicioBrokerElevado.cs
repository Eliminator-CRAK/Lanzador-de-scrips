// (Autor: Alex Roman)
// Descripcion: Ejecuta scripts elevados mediante un broker minimo y autenticado.

using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        ScriptInterno script,
        bool permitirExecutionPolicyBypass,
        [EnumeratorCancellation] CancellationToken cancelacion)
    {
        if (!EstaDisponible())
        {
            yield return EventoBrokerElevado.ErrorFinal("Broker elevado no disponible en este equipo.", null);
            yield break;
        }

        var nombrePipe = $"LanzadorScriptsBroker_{Guid.NewGuid():N}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var pipe = CrearServidorPipe(nombrePipe);
        using var procesoBroker = IniciarBroker(nombrePipe, token);

        try
        {
            await pipe.WaitForConnectionAsync(cancelacion);
            await using var escritorFlujo = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
            using var lector = new StreamReader(pipe, Encoding.UTF8);
            var comando = new ComandoBrokerElevado(
                "ejecutar",
                token,
                script.Id,
                script.Nombre,
                script.Tipo,
                script.RutaCompleta,
                permitirExecutionPolicyBypass);

            await escritorFlujo.WriteLineAsync(JsonSerializer.Serialize(comando, OpcionesJson));
            using var registroCancelacion = cancelacion.Register(() =>
            {
                try
                {
                    var cancelacionBroker = new ComandoBrokerElevado("cancelar", token, string.Empty, string.Empty, string.Empty, string.Empty, false);
                    escritorFlujo.WriteLine(JsonSerializer.Serialize(cancelacionBroker, OpcionesJson));
                }
                catch
                {
                }
            });

            while (!cancelacion.IsCancellationRequested)
            {
                var linea = await lector.ReadLineAsync(cancelacion);
                if (linea is null)
                {
                    break;
                }

                var evento = JsonSerializer.Deserialize<EventoBrokerElevado>(linea, OpcionesJson);
                if (evento is null)
                {
                    continue;
                }

                yield return evento;
                if (evento.Finalizado)
                {
                    break;
                }
            }
        }
        finally
        {
            try
            {
                if (!procesoBroker.HasExited)
                {
                    procesoBroker.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task<int> EjecutarBrokerAsync(string nombrePipe, string tokenEsperado)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            nombrePipe,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await pipe.ConnectAsync(15000);
        await using var escritorFlujo = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
        using var lector = new StreamReader(pipe, Encoding.UTF8);
        using var bloqueoEnvio = new SemaphoreSlim(1, 1);
        var primeraLinea = await lector.ReadLineAsync();
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

        var script = new ScriptInterno(comando.ScriptId, comando.Nombre, comando.TipoScript, comando.RutaCompleta);
        using var proceso = CrearProceso(script, comando.PermitirExecutionPolicyBypass);
        using var cancelacionProceso = new CancellationTokenSource();
        var lectorComandos = EscucharCancelacionAsync(lector, tokenEsperado, proceso, cancelacionProceso.Token);

        try
        {
            proceso.Start();
            var salida = LeerFlujoAsync(proceso.StandardOutput, escritorFlujo, bloqueoEnvio, "info", null, cancelacionProceso.Token);
            var error = LeerFlujoAsync(proceso.StandardError, escritorFlujo, bloqueoEnvio, "error", "#F44747", cancelacionProceso.Token);
            await proceso.WaitForExitAsync(cancelacionProceso.Token);
            await Task.WhenAll(salida, error);

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
            inicio.FileName = ObtenerRutaPowerShell();
            inicio.ArgumentList.Add("-NoLogo");
            inicio.ArgumentList.Add("-NoProfile");
            if (permitirExecutionPolicyBypass)
            {
                inicio.ArgumentList.Add("-ExecutionPolicy");
                inicio.ArgumentList.Add("Bypass");
            }

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

    private static async Task LeerFlujoAsync(StreamReader lector, StreamWriter escritor, SemaphoreSlim bloqueoEnvio, string tipo, string? color, CancellationToken cancelacion)
    {
        var buffer = new char[512];
        int leidos;
        while ((leidos = await lector.ReadAsync(buffer.AsMemory(0, buffer.Length), cancelacion)) > 0)
        {
            await EnviarAsync(escritor, bloqueoEnvio, new EventoBrokerElevado(tipo, new string(buffer, 0, leidos), color, false, null, string.Empty, string.Empty));
        }
    }

    private static async Task EscucharCancelacionAsync(StreamReader lector, string tokenEsperado, Process proceso, CancellationToken cancelacion)
    {
        while (!cancelacion.IsCancellationRequested)
        {
            var linea = await lector.ReadLineAsync(cancelacion);
            if (linea is null)
            {
                return;
            }

            var comando = JsonSerializer.Deserialize<ComandoBrokerElevado>(linea, OpcionesJson);
            if (comando is null
                || !string.Equals(comando.Tipo, "cancelar", StringComparison.OrdinalIgnoreCase)
                || !CompararTextoSeguro(comando.Token, tokenEsperado))
            {
                continue;
            }

            if (!proceso.HasExited)
            {
                proceso.Kill(entireProcessTree: true);
            }
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

    private static string CrearComandoPowerShell(string rutaScript)
    {
        var rutaEscapada = rutaScript.Replace("'", "''");
        return "$ErrorActionPreference='Continue'; & '" + rutaEscapada + "' *>&1; exit $LASTEXITCODE";
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
    string Detalle)
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
    string RutaCompleta,
    bool PermitirExecutionPolicyBypass);
