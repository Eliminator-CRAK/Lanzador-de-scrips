// (Autor: Alex Roman)
// Descripcion: Gestiona la cola y ejecucion paralela de scripts.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using LanzadorScripts.Modelos;
using LanzadorScripts.ModelosVista;

namespace LanzadorScripts.Servicios;

public sealed class GestorEjecucionScripts
{
    private sealed class EjecucionActiva(Process proceso, CancellationTokenSource cancelacion)
    {
        public Process Proceso { get; } = proceso;
        public CancellationTokenSource Cancelacion { get; } = cancelacion;
    }

    private readonly Dispatcher _despachador;
    private readonly Queue<ModeloEjecucionScript> _ejecucionesPendientes = new();
    private readonly ConcurrentDictionary<Guid, EjecucionActiva> _ejecucionesActivas = new();
    private readonly object _bloqueo = new();
    private string _rutaLogs;
    private int _maximoEjecucionesParalelas;
    private int _huecosActivos;

    public GestorEjecucionScripts(Dispatcher despachador, string rutaLogs, int maximoEjecucionesParalelas)
    {
        _despachador = despachador;
        _rutaLogs = rutaLogs;
        _maximoEjecucionesParalelas = Math.Clamp(maximoEjecucionesParalelas, 1, 50);
    }

    public event EventHandler? RecuentosCambiados;

    public int RecuentoEjecucionesActivas
    {
        get
        {
            lock (_bloqueo)
            {
                return _huecosActivos;
            }
        }
    }

    public int RecuentoEjecucionesPendientes
    {
        get
        {
            lock (_bloqueo)
            {
                return _ejecucionesPendientes.Count(ejecucion => ejecucion.Estado == EstadoEjecucion.Pendiente);
            }
        }
    }

    public void ActualizarConfiguracion(string rutaLogs, int maximoEjecucionesParalelas)
    {
        _rutaLogs = rutaLogs;
        _maximoEjecucionesParalelas = Math.Clamp(maximoEjecucionesParalelas, 1, 50);
        IniciarSiguientesEjecucionesPendientes();
        NotificarRecuentosCambiados();
    }

    public void Encolar(ModeloEjecucionScript ejecucion)
    {
        lock (_bloqueo)
        {
            ejecucion.Estado = EstadoEjecucion.Pendiente;
            _ejecucionesPendientes.Enqueue(ejecucion);
        }

        IniciarSiguientesEjecucionesPendientes();
        NotificarRecuentosCambiados();
    }

