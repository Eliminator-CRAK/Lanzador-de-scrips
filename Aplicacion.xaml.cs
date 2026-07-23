// (Autor: Alex Roman)
// Descripcion: Punto de entrada y control de instancia unica de la aplicacion WPF.

using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using LanzadorScripts.Servicios;

namespace LanzadorScripts;

public partial class Aplicacion : System.Windows.Application
{
    private const string NombreMutex = "Local\\LanzadorScripts_AlexRoman";
    private const string NombrePipe = "LanzadorScripts_AlexRoman_ConfigPipe";

    private Mutex? _mutex;
    private CancellationTokenSource? _cancelacionPipe;
    private VentanaPrincipal? _ventanaPrincipal;
    private bool _instanciaPrincipal;
    private readonly ServicioLogInicio _servicioLogInicio = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        if (ServicioGeneracionArtefactosIniciales.EsSolicitud(e.Args))
        {
            Shutdown(ServicioGeneracionArtefactosIniciales.Ejecutar(e.Args));
            return;
        }

        if (ServicioBrokerElevado.EsSolicitudBroker(e.Args))
        {
            Shutdown(ServicioBrokerElevado.EjecutarModoBroker(e.Args));
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, NombreMutex, out _instanciaPrincipal);
        if (!_instanciaPrincipal)
        {
            EnviarArgumentosAInstanciaPrincipal(e.Args);
            Shutdown();
            return;
        }

        try
        {
            ServicioDirectoriosAplicacion.PrepararEstructuraAplicacion();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No se pudo preparar la carpeta local segura de LanzadorScripts.\n\n{ex.Message}",
                "LanzadorScripts",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ServicioAsociacionArchivos.Registrar();
        _cancelacionPipe = new CancellationTokenSource();
        _ventanaPrincipal = new VentanaPrincipal();
        _ventanaPrincipal.Show();
        _ = EscucharArgumentosAsync(_cancelacionPipe.Token);
        ProcesarArgumentos(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cancelacionPipe?.Cancel();
        _cancelacionPipe?.Dispose();
        if (_instanciaPrincipal)
        {
            _mutex?.ReleaseMutex();
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }

    private async Task EscucharArgumentosAsync(CancellationToken cancelacion)
    {
        while (!cancelacion.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(NombrePipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancelacion);
                using var lector = new StreamReader(pipe, Encoding.UTF8);
                var ruta = await lector.ReadLineAsync(cancelacion);
                if (!string.IsNullOrWhiteSpace(ruta) && EsPaqueteConfiguracionValido(ruta))
                {
                    Dispatcher.Invoke(() => ProcesarRutaPaquete(ruta));
                }
            }
            catch when (cancelacion.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await _servicioLogInicio.RegistrarAsync("pipe.argumentos.error", "No se pudo procesar el pipe local de argumentos.");
            }
        }
    }

    private static void EnviarArgumentosAInstanciaPrincipal(string[] argumentos)
    {
        foreach (var argumento in argumentos.Where(EsPaqueteConfiguracionValido))
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", NombrePipe, PipeDirection.Out);
                pipe.Connect(1500);
                using var escritor = new StreamWriter(pipe, Encoding.UTF8)
                {
                    AutoFlush = true
                };
                escritor.WriteLine(argumento);
            }
            catch
            {
            }
        }
    }

    private void ProcesarArgumentos(string[] argumentos)
    {
        foreach (var argumento in argumentos.Where(EsPaqueteConfiguracionValido))
        {
            ProcesarRutaPaquete(argumento);
        }
    }

    private void ProcesarRutaPaquete(string ruta)
    {
        _ventanaPrincipal?.ImportarPaqueteConfiguracion(ruta);
        _ventanaPrincipal?.Activate();
    }

    private static bool EsPaqueteConfiguracion(string ruta)
    {
        return string.Equals(Path.GetExtension(ruta), ServicioPaquetesConfiguracion.ExtensionPaquete, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EsPaqueteConfiguracionValido(string ruta)
    {
        return EsPaqueteConfiguracion(ruta)
            && ServicioPaquetesConfiguracion.EsRutaImportacionValida(ruta);
    }
}
