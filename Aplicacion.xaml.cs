// (Autor: Alex Roman)
// Descripcion: Punto de entrada y control de instancia unica de la aplicacion WPF.

using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Windows;
using LanzadorScripts.Servicios;
using MessageBox = System.Windows.MessageBox;

namespace LanzadorScripts;

public partial class Aplicacion : System.Windows.Application
{
    private const string NombreMutex = "Local\\LanzadorScripts_AlexRoman";
    private const string NombrePipe = "LanzadorScripts_AlexRoman_ConfigPipe";
    private const int IntentosConexionInstancia = 20;
    private const int IntentosAprovisionamientoClave = 12;
    private static readonly TimeSpan IntervaloAprovisionamientoClave = TimeSpan.FromSeconds(15);

    private Mutex? _mutex;
    private CancellationTokenSource? _cancelacionPipe;
    private VentanaPrincipal? _ventanaPrincipal;
    private bool _instanciaPrincipal;
    private readonly ServicioLogInicio _servicioLogInicio = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        if (ServicioGeneracionPaqueteClaveArtefactos.EsSolicitud(e.Args))
        {
            Shutdown(ServicioGeneracionPaqueteClaveArtefactos.Ejecutar(e.Args));
            return;
        }

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
            if (!EnviarMensajesAInstanciaPrincipal(e.Args))
            {
                MessageBox.Show(
                    "La aplicacion ya esta iniciada, pero no respondio a la solicitud de mostrar la ventana.",
                    "LanzadorScripts",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        ResultadoAprovisionamientoClave resultadoAprovisionamiento;
        try
        {
            ServicioDirectoriosAplicacion.PrepararEstructuraAplicacion();
            resultadoAprovisionamiento = IntentarAprovisionarClaveArtefactos();
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
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ServicioAsociacionArchivos.Registrar();
        _cancelacionPipe = new CancellationTokenSource();
        _ventanaPrincipal = new VentanaPrincipal();
        _ventanaPrincipal.Show();
        _ = _servicioLogInicio.RegistrarAsync(
            "aplicacion.ventana_mostrada",
            "La ventana principal se mostro antes de iniciar los componentes pesados.");
        _ = EscucharArgumentosAsync(_cancelacionPipe.Token);
        _ = ReintentarAprovisionamientoClaveArtefactosAsync(
            resultadoAprovisionamiento,
            _cancelacionPipe.Token);
        ProcesarArgumentos(e.Args);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Permite cerrar sin bloquear el apagado de Windows.
        _ventanaPrincipal?.PrepararCierrePorSistema();
        base.OnSessionEnding(e);
    }

    private ResultadoAprovisionamientoClave IntentarAprovisionarClaveArtefactos()
    {
        // Intenta obtener la clave desde el paquete central firmado.
        try
        {
            var configuracion = new ServicioConfiguracion().Cargar();
            var resultado = new ServicioAprovisionamientoClaveArtefactos()
                .IntentarAprovisionar(configuracion.RutaPermisos);
            if (resultado.Estado == EstadoAprovisionamientoClave.YaDisponible)
            {
                return resultado;
            }

            _servicioLogInicio.RegistrarAsync(
                resultado.Estado is EstadoAprovisionamientoClave.Aprovisionada
                    or EstadoAprovisionamientoClave.Actualizada
                    ? "clave_artefactos.aprovisionada"
                    : "clave_artefactos.no_aprovisionada",
                resultado.Mensaje,
                new Dictionary<string, string?>
                {
                    ["estado"] = resultado.Estado.ToString(),
                    ["keyId"] = resultado.KeyId,
                    ["tipoError"] = resultado.TipoError,
                    ["detalleError"] = resultado.DetalleError
                }).GetAwaiter().GetResult();
            return resultado;
        }
        catch (Exception ex)
        {
            _servicioLogInicio.RegistrarExcepcionAsync(
                "clave_artefactos.aprovisionamiento_error",
                "arranque",
                string.Empty,
                ex).GetAwaiter().GetResult();
            return new ResultadoAprovisionamientoClave(
                EstadoAprovisionamientoClave.Error,
                "No se pudo completar el aprovisionamiento automatico de la clave.",
                TipoError: ex.GetType().Name,
                DetalleError: ServicioRedaccionSecretos.Sanitizar(ex.Message));
        }
    }

    private async Task ReintentarAprovisionamientoClaveArtefactosAsync(
        ResultadoAprovisionamientoClave resultadoInicial,
        CancellationToken cancelacion)
    {
        // Reintenta mientras la red o el paquete central aun no esten disponibles.
        if (!DebeReintentarAprovisionamiento(resultadoInicial))
        {
            return;
        }

        var ultimoResultado = resultadoInicial;
        for (var intento = 2; intento <= IntentosAprovisionamientoClave; intento++)
        {
            try
            {
                await Task.Delay(IntervaloAprovisionamientoClave, cancelacion);
                ultimoResultado = await Task.Run(IntentarAprovisionarClaveArtefactos, cancelacion);
            }
            catch (OperationCanceledException) when (cancelacion.IsCancellationRequested)
            {
                return;
            }

            if (DebeReintentarAprovisionamiento(ultimoResultado))
            {
                continue;
            }

            if (ClaveDisponible(ultimoResultado))
            {
                await Dispatcher.InvokeAsync(
                    () => _ventanaPrincipal?.ActualizarTrasAprovisionamientoClave());
            }

            return;
        }

        await _servicioLogInicio.RegistrarAsync(
            "clave_artefactos.reintentos_agotados",
            "No se pudo obtener la clave durante los reintentos automaticos.",
            new Dictionary<string, string?>
            {
                ["estado"] = ultimoResultado.Estado.ToString(),
                ["tipoError"] = ultimoResultado.TipoError,
                ["detalleError"] = ultimoResultado.DetalleError
            });
    }

    internal static bool DebeReintentarAprovisionamiento(ResultadoAprovisionamientoClave resultado)
    {
        // Solo reintenta estados que todavia pueden resolverse desde el servidor.
        return resultado.Estado is EstadoAprovisionamientoClave.PaqueteAusente
            or EstadoAprovisionamientoClave.Error;
    }

    internal static bool ClaveDisponible(ResultadoAprovisionamientoClave resultado)
    {
        // Identifica los resultados que permiten volver a cargar permisos y catalogo.
        return resultado.Estado is EstadoAprovisionamientoClave.YaDisponible
            or EstadoAprovisionamientoClave.Aprovisionada
            or EstadoAprovisionamientoClave.Actualizada;
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
                await using var pipe = new NamedPipeServerStream(
                    NombrePipe,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancelacion);
                using var lector = new StreamReader(pipe, Encoding.UTF8);
                var json = await LeerMensajeLimitadoAsync(lector, cancelacion);
                if (ProtocoloInstanciaAplicacion.IntentarDeserializar(json, out var mensaje)
                    && mensaje is not null)
                {
                    Dispatcher.Invoke(() => ProcesarMensaje(mensaje));
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

    private static bool EnviarMensajesAInstanciaPrincipal(string[] argumentos)
    {
        // Restaura siempre la instancia existente y despues entrega los paquetes.
        var mensajes = new List<MensajeInstanciaAplicacion>
        {
            new(AccionInstanciaAplicacion.Mostrar)
        };
        foreach (var argumento in argumentos.Where(EsPaqueteConfiguracionValido))
        {
            mensajes.Add(new MensajeInstanciaAplicacion(
                AccionInstanciaAplicacion.ImportarPaquete,
                argumento));
        }

        return mensajes.All(EnviarMensajeAInstanciaPrincipal);
    }

    private static bool EnviarMensajeAInstanciaPrincipal(MensajeInstanciaAplicacion mensaje)
    {
        // Reintenta durante el arranque temprano de la instancia principal.
        var json = ProtocoloInstanciaAplicacion.Serializar(mensaje);
        for (var intento = 0; intento < IntentosConexionInstancia; intento++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    NombrePipe,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    TokenImpersonationLevel.Identification,
                    HandleInheritability.None);
                pipe.Connect(250);
                using var escritor = new StreamWriter(pipe, Encoding.UTF8)
                {
                    AutoFlush = true
                };
                escritor.WriteLine(json);
                return true;
            }
            catch
            {
                Thread.Sleep(100);
            }
        }

        return false;
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
        _ventanaPrincipal?.MostrarDesdeInstanciaSecundaria();
    }

    private void ProcesarMensaje(MensajeInstanciaAplicacion mensaje)
    {
        // Ejecuta solamente acciones conocidas y vuelve a validar las rutas.
        switch (mensaje.Accion)
        {
            case AccionInstanciaAplicacion.Mostrar:
                _ventanaPrincipal?.MostrarDesdeInstanciaSecundaria();
                break;
            case AccionInstanciaAplicacion.ImportarPaquete
                when mensaje.Ruta is not null && EsPaqueteConfiguracionValido(mensaje.Ruta):
                ProcesarRutaPaquete(mensaje.Ruta);
                break;
        }
    }

    private static async Task<string> LeerMensajeLimitadoAsync(
        StreamReader lector,
        CancellationToken cancelacion)
    {
        // Lee una unica linea sin aceptar mensajes de tamano ilimitado.
        var resultado = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var leidos = await lector.ReadAsync(buffer.AsMemory(), cancelacion);
            if (leidos == 0)
            {
                return resultado.ToString();
            }

            for (var indice = 0; indice < leidos; indice++)
            {
                var caracter = buffer[indice];
                if (caracter == '\n')
                {
                    return resultado.ToString();
                }

                if (caracter != '\r')
                {
                    resultado.Append(caracter);
                }

                if (resultado.Length > ProtocoloInstanciaAplicacion.LongitudMaximaMensaje)
                {
                    throw new InvalidDataException("El mensaje entre instancias supera el limite permitido.");
                }
            }
        }
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