    public async Task EnviarEntradaAsync(ModeloEjecucionScript ejecucion, string texto)
    {
        if (!_ejecucionesActivas.TryGetValue(ejecucion.Id, out var ejecucionActiva))
        {
            return;
        }

        try
        {
            await ejecucionActiva.Proceso.StandardInput.WriteLineAsync(texto);
            await ejecucionActiva.Proceso.StandardInput.FlushAsync();
            AgregarSalida(ejecucion, $"{Environment.NewLine}> {texto}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            AgregarSalida(ejecucion, $"{Environment.NewLine}[ERROR] No se pudo enviar entrada: {ex.Message}{Environment.NewLine}");
        }
    }

    public void Cancelar(ModeloEjecucionScript ejecucion)
    {
        if (ejecucion.Estado == EstadoEjecucion.Pendiente)
        {
            ejecucion.Estado = EstadoEjecucion.Cancelada;
            ejecucion.Fin = DateTime.Now;
            AgregarSalida(ejecucion, $"{Environment.NewLine}[CANCELADO] Ejecucion pendiente cancelada por el usuario.{Environment.NewLine}");
            IniciarSiguientesEjecucionesPendientes();
            NotificarRecuentosCambiados();
            return;
        }

        if (!_ejecucionesActivas.TryGetValue(ejecucion.Id, out var ejecucionActiva))
        {
            return;
        }

        try
        {
            ejecucionActiva.Cancelacion.Cancel();
            if (!ejecucionActiva.Proceso.HasExited)
            {
                ejecucionActiva.Proceso.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            AgregarSalida(ejecucion, $"{Environment.NewLine}[ERROR] No se pudo cancelar: {ex.Message}{Environment.NewLine}");
        }
    }

    private void IniciarSiguientesEjecucionesPendientes()
    {
        while (true)
        {
            ModeloEjecucionScript? siguienteEjecucion = null;

            lock (_bloqueo)
            {
                if (_huecosActivos >= _maximoEjecucionesParalelas)
                {
                    break;
                }

                while (_ejecucionesPendientes.Count > 0)
                {
                    var candidata = _ejecucionesPendientes.Dequeue();
                    if (candidata.Estado == EstadoEjecucion.Pendiente)
                    {
                        siguienteEjecucion = candidata;
                        _huecosActivos++;
                        break;
                    }
                }
            }

            if (siguienteEjecucion is null)
            {
                break;
            }

            _ = EjecutarAsync(siguienteEjecucion);
        }
    }

    private async Task EjecutarAsync(ModeloEjecucionScript ejecucion)
    {
        var cancelacion = new CancellationTokenSource();

        try
        {
            Directory.CreateDirectory(_rutaLogs);

            ejecucion.Inicio = DateTime.Now;
            ejecucion.Estado = EstadoEjecucion.Ejecutando;
            ejecucion.RutaLog = ConstruirRutaLog(ejecucion);
            AgregarSalida(ejecucion, $"[INFO] Script: {ejecucion.Script.Nombre}{Environment.NewLine}");
            AgregarSalida(ejecucion, $"[INFO] Log preparado para esta ejecucion.{Environment.NewLine}");

            using var escritorLog = new StreamWriter(ejecucion.RutaLog, append: false, Encoding.UTF8)
            {
                AutoFlush = true
            };
            using var bloqueoLog = new SemaphoreSlim(1, 1);

            await escritorLog.WriteLineAsync($"Script: {ejecucion.Script.Nombre}");
            await escritorLog.WriteLineAsync($"Inicio: {ejecucion.Inicio:yyyy-MM-dd HH:mm:ss}");
            await escritorLog.WriteLineAsync();

            using var proceso = CrearProcesoPowerShell(ejecucion.Script.RutaCompleta);
            var ejecucionActiva = new EjecucionActiva(proceso, cancelacion);
            _ejecucionesActivas[ejecucion.Id] = ejecucionActiva;
            NotificarRecuentosCambiados();

            proceso.Start();

            var tareaSalida = LeerSalidaAsync(proceso.StandardOutput, ejecucion, escritorLog, bloqueoLog, cancelacion.Token);
            var tareaError = LeerSalidaAsync(proceso.StandardError, ejecucion, escritorLog, bloqueoLog, cancelacion.Token);

            await proceso.WaitForExitAsync(cancelacion.Token);
            await Task.WhenAll(tareaSalida, tareaError);

            ejecucion.CodigoSalida = proceso.ExitCode;
            ejecucion.Fin = DateTime.Now;
            ejecucion.Estado = proceso.ExitCode == 0 ? EstadoEjecucion.Finalizada : EstadoEjecucion.Error;

            await escritorLog.WriteLineAsync();
            await escritorLog.WriteLineAsync($"Fin: {ejecucion.Fin:yyyy-MM-dd HH:mm:ss}");
            await escritorLog.WriteLineAsync($"Codigo de salida: {ejecucion.CodigoSalida}");

            if (ejecucion.Estado == EstadoEjecucion.Error)
            {
                AgregarSalida(ejecucion, $"{Environment.NewLine}[ERROR] El script termino con codigo {ejecucion.CodigoSalida}.{Environment.NewLine}");
            }
            else
            {
                AgregarSalida(ejecucion, $"{Environment.NewLine}[INFO] Codigo de salida: {ejecucion.CodigoSalida}{Environment.NewLine}");
            }
        }
        catch (OperationCanceledException)
        {
            ejecucion.Estado = EstadoEjecucion.Cancelada;
            ejecucion.Fin = DateTime.Now;
            var mensaje = $"{Environment.NewLine}[CANCELADO] Ejecucion cancelada por el usuario.{Environment.NewLine}";
            AgregarSalida(ejecucion, mensaje);
            IntentarAgregarAlLog(ejecucion, mensaje);
        }
        catch (Exception ex)
        {
            ejecucion.Estado = EstadoEjecucion.Error;
            ejecucion.Fin = DateTime.Now;
            var mensaje = $"{Environment.NewLine}[ERROR] {ex.Message}{Environment.NewLine}";
            AgregarSalida(ejecucion, mensaje);
            IntentarAgregarAlLog(ejecucion, mensaje);
        }
        finally
        {
            _ejecucionesActivas.TryRemove(ejecucion.Id, out _);
            cancelacion.Dispose();

            lock (_bloqueo)
            {
                if (_huecosActivos > 0)
                {
                    _huecosActivos--;
                }
            }

            IniciarSiguientesEjecucionesPendientes();
            NotificarRecuentosCambiados();
        }
    }

    private static Process CrearProcesoPowerShell(string rutaScript)
    {
        var rutaPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        if (!File.Exists(rutaPowerShell))
        {
            rutaPowerShell = "powershell.exe";
        }

        var informacionInicio = new ProcessStartInfo
        {
            FileName = rutaPowerShell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.Default,
            StandardErrorEncoding = Encoding.Default,
            WorkingDirectory = Path.GetDirectoryName(rutaScript) ?? Environment.CurrentDirectory
        };

        informacionInicio.ArgumentList.Add("-NoLogo");
        informacionInicio.ArgumentList.Add("-NoProfile");
        informacionInicio.ArgumentList.Add("-File");
        informacionInicio.ArgumentList.Add(rutaScript);

        return new Process { StartInfo = informacionInicio, EnableRaisingEvents = true };
    }

    private async Task LeerSalidaAsync(
        StreamReader lector,
        ModeloEjecucionScript ejecucion,
        StreamWriter escritorLog,
        SemaphoreSlim bloqueoLog,
        CancellationToken cancelacion)
    {
        var buffer = new char[1024];

        while (!cancelacion.IsCancellationRequested)
        {
            var leidos = await lector.ReadAsync(buffer.AsMemory(0, buffer.Length), cancelacion);
            if (leidos == 0)
            {
                break;
            }

            var texto = OcultarRutasInternas(ejecucion, new string(buffer, 0, leidos));
            AgregarSalida(ejecucion, texto);
            await bloqueoLog.WaitAsync(cancelacion);
            try
            {
                await escritorLog.WriteAsync(texto);
            }
            finally
            {
                bloqueoLog.Release();
            }
        }
    }

    private void AgregarSalida(ModeloEjecucionScript ejecucion, string texto)
    {
        texto = OcultarRutasInternas(ejecucion, texto);

        if (_despachador.CheckAccess())
        {
            ejecucion.AgregarSalida(texto);
            return;
        }

        _despachador.Invoke(() => ejecucion.AgregarSalida(texto));
    }

    private string ConstruirRutaLog(ModeloEjecucionScript ejecucion)
    {
        var carpetaDia = Path.Combine(_rutaLogs, DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(carpetaDia);

        var caracteresInvalidos = Path.GetInvalidFileNameChars();
        var nombreSeguro = string.Concat(ejecucion.Script.Nombre.Select(caracter =>
            caracteresInvalidos.Contains(caracter) ? '_' : caracter));

        return Path.Combine(carpetaDia, $"{DateTime.Now:HHmmss}_{nombreSeguro}_{ejecucion.Id:N}.log");
    }

    private static string OcultarRutasInternas(ModeloEjecucionScript ejecucion, string texto)
    {
        var rutaScript = ejecucion.Script.RutaCompleta;
        var carpetaScript = Path.GetDirectoryName(rutaScript);

        if (!string.IsNullOrWhiteSpace(carpetaScript))
        {
            texto = texto.Replace(carpetaScript, "[origen protegido]", StringComparison.OrdinalIgnoreCase);
        }

        return ServicioRedaccionSecretos.Sanitizar(
            texto.Replace(rutaScript, "[script protegido]", StringComparison.OrdinalIgnoreCase));
    }

    private static void IntentarAgregarAlLog(ModeloEjecucionScript ejecucion, string texto)
    {
        if (string.IsNullOrWhiteSpace(ejecucion.RutaLog))
        {
            return;
        }

        try
        {
            texto = OcultarRutasInternas(ejecucion, texto);
            File.AppendAllText(ejecucion.RutaLog, texto, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private void NotificarRecuentosCambiados()
    {
        if (_despachador.CheckAccess())
        {
            RecuentosCambiados?.Invoke(this, EventArgs.Empty);
            return;
        }

        _despachador.Invoke(() => RecuentosCambiados?.Invoke(this, EventArgs.Empty));
    }
}
