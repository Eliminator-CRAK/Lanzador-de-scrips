// (Autor: Alex Roman)
// Descripcion: Inicializa el cliente web y su backend local.

using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using LanzadorScripts.Servicios;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using Panel = System.Windows.Controls.Panel;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LanzadorScripts;

public partial class VentanaPrincipal : Window
{
    internal const int CodigoSalidaCierreDefinitivo = 42;

    private const int WmNcHitTest = 0x0084;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const double GrosorRedimensionVentana = 10;

    private readonly ServicioArranqueWebView2 _servicioArranqueWebView2 = new();
    private readonly ServicioLogInicio _servicioLogInicio = new();
    private readonly ServicioExecutionPolicy _servicioExecutionPolicy = new();
    private readonly Stopwatch _cronometroNavegacion = new();
    private readonly ServicioIconoBandeja _servicioIconoBandeja;
    private ServidorLocalWeb? _servidorLocalIntegrado;
    private EndpointServicioLanzador? _endpointServicio;
    private CoreWebView2? _webViewConfigurada;
    private ulong _idNavegacionActual;
    private string _origenNavegacionActual = string.Empty;
    private WindowState _estadoAntesOcultar = WindowState.Normal;
    private bool _inicioProgramado;
    private bool _cargaClienteEnCurso;
    private bool _cierreDefinitivo;
    private bool _cierreEnCurso;
    private bool _confirmacionCierreAbierta;
    private int _recursosLiberados;

    public VentanaPrincipal()
    {
        InitializeComponent();
        _servicioIconoBandeja = new ServicioIconoBandeja(
            Dispatcher,
            RestaurarDesdeBandeja,
            MaximizarDesdeBandeja,
            MinimizarDesdeBandeja,
            SolicitarCierreDesdeBandeja);
        _ = _servicioLogInicio.RegistrarAsync(
            "aplicacion.bandeja_lista",
            "El icono de la bandeja quedo disponible.");
    }

    protected override void OnContentRendered(EventArgs e)
    {
        // Inicia los componentes pesados cuando la ventana ya es visible.
        base.OnContentRendered(e);
        if (_inicioProgramado)
        {
            return;
        }

        _inicioProgramado = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(CargarClienteAsync));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // El cierre normal mantiene la aplicacion en segundo plano.
        if (!_cierreDefinitivo)
        {
            e.Cancel = true;
            OcultarEnSegundoPlano();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Libera cualquier recurso restante en un cierre definitivo.
        LiberarRecursosSincronos();
        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AplicarEstiloNativoVentana();
        InstalarRedimensionNativo();
    }

    private async void CargarClienteAsync()
    {
        if (_cargaClienteEnCurso || _cierreEnCurso)
        {
            return;
        }

        _cargaClienteEnCurso = true;
        try
        {
            PanelArranque.Visibility = Visibility.Visible;
            PanelArranque.IsHitTestVisible = true;
            BotonReintentarArranque.Visibility = Visibility.Collapsed;
            TextoArranque.Text = "Iniciando backend local...";
            var endpoint = await ObtenerEndpointBackendAsync();
            _endpointServicio = endpoint;

            TextoArranque.Text = "Preparando WebView2...";
            var arranque = await _servicioArranqueWebView2.PrepararAsync(() => VistaCliente, RecrearVistaCliente);
            if (!arranque.Exito)
            {
                PanelArranque.IsHitTestVisible = true;
                TextoArranque.Text = arranque.Mensaje;
                BotonReintentarArranque.Visibility = Visibility.Visible;
                MessageBox.Show(
                    arranque.Mensaje,
                    "No se pudo preparar WebView2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            TextoArranque.Text = "Aplicando protecciones del cliente local...";
            var coreWebView2 = VistaCliente.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 no termino de inicializarse.");
            if (!ReferenceEquals(_webViewConfigurada, coreWebView2))
            {
                coreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                coreWebView2.Settings.AreDevToolsEnabled = false;
                await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerDiagnosticoErroresCliente());
                await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerProteccionApiLocal(endpoint.TokenApiInterno));
                await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerProteccionTokenLocalStorage());
                await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerPanelDiagnosticoEjecucion());
                await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerMejorasInterfazScripts());
                await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerPanelPermisosSubcarpetas());
                await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerAvisosConfiguracionApp());
                await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerExportacionConfiguracionGestionada());
                await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerAtajoTokenMaestro());
                await coreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerEstadoCargaAjustes());
                _webViewConfigurada = coreWebView2;
            }

            coreWebView2.WebMessageReceived -= VistaCliente_WebMessageReceived;
            coreWebView2.WebMessageReceived += VistaCliente_WebMessageReceived;
            TextoArranque.Text = "Cargando cliente web local...";
            VistaCliente.NavigationStarting -= VistaCliente_NavigationStarting;
            VistaCliente.NavigationStarting += VistaCliente_NavigationStarting;
            VistaCliente.NavigationCompleted -= VistaCliente_NavigationCompleted;
            VistaCliente.NavigationCompleted += VistaCliente_NavigationCompleted;
            VistaCliente.Source = new Uri(endpoint.UrlBase);
        }
        catch (Exception ex)
        {
            PanelArranque.Visibility = Visibility.Visible;
            PanelArranque.IsHitTestVisible = true;
            TextoArranque.Text = $"No se pudo iniciar el cliente local. Logs: {RutasAplicacion.RutaLogsUsuario}";
            BotonReintentarArranque.Visibility = Visibility.Visible;
            await _servicioLogInicio.RegistrarAsync(
                "cliente.arranque_error",
                ServicioRedaccionSecretos.Sanitizar(ex.Message),
                new Dictionary<string, string?>
                {
                    ["tipoExcepcion"] = ex.GetType().Name
                });
        }
        finally
        {
            _cargaClienteEnCurso = false;
        }
    }

    private async Task<EndpointServicioLanzador> ObtenerEndpointBackendAsync()
    {
        if (_servidorLocalIntegrado is null)
        {
            _servidorLocalIntegrado = ServidorLocalWeb.Iniciar();
            await _servicioLogInicio.RegistrarAsync(
                "cliente.backend_integrado",
                "Backend local integrado iniciado.");
        }

        return new EndpointServicioLanzador(
            _servidorLocalIntegrado.UrlBase.ToString(),
            _servidorLocalIntegrado.TokenApiInterno);
    }

    private void VistaCliente_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Registra la navegacion principal que debe habilitar la interfaz.
        _idNavegacionActual = e.NavigationId;
        _origenNavegacionActual = e.Uri ?? string.Empty;
        _cronometroNavegacion.Restart();
        PanelArranque.Visibility = Visibility.Visible;
        PanelArranque.IsHitTestVisible = true;
        _ = _servicioLogInicio.RegistrarAsync(
            "cliente.navegacion.inicio",
            "WebView2 inicio la navegacion del cliente local.",
            new Dictionary<string, string?>
            {
                ["navigationId"] = e.NavigationId.ToString(),
                ["origen"] = _origenNavegacionActual
            });
    }

    private async void VistaCliente_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _cronometroNavegacion.Stop();
        var navegacionActual = e.NavigationId == _idNavegacionActual;
        var origenEsperado = EsOrigenBackendEsperado(_origenNavegacionActual);
        var datos = new Dictionary<string, string?>
        {
            ["navigationId"] = e.NavigationId.ToString(),
            ["navegacionActual"] = navegacionActual.ToString(),
            ["origenEsperado"] = origenEsperado.ToString(),
            ["origen"] = _origenNavegacionActual,
            ["webErrorStatus"] = e.WebErrorStatus.ToString(),
            ["duracionMs"] = _cronometroNavegacion.ElapsedMilliseconds.ToString()
        };

        if (e.IsSuccess && navegacionActual && origenEsperado)
        {
            PanelArranque.IsHitTestVisible = false;
            PanelArranque.Visibility = Visibility.Collapsed;
            BotonReintentarArranque.Visibility = Visibility.Collapsed;
            await _servicioLogInicio.RegistrarAsync(
                "cliente.navegacion.correcta",
                "El cliente local quedo disponible.",
                datos);
            return;
        }

        if (!navegacionActual)
        {
            await _servicioLogInicio.RegistrarAsync(
                "cliente.navegacion.obsoleta",
                "Se ignoro el resultado de una navegacion anterior.",
                datos);
            return;
        }

        PanelArranque.Visibility = Visibility.Visible;
        PanelArranque.IsHitTestVisible = true;
        TextoArranque.Text = $"No se pudo cargar el cliente local. Logs: {RutasAplicacion.RutaLogsUsuario}";
        BotonReintentarArranque.Visibility = Visibility.Visible;
        await _servicioLogInicio.RegistrarAsync(
            "cliente.navegacion.error",
            "WebView2 no pudo cargar el cliente local.",
            datos);
    }

    private bool EsOrigenBackendEsperado(string origen)
    {
        // Verifica que la navegacion pertenece al backend local activo.
        if (_endpointServicio is null
            || !Uri.TryCreate(_endpointServicio.UrlBase, UriKind.Absolute, out var esperado)
            || !Uri.TryCreate(origen, UriKind.Absolute, out var actual))
        {
            return false;
        }

        return esperado.Scheme.Equals(actual.Scheme, StringComparison.OrdinalIgnoreCase)
            && esperado.Host.Equals(actual.Host, StringComparison.OrdinalIgnoreCase)
            && esperado.Port == actual.Port;
    }

    private void BotonReintentarArranque_Click(object sender, RoutedEventArgs e)
    {
        CargarClienteAsync();
    }

    private WebView2 RecrearVistaCliente()
    {
        // Reemplaza el control cuando WebView2 queda en estado fallido.
        var vistaAnterior = VistaCliente;
        var vistaNueva = new WebView2();
        var contenedor = vistaAnterior.Parent as Panel
            ?? throw new InvalidOperationException("No se encontro el contenedor de WebView2.");

        var indice = contenedor.Children.IndexOf(vistaAnterior);
        contenedor.Children.Remove(vistaAnterior);
        contenedor.Children.Insert(indice < 0 ? 0 : indice, vistaNueva);
        VistaCliente = vistaNueva;

        try
        {
            vistaAnterior.Dispose();
        }
        catch
        {
        }

        return vistaNueva;
    }

    private void BarraTitulo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            AlternarMaximizado();
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private void BotonMinimizar_Click(object sender, RoutedEventArgs e)
    {
        MinimizarDesdeBandeja();
    }

    private void BotonMaximizar_Click(object sender, RoutedEventArgs e)
    {
        AlternarMaximizado();
    }

    private void BotonCerrar_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public void MostrarDesdeInstanciaSecundaria()
    {
        // Restaura una ventana oculta o minimizada por una segunda ejecucion.
        MostrarVentana(WindowState.Normal);
    }

    public void PrepararCierrePorSistema()
    {
        // No bloquea el cierre de sesion de Windows con dialogos.
        _cierreDefinitivo = true;
        _cierreEnCurso = true;
        LiberarRecursosSincronos();
    }

    private void RestaurarDesdeBandeja()
    {
        // Recupera el estado anterior cuando sea util para el usuario.
        var estado = _estadoAntesOcultar == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        MostrarVentana(estado);
    }

    private void MaximizarDesdeBandeja()
    {
        // Muestra la ventana directamente maximizada.
        MostrarVentana(WindowState.Maximized);
    }

    private void MinimizarDesdeBandeja()
    {
        // Conserva la entrada de la barra de tareas y el icono de bandeja.
        ShowInTaskbar = true;
        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Minimized;
    }

    private void OcultarEnSegundoPlano()
    {
        // Oculta la ventana sin detener scripts ni backend.
        if (WindowState != WindowState.Minimized)
        {
            _estadoAntesOcultar = WindowState;
        }

        ShowInTaskbar = false;
        Hide();
        _ = _servicioLogInicio.RegistrarAsync(
            "aplicacion.segundo_plano",
            "La ventana se oculto y la aplicacion sigue disponible en la bandeja.");
    }

    private void MostrarVentana(WindowState estado)
    {
        // Hace visible y activa la ventana desde cualquier estado.
        ShowInTaskbar = true;
        if (!IsVisible)
        {
            Show();
        }

        WindowState = estado;
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(hwnd);
        }
    }

    private async void SolicitarCierreDesdeBandeja()
    {
        // Confirma el cierre solo cuando hay scripts que se cancelaran.
        if (_cierreEnCurso || _confirmacionCierreAbierta)
        {
            return;
        }

        _confirmacionCierreAbierta = true;
        IReadOnlyList<EjecucionActivaResumen> ejecuciones;
        try
        {
            ejecuciones = _servidorLocalIntegrado?.ObtenerEjecucionesActivas()
                ?? Array.Empty<EjecucionActivaResumen>();
            while (ejecuciones.Count > 0)
            {
                var dialogo = new DialogoCerrarAplicacion(ejecuciones);
                if (IsVisible && WindowState != WindowState.Minimized)
                {
                    dialogo.Owner = this;
                    dialogo.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }

                if (dialogo.ShowDialog() != true)
                {
                    return;
                }

                var actuales = _servidorLocalIntegrado?.ObtenerEjecucionesActivas()
                    ?? Array.Empty<EjecucionActivaResumen>();
                if (ejecuciones.Select(ejecucion => ejecucion.Id)
                    .SequenceEqual(actuales.Select(ejecucion => ejecucion.Id)))
                {
                    break;
                }

                ejecuciones = actuales;
            }
        }
        finally
        {
            _confirmacionCierreAbierta = false;
        }

        _cierreEnCurso = true;
        _cierreDefinitivo = true;
        MostrarVentana(WindowState.Normal);
        PanelCierre.Visibility = Visibility.Visible;
        PanelCierre.IsHitTestVisible = true;
        await Dispatcher.Yield(DispatcherPriority.Render);
        await _servicioLogInicio.RegistrarAsync(
            "aplicacion.cierre_confirmado",
            "Se inicio el cierre definitivo desde la bandeja.",
            new Dictionary<string, string?>
            {
                ["ejecucionesActivas"] = ejecuciones.Count.ToString()
            });
        try
        {
            await LiberarRecursosAsync();
        }
        catch (Exception ex)
        {
            await _servicioLogInicio.RegistrarExcepcionAsync(
                "aplicacion.cierre_liberacion_error",
                "cierre",
                string.Empty,
                ex);
        }
        finally
        {
            System.Windows.Application.Current.Shutdown(CodigoSalidaCierreDefinitivo);
        }
    }

    private async Task LiberarRecursosAsync()
    {
        // Detiene el backend fuera del hilo visual y libera WebView2.
        if (Interlocked.Exchange(ref _recursosLiberados, 1) != 0)
        {
            return;
        }

        var servidor = _servidorLocalIntegrado;
        _servidorLocalIntegrado = null;
        Exception? errorLiberacion = null;
        try
        {
            if (servidor is not null)
            {
                await Task.Run(servidor.Dispose);
            }
        }
        catch (Exception ex)
        {
            errorLiberacion = ex;
        }

        try
        {
            VistaCliente.Dispose();
        }
        catch (Exception ex)
        {
            errorLiberacion ??= ex;
        }

        try
        {
            _servicioIconoBandeja.Dispose();
        }
        catch (Exception ex)
        {
            errorLiberacion ??= ex;
        }

        if (errorLiberacion is not null)
        {
            throw new InvalidOperationException(
                "No se pudieron liberar todos los recursos de la aplicacion.",
                errorLiberacion);
        }
    }

    private void LiberarRecursosSincronos()
    {
        // Aplica la misma limpieza cuando no se puede esperar de forma asincrona.
        if (Interlocked.Exchange(ref _recursosLiberados, 1) != 0)
        {
            return;
        }

        var servidor = _servidorLocalIntegrado;
        _servidorLocalIntegrado = null;
        try
        {
            servidor?.Dispose();
        }
        catch
        {
        }

        try
        {
            VistaCliente.Dispose();
        }
        catch
        {
        }

        try
        {
            _servicioIconoBandeja.Dispose();
        }
        catch
        {
        }
    }

    private void AlternarMaximizado()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void AplicarEstiloNativoVentana()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var modoOscuro = 1;
        DwmSetWindowAttribute(hwnd, 20, ref modoOscuro, sizeof(int));

        var esquinasRedondeadas = 2;
        DwmSetWindowAttribute(hwnd, 33, ref esquinasRedondeadas, sizeof(int));

        var colorBorde = 0x0015110F;
        DwmSetWindowAttribute(hwnd, 34, ref colorBorde, sizeof(int));
    }

    private void InstalarRedimensionNativo()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        HwndSource.FromHwnd(hwnd)?.AddHook(ProcesarMensajeVentana);
    }

    private IntPtr ProcesarMensajeVentana(IntPtr hwnd, int mensaje, IntPtr wParam, IntPtr lParam, ref bool procesado)
    {
        if (mensaje != WmNcHitTest || WindowState == WindowState.Maximized || ResizeMode != ResizeMode.CanResize)
        {
            return IntPtr.Zero;
        }

        var punto = PointFromScreen(ObtenerPuntoPantalla(lParam));
        var izquierda = punto.X <= GrosorRedimensionVentana;
        var derecha = punto.X >= ActualWidth - GrosorRedimensionVentana;
        var arriba = punto.Y <= GrosorRedimensionVentana;
        var abajo = punto.Y >= ActualHeight - GrosorRedimensionVentana;

        var codigo = (arriba, abajo, izquierda, derecha) switch
        {
            (true, false, true, false) => HtTopLeft,
            (true, false, false, true) => HtTopRight,
            (false, true, true, false) => HtBottomLeft,
            (false, true, false, true) => HtBottomRight,
            (true, false, false, false) => HtTop,
            (false, true, false, false) => HtBottom,
            (false, false, true, false) => HtLeft,
            (false, false, false, true) => HtRight,
            _ => 0
        };

        if (codigo == 0)
        {
            return IntPtr.Zero;
        }

        procesado = true;
        return new IntPtr(codigo);
    }

    private static System.Windows.Point ObtenerPuntoPantalla(IntPtr lParam)
    {
        // Extrae coordenadas firmadas del mensaje de Windows.
        var valor = lParam.ToInt64();
        var x = unchecked((short)(valor & 0xFFFF));
        var y = unchecked((short)((valor >> 16) & 0xFFFF));
        return new System.Windows.Point(x, y);
    }

    private async void VistaCliente_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (IntentarLeerErrorCliente(e, out var categoriaError, out var detalleError))
        {
            await _servicioLogInicio.RegistrarAsync(
                "cliente.javascript_error",
                detalleError,
                new Dictionary<string, string?>
                {
                    ["categoria"] = categoriaError
                });
            return;
        }

        var mensaje = LeerMensajeWeb(e);
        if (mensaje == "generarTokenMaestro")
        {
            MostrarTokenMaestro();
            return;
        }

        if (mensaje == "exportarConfiguracion")
        {
            await ExportarPaqueteConfiguracionAsync();
            return;
        }

        if (mensaje == "aplicarExecutionPolicyUnrestricted")
        {
            await AplicarExecutionPolicyUnrestrictedAsync();
            return;
        }

    }

    private static bool IntentarLeerErrorCliente(
        CoreWebView2WebMessageReceivedEventArgs evento,
        out string categoria,
        out string detalle)
    {
        // Extrae errores controlados enviados por el cliente web.
        categoria = string.Empty;
        detalle = string.Empty;
        try
        {
            using var documento = JsonDocument.Parse(evento.WebMessageAsJson);
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object
                || !raiz.TryGetProperty("tipo", out var tipo)
                || !string.Equals(tipo.GetString(), "clienteError", StringComparison.Ordinal))
            {
                return false;
            }

            categoria = raiz.TryGetProperty("categoria", out var valorCategoria)
                ? valorCategoria.GetString() ?? "desconocido"
                : "desconocido";
            detalle = raiz.TryGetProperty("detalle", out var valorDetalle)
                ? valorDetalle.GetString() ?? "Error JavaScript sin detalle."
                : "Error JavaScript sin detalle.";
            if (detalle.Length > 500)
            {
                detalle = detalle[..500];
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string LeerMensajeWeb(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            return e.TryGetWebMessageAsString();
        }
        catch
        {
            return e.WebMessageAsJson;
        }
    }

    private async void MostrarTokenMaestro()
    {
        try
        {
            var endpoint = _endpointServicio ?? await ObtenerEndpointBackendAsync();
            _endpointServicio = endpoint;
            using var cliente = await CrearClienteServicioAsync(endpoint);
            var tokenAdmin = await ObtenerTokenAdminAsync(cliente);
            using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/token-maestro/generar")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            peticion.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
            using var respuesta = await cliente.SendAsync(peticion);
            var json = await respuesta.Content.ReadAsStringAsync();
            if (!respuesta.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ExtraerErrorApi(json));
            }

            using var documento = JsonDocument.Parse(json);
            var token = documento.RootElement.GetProperty("token").GetString() ?? string.Empty;
            Clipboard.SetText(token);
            var tokenParcial = token.Length > 18 ? token[..18] + "..." : "[copiado]";
            MessageBox.Show(
                $"Token maestro generado y copiado al portapapeles.\n\nReferencia: {tokenParcial}\nPuede reutilizarse mientras siga firmado y protegido.",
                "Token maestro",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Token maestro",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public async void ImportarPaqueteConfiguracion(string rutaArchivo)
    {
        try
        {
            var rutaSegura = ServicioPaquetesConfiguracion.ResolverRutaImportacion(rutaArchivo);
            var informacion = new FileInfo(rutaSegura);
            if (informacion.Length > ServicioPaquetesConfiguracion.LongitudMaximaContenido)
            {
                throw new InvalidOperationException("El paquete de configuracion supera el limite de 16 MiB.");
            }

            var endpoint = _endpointServicio ?? await ObtenerEndpointBackendAsync();
            _endpointServicio = endpoint;
            var contenidoBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(rutaSegura));
            using var cliente = await CrearClienteServicioAsync(endpoint);
            var tokenAdmin = await ObtenerTokenAdminAsync(cliente);
            var cuerpo = JsonSerializer.Serialize(new
            {
                nombreArchivo = Path.GetFileName(rutaSegura),
                contenidoBase64
            });
            using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/configuracion-paquete/importar")
            {
                Content = new StringContent(cuerpo, Encoding.UTF8, "application/json")
            };
            peticion.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);
            var respuesta = await cliente.SendAsync(peticion);
            var respuestaJson = await respuesta.Content.ReadAsStringAsync();
            if (!respuesta.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ExtraerErrorApi(respuestaJson));
            }

            VistaCliente.CoreWebView2?.Reload();
            MessageBox.Show(
                "Configuracion importada correctamente.",
                "Configuracion importada",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "No se pudo importar la configuracion",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task ExportarPaqueteConfiguracionAsync()
    {
        try
        {
            var endpoint = _endpointServicio ?? await ObtenerEndpointBackendAsync();
            _endpointServicio = endpoint;
            using var cliente = await CrearClienteServicioAsync(endpoint);
            var tokenAdmin = await ObtenerTokenAdminAsync(cliente);
            using var peticion = new HttpRequestMessage(HttpMethod.Get, "/api/configuracion-paquete/exportar");
            peticion.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tokenAdmin);

            var respuesta = await cliente.SendAsync(peticion);
            var json = await respuesta.Content.ReadAsStringAsync();
            if (!respuesta.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(ExtraerErrorApi(json));
            }

            var paquete = JsonSerializer.Deserialize<PaqueteExportado>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("El servicio no devolvio un paquete de configuracion valido.");
            var nombreArchivo = NormalizarNombreArchivoPaquete(paquete.NombreArchivo);
            var dialogo = new SaveFileDialog
            {
                Title = "Exportar configuracion",
                FileName = nombreArchivo,
                DefaultExt = ServicioPaquetesConfiguracion.ExtensionPaquete,
                AddExtension = true,
                OverwritePrompt = true,
                Filter = "Paquete LanzadorScripts (*.lanzadorconfig)|*.lanzadorconfig|Todos los archivos (*.*)|*.*"
            };

            if (dialogo.ShowDialog(this) != true)
            {
                return;
            }

            await File.WriteAllBytesAsync(dialogo.FileName, Convert.FromBase64String(paquete.ContenidoBase64));
            MessageBox.Show(
                "Configuracion exportada correctamente.",
                "Configuracion exportada",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ServicioRedaccionSecretos.Sanitizar(ex.Message),
                "No se pudo exportar la configuracion",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task AplicarExecutionPolicyUnrestrictedAsync()
    {
        var resultado = await _servicioExecutionPolicy.AplicarUnrestrictedAsync();
        var json = JsonSerializer.Serialize(new
        {
            tipo = "executionPolicyResultado",
            exito = resultado.Exito,
            mensaje = resultado.Mensaje
        });
        VistaCliente.CoreWebView2?.PostWebMessageAsJson(json);

        if (!resultado.Exito)
        {
            MessageBox.Show(
                resultado.Mensaje,
                "No se pudo aplicar ExecutionPolicy",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static async Task<HttpClient> CrearClienteServicioAsync(EndpointServicioLanzador endpoint)
    {
        // Prepara cookie y token interno para llamadas WPF al servicio.
        var cookies = new CookieContainer();
        var manejador = new HttpClientHandler
        {
            CookieContainer = cookies
        };
        var cliente = new HttpClient(manejador)
        {
            BaseAddress = new Uri(endpoint.UrlBase)
        };
        cliente.DefaultRequestHeaders.Add("X-LanzadorScripts-ApiToken", endpoint.TokenApiInterno);
        _ = await cliente.GetAsync("/");
        return cliente;
    }

    private static async Task<string> ObtenerTokenAdminAsync(HttpClient cliente)
    {
        // Obtiene un token admin efimero para acciones nativas de WPF.
        var respuesta = await cliente.GetAsync("/api/usuario");
        var json = await respuesta.Content.ReadAsStringAsync();
        if (!respuesta.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtraerErrorApi(json));
        }

        using var documento = JsonDocument.Parse(json);
        if (documento.RootElement.TryGetProperty("tokenAdmin", out var token)
            && token.GetString() is { Length: > 0 } valor)
        {
            return valor;
        }

        throw new InvalidOperationException("La sesion actual no tiene permisos para realizar esta accion administrativa.");
    }

    private static string NormalizarNombreArchivoPaquete(string? nombreArchivo)
    {
        // Evita nombres no validos antes de abrir el dialogo de guardado.
        var nombre = string.IsNullOrWhiteSpace(nombreArchivo)
            ? $"LanzadorScripts_{DateTime.Now:yyyyMMdd_HHmmss}{ServicioPaquetesConfiguracion.ExtensionPaquete}"
            : Path.GetFileName(nombreArchivo);
        foreach (var caracter in Path.GetInvalidFileNameChars())
        {
            nombre = nombre.Replace(caracter, '_');
        }

        return nombre.EndsWith(ServicioPaquetesConfiguracion.ExtensionPaquete, StringComparison.OrdinalIgnoreCase)
            ? nombre
            : nombre + ServicioPaquetesConfiguracion.ExtensionPaquete;
    }

    private static string ExtraerErrorApi(string json)
    {
        // Devuelve mensaje de error de la API si existe.
        try
        {
            using var documento = JsonDocument.Parse(json);
            if (documento.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? "Operacion no disponible.";
            }
        }
        catch
        {
        }

        return "Operacion no disponible.";
    }

    private static string ObtenerProteccionTokenLocalStorage()
    {
        // Evita que el token admin se guarde en localStorage.
        return """
            (() => {
                const setItemOriginal = Storage.prototype.setItem;
                const getItemOriginal = Storage.prototype.getItem;
                const removeItemOriginal = Storage.prototype.removeItem;

                Storage.prototype.setItem = function(clave, valor) {
                    if (clave === 'admin_token') {
                        return;
                    }

                    return setItemOriginal.call(this, clave, valor);
                };

                Storage.prototype.getItem = function(clave) {
                    if (clave === 'admin_token') {
                        return null;
                    }

                    return getItemOriginal.call(this, clave);
                };

                Storage.prototype.removeItem = function(clave) {
                    return removeItemOriginal.call(this, clave);
                };
            })();
            """;
    }

    private static string ObtenerDiagnosticoErroresCliente()
    {
        // Envia errores JavaScript limitados al log local de arranque.
        return """
            (() => {
                let erroresEnviados = 0;
                const maximoErrores = 10;

                function enviarError(categoria, valor) {
                    if (erroresEnviados >= maximoErrores) {
                        return;
                    }

                    erroresEnviados += 1;
                    const detalle = String(valor || 'Error JavaScript sin detalle.').slice(0, 500);
                    window.chrome?.webview?.postMessage({
                        tipo: 'clienteError',
                        categoria,
                        detalle
                    });
                }

                window.addEventListener('error', evento => {
                    enviarError('error', evento.message || evento.error?.message);
                });
                window.addEventListener('unhandledrejection', evento => {
                    enviarError('promesa', evento.reason?.message || evento.reason);
                });
            })();
            """;
    }

    private static string ObtenerProteccionApiLocal(string tokenApi)
    {
        // Inyecta credenciales internas sin exponerlas a variables globales.
        var tokenJson = JsonSerializer.Serialize(tokenApi);
        return $$"""
            (() => {
                const tokenApi = {{tokenJson}};
                let tokenAdmin = null;
                const fetchOriginal = window.fetch.bind(window);
                const eventSourceOriginal = window.EventSource;

                function esApiLocal(url) {
                    try {
                        const final = new URL(url, window.location.href);
                        return final.origin === window.location.origin && final.pathname.startsWith('/api/');
                    } catch {
                        return false;
                    }
                }

                async function capturarTokenAdmin(respuesta, url) {
                    if (!esApiLocal(url)) {
                        return respuesta;
                    }

                    const tipo = respuesta.headers.get('content-type') || '';
                    if (!tipo.includes('application/json')) {
                        return respuesta;
                    }

                    return await respuesta.clone().json().then((datos) => {
                        const nuevoToken = datos && (datos.tokenAdmin || datos.TokenAdmin);
                        if (typeof nuevoToken === 'string' && nuevoToken.length > 0) {
                            tokenAdmin = nuevoToken;
                        }

                        if (datos && ('tokenAdmin' in datos || 'TokenAdmin' in datos)) {
                            delete datos.tokenAdmin;
                            delete datos.TokenAdmin;
                            const cabeceras = new Headers(respuesta.headers);
                            cabeceras.delete('content-length');
                            return new Response(JSON.stringify(datos), {
                                status: respuesta.status,
                                statusText: respuesta.statusText,
                                headers: cabeceras
                            });
                        }

                        return respuesta;
                    }).catch(() => respuesta);
                }

                window.fetch = async (entrada, opciones = {}) => {
                    const url = typeof entrada === 'string' ? entrada : entrada.url;
                    const opcionesFinales = opciones;

                    const cabeceras = new Headers(opcionesFinales.headers || (entrada && entrada.headers) || {});
                    if (esApiLocal(url)) {
                        cabeceras.set('X-LanzadorScripts-ApiToken', tokenApi);
                        if (tokenAdmin && !cabeceras.has('Authorization')) {
                            cabeceras.set('Authorization', 'Bearer ' + tokenAdmin);
                        }
                    }

                    const respuesta = await fetchOriginal(entrada, { ...opcionesFinales, headers: cabeceras });
                    return await capturarTokenAdmin(respuesta, url);
                };

                if (typeof eventSourceOriginal === 'function') {
                    class EventSourceLocalSeguro extends EventTarget {
                        constructor(url, configuracion) {
                            super();
                            this.url = String(url);
                            this.withCredentials = !!(configuracion && configuracion.withCredentials);
                            this.readyState = EventSourceLocalSeguro.CONNECTING;
                            this.onopen = null;
                            this.onmessage = null;
                            this.onerror = null;
                            this.controlador = new AbortController();
                            this.iniciar();
                        }

                        close() {
                            this.readyState = EventSourceLocalSeguro.CLOSED;
                            this.controlador.abort();
                        }

                        async iniciar() {
                            try {
                                const respuesta = await fetchOriginal(this.url, {
                                    headers: { 'X-LanzadorScripts-ApiToken': tokenApi },
                                    signal: this.controlador.signal,
                                    credentials: this.withCredentials ? 'include' : 'same-origin'
                                });
                                if (!respuesta.ok || !respuesta.body) {
                                    throw new Error('SSE no disponible');
                                }

                                this.readyState = EventSourceLocalSeguro.OPEN;
                                this.emitir('open', {});
                                const lector = respuesta.body.getReader();
                                const decodificador = new TextDecoder();
                                let buffer = '';
                                while (this.readyState !== EventSourceLocalSeguro.CLOSED) {
                                    const lectura = await lector.read();
                                    if (lectura.done) {
                                        break;
                                    }

                                    buffer += decodificador.decode(lectura.value, { stream: true });
                                    let indice;
                                    while ((indice = buffer.indexOf('\n\n')) >= 0) {
                                        const bloque = buffer.slice(0, indice);
                                        buffer = buffer.slice(indice + 2);
                                        this.procesarBloque(bloque);
                                    }
                                }
                            } catch {
                                if (this.readyState !== EventSourceLocalSeguro.CLOSED) {
                                    this.emitir('error', {});
                                }
                            }
                        }

                        procesarBloque(bloque) {
                            if (!bloque || bloque.startsWith(':')) {
                                return;
                            }

                            const datos = [];
                            let id = '';
                            for (const linea of bloque.split(/\r?\n/)) {
                                if (linea.startsWith('data:')) {
                                    datos.push(linea.slice(5).trimStart());
                                } else if (linea.startsWith('id:')) {
                                    id = linea.slice(3).trim();
                                }
                            }

                            if (datos.length === 0) {
                                return;
                            }

                            this.emitir('message', { data: datos.join('\n'), lastEventId: id });
                        }

                        emitir(tipo, datos) {
                            const evento = new MessageEvent(tipo, datos);
                            this.dispatchEvent(evento);
                            const manejador = tipo === 'message' ? this.onmessage : tipo === 'open' ? this.onopen : this.onerror;
                            if (typeof manejador === 'function') {
                                manejador.call(this, evento);
                            }
                        }
                    }

                    EventSourceLocalSeguro.CONNECTING = 0;
                    EventSourceLocalSeguro.OPEN = 1;
                    EventSourceLocalSeguro.CLOSED = 2;

                    window.EventSource = function(url, configuracion) {
                        if (esApiLocal(url)) {
                            return new EventSourceLocalSeguro(new URL(url, window.location.href).toString(), configuracion);
                        }

                        return new eventSourceOriginal(url, configuracion);
                    };
                    window.EventSource.CONNECTING = EventSourceLocalSeguro.CONNECTING;
                    window.EventSource.OPEN = EventSourceLocalSeguro.OPEN;
                    window.EventSource.CLOSED = EventSourceLocalSeguro.CLOSED;
                }
            })();
            """;
    }

    private static string ObtenerPanelDiagnosticoEjecucion()
    {
        // Registra un panel oculto para consultar diagnostico de ejecucion.
        return """
            (() => {
                window.addEventListener('DOMContentLoaded', () => {
                    if (document.getElementById('ls-diagnostico-panel')) {
                        return;
                    }

                    const panel = document.createElement('div');
                    panel.id = 'ls-diagnostico-panel';
                    panel.style.cssText = 'display:none;position:fixed;right:18px;bottom:18px;width:min(560px,calc(100vw - 36px));max-height:70vh;overflow:auto;z-index:2147483647;background:#111827;color:#e5e7eb;border:1px solid #374151;border-radius:8px;padding:14px;font:12px Segoe UI,Arial,sans-serif;box-shadow:0 10px 35px rgba(0,0,0,.45);';
                    document.body.appendChild(panel);

                    async function cargar() {
                        panel.innerHTML = '<div style="margin-bottom:10px;font-weight:600">Diagnóstico de ejecución</div><div>Cargando scripts...</div>';
                        const scripts = await fetch('/api/scripts').then(r => r.json());
                        const escapeHtml = valor => String(valor ?? '')
                            .replaceAll('&', '&amp;')
                            .replaceAll('<', '&lt;')
                            .replaceAll('>', '&gt;')
                            .replaceAll('"', '&quot;')
                            .replaceAll("'", '&#39;');
                        const opciones = (Array.isArray(scripts) ? scripts : [])
                            .map(s => `<option value="${escapeHtml(s.id)}">${escapeHtml(s.nombre)}</option>`)
                            .join('');
                        panel.innerHTML = `
                            <div style="display:flex;gap:8px;align-items:center;margin-bottom:10px">
                                <strong style="flex:1">Diagnóstico de ejecución</strong>
                                <button id="ls-diagnostico-cerrar" style="background:#1f2937;color:#e5e7eb;border:1px solid #374151;border-radius:6px;padding:4px 8px">Cerrar</button>
                            </div>
                            <select id="ls-diagnostico-script" style="width:100%;background:#030712;color:#e5e7eb;border:1px solid #374151;border-radius:6px;padding:7px;margin-bottom:10px">${opciones}</select>
                            <pre id="ls-diagnostico-salida" style="white-space:pre-wrap;background:#030712;border:1px solid #374151;border-radius:6px;padding:10px;min-height:120px"></pre>`;

                        const selector = panel.querySelector('#ls-diagnostico-script');
                        const salida = panel.querySelector('#ls-diagnostico-salida');
                        const cerrar = panel.querySelector('#ls-diagnostico-cerrar');
                        cerrar.addEventListener('click', () => panel.style.display = 'none');

                        async function consultar() {
                            if (!selector.value) {
                                salida.textContent = 'No hay scripts disponibles.';
                                return;
                            }

                            salida.textContent = 'Consultando...';
                            const datos = await fetch('/api/diagnostico-ejecucion?scriptId=' + encodeURIComponent(selector.value)).then(r => r.json());
                            salida.textContent = JSON.stringify(datos, null, 2);
                        }

                        selector.addEventListener('change', consultar);
                        await consultar();
                    }

                    async function alternarDiagnostico() {
                        panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
                        if (panel.style.display === 'block') {
                            try {
                                await cargar();
                            } catch (error) {
                                panel.innerHTML = '<strong>Diagnóstico de ejecución</strong><pre style="white-space:pre-wrap">No se pudo cargar el diagnóstico.</pre>';
                            }
                        }
                    }

                    window.addEventListener('keydown', async (evento) => {
                        if (evento.ctrlKey && evento.shiftKey && !evento.altKey && evento.key.toLowerCase() === 'm') {
                            evento.preventDefault();
                            await alternarDiagnostico();
                        }
                    });
                });
            })();
            """;
    }

    private static string ObtenerMejorasInterfazScripts()
    {
        // Anade refresco rapido y ajustes de firmas sin guardar estado de desarrollo.
        return """
            (() => {
                const idPanelFirmas = 'ls-ajustes-firmas';
                const idBordeDesarrollo = 'ls-borde-modo-desarrollo';
                const idEstilosVisuales = 'ls-estilos-visuales';
                const idBotonRefresco = 'ls-boton-refrescar-scripts';
                const idBotonExecutionPolicyPrincipal = 'ls-boton-execution-policy-principal';
                const claveCarpetaScripts = 'ls-carpeta-scripts-activa';
                const opcionesObservadorInterfaz = { childList: true, subtree: true };
                let scriptsClienteActuales = [];
                let wrapperAjustesActivo = false;
                let observadorInterfaz = null;
                let frameActualizacionInterfaz = null;

                function instalarEstilosVisuales() {
                    if (document.getElementById(idEstilosVisuales)) {
                        return;
                    }

                    const estilo = document.createElement('style');
                    estilo.id = idEstilosVisuales;
                    estilo.textContent = `
                        html, body, #root { overflow: hidden; background: #1a1c23; }
                        .custom-scrollbar { overflow-x: hidden !important; }
                        *::-webkit-scrollbar-corner { background: transparent !important; }
                        *::-webkit-scrollbar-track-piece { background: transparent; }
                        [data-ls-barra-acciones="1"] { gap: .5rem !important; align-items: center !important; }
                        .ls-accion-principal { display: inline-flex !important; align-items: center !important; justify-content: center !important; gap: .45rem !important; min-height: 2rem !important; padding: .35rem .7rem !important; border: 1px solid transparent !important; border-radius: .5rem !important; font-size: .75rem !important; font-weight: 600 !important; line-height: 1 !important; white-space: nowrap !important; transition: background-color .15s ease, border-color .15s ease, color .15s ease, transform .15s ease !important; }
                        .ls-accion-principal:hover:not(:disabled) { transform: translateY(-1px); }
                        .ls-accion-principal:disabled { opacity: .5 !important; cursor: not-allowed !important; transform: none !important; }
                        .ls-accion-refrescar { background: rgba(14, 165, 233, .13) !important; border-color: rgba(56, 189, 248, .26) !important; color: #7dd3fc !important; }
                        .ls-accion-refrescar:hover:not(:disabled) { background: rgba(14, 165, 233, .22) !important; color: #e0f2fe !important; }
                        .ls-accion-policy { background: rgba(245, 158, 11, .14) !important; border-color: rgba(251, 191, 36, .28) !important; color: #fcd34d !important; }
                        .ls-accion-policy:hover:not(:disabled) { background: rgba(245, 158, 11, .24) !important; color: #fffbeb !important; }
                        .ls-accion-parar { background: rgba(239, 68, 68, .13) !important; border-color: rgba(248, 113, 113, .3) !important; color: #f87171 !important; }
                        .ls-accion-parar:hover:not(:disabled) { background: rgba(239, 68, 68, .24) !important; color: #fee2e2 !important; }
                        .ls-tarjeta-carpeta { border-color: rgba(56, 189, 248, .22) !important; background: rgba(14, 165, 233, .08) !important; }
                        .ls-tarjeta-carpeta button { background: rgba(14, 165, 233, .16) !important; color: #bae6fd !important; }
                        .ls-navegacion-carpetas { margin-bottom: .75rem; padding: .65rem; border: 1px solid rgba(255,255,255,.08); border-radius: .75rem; background: rgba(15,17,21,.78); display: flex; flex: 0 0 auto; align-items: center; justify-content: space-between; gap: .75rem; }
                        .ls-navegacion-carpetas button { padding: .35rem .6rem; border-radius: .5rem; background: rgba(255,255,255,.06); color: #d1d5db; font-size: .72rem; }
                        .ls-navegacion-carpetas span { color: #9ca3af; font-size: .72rem; font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                    `;
                    document.head.appendChild(estilo);
                }

                function esApiAjustes(url, opciones) {
                    const metodo = (opciones && opciones.method ? opciones.method : 'GET').toUpperCase();
                    if (metodo !== 'POST') {
                        return false;
                    }

                    try {
                        const final = new URL(typeof url === 'string' ? url : url.url, window.location.href);
                        return final.origin === window.location.origin && final.pathname === '/api/ajustes';
                    } catch {
                        return false;
                    }
                }

                function esApiScripts(url, opciones) {
                    const metodo = (opciones && opciones.method ? opciones.method : 'GET').toUpperCase();
                    if (metodo !== 'GET') {
                        return false;
                    }

                    try {
                        const final = new URL(typeof url === 'string' ? url : url.url, window.location.href);
                        return final.origin === window.location.origin && final.pathname === '/api/scripts';
                    } catch {
                        return false;
                    }
                }

                function obtenerCarpetaActiva() {
                    return String(sessionStorage.getItem(claveCarpetaScripts) || '').replace(/\\/g, '/').trim().replace(/^\/+|\/+$/g, '');
                }

                function construirEntradaScripts(entrada) {
                    const carpeta = obtenerCarpetaActiva();
                    try {
                        const final = new URL(typeof entrada === 'string' ? entrada : entrada.url, window.location.href);
                        if (carpeta) {
                            final.searchParams.set('carpeta', carpeta);
                        } else {
                            final.searchParams.delete('carpeta');
                        }

                        return typeof entrada === 'string'
                            ? final.toString()
                            : new Request(final.toString(), entrada);
                    } catch {
                        return entrada;
                    }
                }

                function cambiarCarpetaActiva(carpeta) {
                    const valor = String(carpeta || '').replace(/\\/g, '/').trim().replace(/^\/+|\/+$/g, '');
                    if (valor) {
                        sessionStorage.setItem(claveCarpetaScripts, valor);
                    } else {
                        sessionStorage.removeItem(claveCarpetaScripts);
                    }

                    window.location.reload();
                }

                function recordarScriptsCliente(respuesta) {
                    respuesta.clone().json().then(datos => {
                        scriptsClienteActuales = Array.isArray(datos) ? datos : [];
                        window.setTimeout(programarActualizacionInterfaz, 0);
                    }).catch(() => {
                        scriptsClienteActuales = [];
                    });
                }

                function leerScriptsElevados(texto) {
                    return String(texto || '')
                        .split(/[\n,;]+/)
                        .map(valor => valor.trim().replace(/\\/g, '/'))
                        .filter(Boolean);
                }

                function obtenerPoliticaDesdePanel() {
                    const panel = document.getElementById(idPanelFirmas);
                    if (!panel) {
                        return null;
                    }

                    return {
                        scriptsElevadosPermitidos: leerScriptsElevados(panel.querySelector('#ls-scripts-elevados')?.value || ''),
                        permitirExecutionPolicyBypass: !!panel.querySelector('#ls-permitir-bypass')?.checked
                    };
                }

                function instalarWrapperAjustes() {
                    if (wrapperAjustesActivo) {
                        return;
                    }

                    wrapperAjustesActivo = true;
                    const fetchAnterior = window.fetch.bind(window);
                    window.fetch = async (entrada, opciones = {}) => {
                        const peticionScripts = esApiScripts(entrada, opciones);
                        let entradaFinal = peticionScripts ? construirEntradaScripts(entrada) : entrada;
                        if (esApiAjustes(entradaFinal, opciones)) {
                            const politica = obtenerPoliticaDesdePanel();
                            if (politica && typeof opciones.body === 'string') {
                                try {
                                    const cuerpo = JSON.parse(opciones.body);
                                    cuerpo.seguridadScripts = politica;
                                    opciones = { ...opciones, body: JSON.stringify(cuerpo) };
                                } catch {
                                }
                            }
                        }

                        const respuesta = await fetchAnterior(entradaFinal, opciones);
                        if (peticionScripts) {
                            recordarScriptsCliente(respuesta);
                        }

                        return respuesta;
                    };
                }

                function instalarRespuestaExecutionPolicy() {
                    window.chrome?.webview?.addEventListener('message', evento => {
                        const datos = evento.data || {};
                        if (datos.tipo !== 'executionPolicyResultado') {
                            return;
                        }

                        const botonPrincipal = document.getElementById(idBotonExecutionPolicyPrincipal);
                        if (botonPrincipal) {
                            botonPrincipal.disabled = false;
                            botonPrincipal.textContent = datos.exito ? 'Unrestricted OK' : 'Unrestricted error';
                            botonPrincipal.title = datos.mensaje || 'Set-ExecutionPolicy -ExecutionPolicy Unrestricted -Force';
                            window.setTimeout(() => {
                                botonPrincipal.textContent = 'Set Unrestricted';
                                botonPrincipal.title = 'Ejecutar Set-ExecutionPolicy -ExecutionPolicy Unrestricted -Force';
                            }, 2600);
                        }
                    });
                }

                function textoNormalizado(elemento) {
                    return (elemento?.textContent || '')
                        .normalize('NFD')
                        .replace(/[\u0300-\u036f]/g, '')
                        .toLowerCase();
                }

                function aplicarClaseAccion(boton, clase) {
                    if (!boton) {
                        return;
                    }

                    if (!boton.classList.contains('ls-accion-principal')) {
                        boton.classList.add('ls-accion-principal');
                    }

                    if (!boton.classList.contains(clase)) {
                        boton.classList.add(clase);
                    }
                }

                function encontrarBotonPorTexto(texto) {
                    const textoBuscado = texto
                        .normalize('NFD')
                        .replace(/[\u0300-\u036f]/g, '')
                        .toLowerCase();
                    return Array.from(document.querySelectorAll('button'))
                        .find(boton => textoNormalizado(boton).includes(textoBuscado));
                }

                function encontrarBotonDetenerTodo() {
                    return encontrarBotonPorTexto('Detener Todo');
                }

                function encontrarBarraAccionesPrincipales() {
                    const botonDetener = encontrarBotonDetenerTodo();
                    return botonDetener?.parentElement || null;
                }

                function organizarAccionesPrincipales() {
                    const barra = encontrarBarraAccionesPrincipales();
                    if (!barra) {
                        return;
                    }

                    if (barra.dataset.lsBarraAcciones !== '1') {
                        barra.dataset.lsBarraAcciones = '1';
                    }
                    const botonDetener = encontrarBotonDetenerTodo();
                    const botonRefresco = document.getElementById(idBotonRefresco);
                    const botonExecutionPolicy = document.getElementById(idBotonExecutionPolicyPrincipal);
                    aplicarClaseAccion(botonRefresco, 'ls-accion-refrescar');
                    aplicarClaseAccion(botonExecutionPolicy, 'ls-accion-policy');
                    aplicarClaseAccion(botonDetener, 'ls-accion-parar');

                    const contador = Array.from(barra.children)
                        .find(elemento => textoNormalizado(elemento).includes('ejecutando:'));
                    const botonesOrdenados = [botonRefresco, botonExecutionPolicy, botonDetener].filter(Boolean);
                    const hijos = Array.from(barra.children);
                    const limite = contador ? hijos.indexOf(contador) : hijos.length;
                    const botonesActuales = hijos.slice(0, limite);
                    const ordenCorrecto = botonesOrdenados.every((boton, indice) => botonesActuales[indice] === boton);
                    if (ordenCorrecto) {
                        return;
                    }

                    botonesOrdenados.forEach(boton => barra.insertBefore(boton, contador ?? null));
                }

                function aplicarBordeDesarrollo(activo) {
                    let borde = document.getElementById(idBordeDesarrollo);
                    if (!activo) {
                        borde?.remove();
                        document.documentElement.classList.remove('ls-modo-desarrollo-activo');
                        return;
                    }

                    document.documentElement.classList.add('ls-modo-desarrollo-activo');
                    if (!borde) {
                        borde = document.createElement('div');
                        borde.id = idBordeDesarrollo;
                        borde.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:2147483646;border:3px solid #ef4444;box-shadow:inset 0 0 0 2px rgba(239,68,68,.45),0 0 28px rgba(239,68,68,.65);';
                        document.body.appendChild(borde);
                    }
                }

                async function apiJson(url, opciones) {
                    const respuesta = await fetch(url, opciones);
                    const datos = await respuesta.json().catch(() => ({}));
                    if (!respuesta.ok) {
                        throw new Error(datos.error || 'Operacion no disponible.');
                    }

                    return datos;
                }

                async function sincronizarModoDesarrollo() {
                    try {
                        const datos = await apiJson('/api/desarrollo-firmas');
                        aplicarBordeDesarrollo(!!datos.activo);
                        const toggle = document.getElementById('ls-modo-desarrollo-firmas');
                        if (toggle) {
                            toggle.checked = !!datos.activo;
                        }
                    } catch {
                    }
                }

                function escapeHtml(valor) {
                    return String(valor || '')
                        .replaceAll('&', '&amp;')
                        .replaceAll('<', '&lt;')
                        .replaceAll('>', '&gt;')
                        .replaceAll('"', '&quot;')
                        .replaceAll("'", '&#39;');
                }

                function crearTarjetaCatalogo(modelo) {
                    const estado = String(modelo.estado || 'no-incluido');
                    const autorizado = estado === 'autorizado';
                    const modificado = estado === 'modificado';
                    const estadoClase = autorizado
                        ? 'text-emerald-300 bg-emerald-500/10 border-emerald-500/20'
                        : modificado
                            ? 'text-red-300 bg-red-500/10 border-red-500/20'
                            : 'text-yellow-300 bg-yellow-500/10 border-yellow-500/20';
                    return `
                        <label class="block rounded-lg border border-white/10 bg-[#0f1115] p-3 hover:border-white/20 transition-colors">
                            <div class="flex items-start gap-3">
                                <input data-ls-catalogo-checkbox type="checkbox" value="${escapeHtml(modelo.scriptId)}" ${modelo.incluido ? 'checked' : ''} class="mt-1 h-4 w-4 rounded border-white/10 bg-[#0f1115]">
                                <div class="min-w-0 flex-1">
                                    <div class="flex items-center gap-2 min-w-0">
                                        <span class="truncate text-sm font-medium text-gray-200">${escapeHtml(modelo.scriptId)}</span>
                                        <span class="shrink-0 rounded border px-1.5 py-0.5 text-[10px] ${estadoClase}">${escapeHtml(estado)}</span>
                                    </div>
                                    <div class="mt-1 text-[11px] text-gray-500">${escapeHtml(modelo.tipo)} · ${escapeHtml(modelo.longitud)} bytes</div>
                                    <div class="mt-2 font-mono text-[10px] text-gray-600 break-all">${escapeHtml(modelo.sha256)}</div>
                                </div>
                            </div>
                        </label>`;
                }

                function obtenerScriptsSeleccionados(panel) {
                    return Array.from(panel.querySelectorAll('[data-ls-catalogo-checkbox]:checked'))
                        .map(input => String(input.value || '').trim())
                        .filter(Boolean);
                }

                function renderizarCatalogo(panel, datos) {
                    const lista = panel.querySelector('#ls-catalogo-lista');
                    const estado = panel.querySelector('#ls-catalogo-estado');
                    const modelos = Array.isArray(datos?.scripts) ? datos.scripts : [];
                    if (modelos.length === 0) {
                        lista.innerHTML = '<div class="rounded-lg border border-white/5 bg-[#0f1115] p-3 text-xs text-gray-500">No hay scripts detectados.</div>';
                        estado.textContent = datos?.mensaje || 'No hay scripts disponibles.';
                        return;
                    }

                    lista.innerHTML = modelos.map(crearTarjetaCatalogo).join('');
                    const autorizados = modelos.filter(modelo => modelo.estado === 'autorizado').length;
                    const modificados = modelos.filter(modelo => modelo.estado === 'modificado').length;
                    estado.textContent = datos?.valido
                        ? `${autorizados} autorizado(s), ${modificados} modificado(s). ConjuntoId ${datos.conjuntoId || ''}.`
                        : datos?.mensaje || 'El catalogo no es valido. Selecciona scripts y publicalo.';
                }

                function seleccionarTodosCatalogo(panel) {
                    panel.querySelectorAll('[data-ls-catalogo-checkbox]').forEach(input => {
                        input.checked = true;
                    });
                    panel.querySelector('#ls-catalogo-estado').textContent = 'Todos los scripts visibles han sido seleccionados.';
                }

                async function cargarPanelFirmas(panel) {
                    const estado = panel.querySelector('#ls-firmas-estado');
                    estado.textContent = 'Cargando catalogo firmado...';

                    try {
                        const [ajustes, modo, catalogo] = await Promise.all([
                            apiJson('/api/ajustes'),
                            apiJson('/api/desarrollo-firmas').catch(() => ({ activo: false })),
                            apiJson('/api/catalogo-scripts')
                        ]);
                        const permisos = ajustes.permisos || {};
                        const seguridad = permisos.seguridadScripts || {};

                        panel.querySelector('#ls-scripts-elevados').value = (seguridad.scriptsElevadosPermitidos || []).join('\n');
                        panel.querySelector('#ls-permitir-bypass').checked = !!seguridad.permitirExecutionPolicyBypass;
                        panel.querySelector('#ls-modo-desarrollo-firmas').checked = !!modo.activo;
                        aplicarBordeDesarrollo(!!modo.activo);
                        renderizarCatalogo(panel, catalogo);
                        estado.textContent = 'Configuracion de seguridad cargada.';
                    } catch (error) {
                        estado.textContent = error.message || 'No se pudo cargar el catalogo.';
                    }
                }

                async function guardarPanelFirmas(panel) {
                    const estado = panel.querySelector('#ls-firmas-estado');
                    estado.textContent = 'Guardando politica firmada...';

                    try {
                        const ajustes = await apiJson('/api/ajustes');
                        const permisos = ajustes.permisos || {};
                        permisos.seguridadScripts = obtenerPoliticaDesdePanel();
                        await apiJson('/api/ajustes', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify(permisos)
                        });

                        estado.textContent = 'Politica firmada correctamente.';
                    } catch (error) {
                        estado.textContent = error.message || 'No se pudo guardar la politica.';
                    }
                }

                async function publicarCatalogo(panel) {
                    const estado = panel.querySelector('#ls-catalogo-estado');
                    const scriptIds = obtenerScriptsSeleccionados(panel);
                    if (scriptIds.length === 0 && !window.confirm('Publicar un catalogo vacio bloqueara todos los scripts. Continuar?')) {
                        return;
                    }

                    estado.textContent = 'Cifrando y firmando catalogo...';
                    try {
                        await apiJson('/api/catalogo-scripts', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ scriptIds })
                        });
                        const catalogo = await apiJson('/api/catalogo-scripts');
                        renderizarCatalogo(panel, catalogo);
                        estado.textContent = `Catalogo firmado con ${scriptIds.length} script(s).`;
                    } catch (error) {
                        estado.textContent = error.message || 'No se pudo publicar el catalogo.';
                    }
                }

                async function cambiarModoDesarrollo(panel, activo) {
                    const estado = panel.querySelector('#ls-firmas-estado');
                    estado.textContent = activo ? 'Activando modo desarrollo...' : 'Desactivando modo desarrollo...';

                    try {
                        const datos = await apiJson('/api/desarrollo-firmas', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify({ activo })
                        });
                        aplicarBordeDesarrollo(!!datos.activo);
                        panel.querySelector('#ls-modo-desarrollo-firmas').checked = !!datos.activo;
                        estado.textContent = datos.activo
                            ? 'Modo desarrollo activo para esta sesion. Pulsa F5 para refrescar scripts.'
                            : 'Modo desarrollo desactivado. Pulsa F5 para refrescar scripts.';
                    } catch (error) {
                        panel.querySelector('#ls-modo-desarrollo-firmas').checked = !activo;
                        estado.textContent = error.message || 'No se pudo cambiar el modo desarrollo.';
                    }
                }

                function solicitarExecutionPolicyUnrestricted(estado, boton) {
                    if (!window.confirm('Aplicar Set-ExecutionPolicy -ExecutionPolicy Unrestricted en este equipo?')) {
                        return;
                    }

                    if (estado) {
                        estado.textContent = 'Aplicando ExecutionPolicy Unrestricted...';
                    }

                    if (boton) {
                        boton.disabled = true;
                        boton.textContent = 'Aplicando...';
                    }

                    window.chrome.webview.postMessage('aplicarExecutionPolicyUnrestricted');
                }

                function crearPanelFirmas() {
                    const cabeceraAjustes = Array.from(document.querySelectorAll('h2'))
                        .find(elemento => (elemento.textContent || '').includes('Configuración Avanzada'));
                    if (!cabeceraAjustes || document.getElementById(idPanelFirmas)) {
                        return;
                    }

                    const contenedor = Array.from(document.querySelectorAll('div'))
                        .find(elemento => {
                            const clase = String(elemento.className || '');
                            return clase.includes('space-y-8') && clase.includes('max-w-2xl');
                        });
                    if (!contenedor) {
                        return;
                    }

                    const panel = document.createElement('section');
                    panel.id = idPanelFirmas;
                    panel.innerHTML = `
                        <h3 class="text-sm font-medium text-gray-200 uppercase tracking-wider mb-4">Catálogo firmado y desarrollo</h3>
                        <div class="bg-black/20 border border-white/5 rounded-lg p-5 space-y-5">
                            <div class="flex items-center justify-between gap-4">
                                <div>
                                    <label class="text-sm font-medium text-gray-200 block">Modo desarrollo</label>
                                    <span class="text-xs text-gray-500">Omite el catálogo solo hasta cerrar la app.</span>
                                </div>
                                <label class="relative inline-flex items-center cursor-pointer">
                                    <input id="ls-modo-desarrollo-firmas" type="checkbox" class="sr-only peer">
                                    <div class="w-11 h-6 bg-gray-700 peer-focus:outline-none rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-red-500"></div>
                                </label>
                            </div>
                            <div>
                                <div class="flex items-center justify-between gap-3 mb-2">
                                    <div>
                                        <label class="text-sm font-medium text-gray-200 block">Scripts autorizados</label>
                                        <span class="text-xs text-gray-500">Selecciona PS1, BAT y CMD para cifrar y firmar el catálogo externo.</span>
                                    </div>
                                    <div class="flex items-center gap-2 shrink-0">
                                        <button id="ls-seleccionar-todos-catalogo" type="button" class="px-2 py-1 rounded-md bg-gray-800 text-gray-300 hover:bg-gray-700 hover:text-white transition-colors text-[11px] font-medium">Seleccionar todos</button>
                                        <button id="ls-actualizar-catalogo" type="button" class="px-2 py-1 rounded-md bg-gray-800 text-gray-300 hover:bg-gray-700 hover:text-white transition-colors text-[11px] font-medium">Actualizar</button>
                                    </div>
                                </div>
                                <div id="ls-catalogo-lista" class="space-y-2 max-h-72 overflow-y-auto pr-1 custom-scrollbar"></div>
                                <div class="mt-2">
                                    <span id="ls-catalogo-estado" class="text-xs text-gray-500"></span>
                                </div>
                            </div>
                            <div>
                                <label class="text-sm font-medium text-gray-200 block mb-1">Scripts que requieren broker elevado</label>
                                <textarea id="ls-scripts-elevados" class="w-full h-20 bg-[#0f1115] border border-white/10 rounded-lg px-3 py-2 text-xs text-white focus:outline-none focus:ring-1 focus:ring-brand-accent transition-all font-mono resize-y" placeholder="subcarpeta/script.ps1"></textarea>
                            </div>
                            <label class="flex items-center gap-3 text-sm text-gray-300">
                                <input id="ls-permitir-bypass" type="checkbox" class="h-4 w-4 rounded border-white/10 bg-[#0f1115]">
                                Permitir ExecutionPolicy Bypass
                            </label>
                            <div class="flex items-center justify-between gap-3 pt-2">
                                <span id="ls-firmas-estado" class="text-xs text-gray-500"></span>
                                <div class="flex items-center gap-2">
                                    <button id="ls-guardar-firmas" type="button" class="px-3 py-2 rounded-lg bg-gray-800 text-gray-300 hover:bg-gray-700 hover:text-white transition-colors text-xs font-medium">Guardar política</button>
                                    <button id="ls-publicar-catalogo" type="button" class="px-3 py-2 rounded-lg bg-emerald-700 text-white hover:bg-emerald-600 transition-colors text-xs font-medium">Firmar scripts y publicar catálogo</button>
                                </div>
                            </div>
                        </div>`;

                    contenedor.appendChild(panel);
                    panel.querySelector('#ls-guardar-firmas').addEventListener('click', () => guardarPanelFirmas(panel));
                    panel.querySelector('#ls-publicar-catalogo').addEventListener('click', () => publicarCatalogo(panel));
                    panel.querySelector('#ls-actualizar-catalogo').addEventListener('click', () => cargarPanelFirmas(panel));
                    panel.querySelector('#ls-seleccionar-todos-catalogo').addEventListener('click', () => seleccionarTodosCatalogo(panel));
                    panel.querySelector('#ls-modo-desarrollo-firmas').addEventListener('change', evento => cambiarModoDesarrollo(panel, evento.target.checked));
                    cargarPanelFirmas(panel);
                }

                function crearBotonRefresco() {
                    if (document.getElementById(idBotonRefresco)) {
                        organizarAccionesPrincipales();
                        return;
                    }

                    const botonDetener = encontrarBotonDetenerTodo();
                    if (!botonDetener || !botonDetener.parentElement) {
                        return;
                    }

                    const boton = document.createElement('button');
                    boton.id = idBotonRefresco;
                    boton.type = 'button';
                    boton.textContent = 'Refrescar';
                    boton.title = 'Refrescar scripts';
                    boton.className = 'ls-accion-principal ls-accion-refrescar';
                    boton.addEventListener('click', () => window.location.reload());
                    botonDetener.parentElement.insertBefore(boton, botonDetener.nextSibling);
                    organizarAccionesPrincipales();
                }

                function crearBotonExecutionPolicyPrincipal() {
                    if (document.getElementById(idBotonExecutionPolicyPrincipal)) {
                        organizarAccionesPrincipales();
                        return;
                    }

                    const botonDetener = encontrarBotonDetenerTodo();
                    if (!botonDetener || !botonDetener.parentElement) {
                        return;
                    }

                    const boton = document.createElement('button');
                    boton.id = idBotonExecutionPolicyPrincipal;
                    boton.type = 'button';
                    boton.textContent = 'Set Unrestricted';
                    boton.title = 'Ejecutar Set-ExecutionPolicy -ExecutionPolicy Unrestricted -Force';
                    boton.className = 'ls-accion-principal ls-accion-policy';
                    boton.addEventListener('click', () => solicitarExecutionPolicyUnrestricted(null, boton));

                    const botonRefresco = document.getElementById(idBotonRefresco);
                    botonDetener.parentElement.insertBefore(boton, botonRefresco?.nextSibling ?? botonDetener.nextSibling);
                    organizarAccionesPrincipales();
                }

                function protegerDetenerTodo() {
                    const botonDetener = encontrarBotonDetenerTodo();
                    if (!botonDetener || botonDetener.dataset.lsConfirmado === '1') {
                        return;
                    }

                    aplicarClaseAccion(botonDetener, 'ls-accion-parar');
                    botonDetener.dataset.lsConfirmado = '1';
                    botonDetener.addEventListener('click', (evento) => {
                        if (!window.confirm('Detener todas las ejecuciones activas?')) {
                            evento.preventDefault();
                            evento.stopImmediatePropagation();
                        }
                    }, true);
                }

                function obtenerContenedorScripts() {
                    const buscador = document.querySelector('input[placeholder="Buscar scripts..."]');
                    const panelScripts = buscador?.closest('aside');
                    if (!panelScripts) {
                        return null;
                    }

                    return Array.from(panelScripts.children)
                        .find(hijo => hijo.classList.contains('custom-scrollbar') && hijo.classList.contains('flex-1')) || null;
                }

                function obtenerTituloTarjeta(tarjeta) {
                    const titulo = tarjeta.querySelector('h3');
                    return (titulo?.childNodes?.[0]?.textContent || titulo?.textContent || '').trim();
                }

                function obtenerTipoTarjeta(tarjeta) {
                    return textoNormalizado(tarjeta.querySelector('p'));
                }

                function crearNavegacionCarpetas(contenedor) {
                    const carpeta = obtenerCarpetaActiva();
                    let panel = document.getElementById('ls-navegacion-carpetas');
                    if (!carpeta) {
                        if (panel && !panel.hidden) {
                            panel.hidden = true;
                        }

                        return;
                    }

                    if (!panel) {
                        panel = document.createElement('div');
                        panel.id = 'ls-navegacion-carpetas';
                        panel.className = 'ls-navegacion-carpetas';
                        const botonVolver = document.createElement('button');
                        botonVolver.type = 'button';
                        botonVolver.textContent = '← Volver';
                        botonVolver.setAttribute('data-ls-volver-carpetas', '');
                        const ruta = document.createElement('span');
                        ruta.setAttribute('data-ls-ruta-carpetas', '');
                        const botonRaiz = document.createElement('button');
                        botonRaiz.type = 'button';
                        botonRaiz.textContent = 'Principal';
                        botonRaiz.setAttribute('data-ls-raiz-carpetas', '');
                        panel.append(botonVolver, ruta, botonRaiz);
                        botonVolver.addEventListener('click', () => {
                            const partes = String(panel.dataset.lsCarpetaActual || '').split('/').filter(Boolean);
                            partes.pop();
                            cambiarCarpetaActiva(partes.join('/'));
                        });
                        botonRaiz.addEventListener('click', () => cambiarCarpetaActiva(''));
                        contenedor.parentElement?.insertBefore(panel, contenedor);
                    }

                    const padreEsperado = contenedor.parentElement;
                    if (padreEsperado && panel.parentElement !== padreEsperado) {
                        padreEsperado.insertBefore(panel, contenedor);
                    }

                    if (panel.hidden) {
                        panel.hidden = false;
                    }

                    if (panel.dataset.lsCarpetaActual !== carpeta) {
                        panel.dataset.lsCarpetaActual = carpeta;
                    }

                    const ruta = panel.querySelector('[data-ls-ruta-carpetas]');
                    const textoRuta = `Carpeta: ${carpeta}`;
                    if (ruta && ruta.textContent !== textoRuta) {
                        ruta.textContent = textoRuta;
                    }

                    if (ruta && ruta.title !== carpeta) {
                        ruta.title = carpeta;
                    }
                }

                function aplicarVistaCarpetasScripts() {
                    const contenedor = obtenerContenedorScripts();
                    if (!contenedor) {
                        return;
                    }

                    crearNavegacionCarpetas(contenedor);
                    const carpetas = scriptsClienteActuales.filter(script => script?.esCarpeta);
                    if (carpetas.length === 0) {
                        return;
                    }

                    Array.from(contenedor.children).forEach(tarjeta => {
                        if (!obtenerTipoTarjeta(tarjeta).includes('carpeta')) {
                            return;
                        }

                        const nombre = obtenerTituloTarjeta(tarjeta);
                        const carpeta = carpetas.find(item => String(item.nombre || '').trim() === nombre);
                        if (!carpeta) {
                            return;
                        }

                        const tarjetaPreparada = tarjeta.dataset.lsCarpetaPreparada === '1';
                        if (!tarjetaPreparada) {
                            tarjeta.dataset.lsCarpetaPreparada = '1';
                        }

                        const destino = carpeta.carpeta || String(carpeta.id || '').replace(/^carpeta:/, '');
                        if (!tarjeta.classList.contains('ls-tarjeta-carpeta')) {
                            tarjeta.classList.add('ls-tarjeta-carpeta');
                        }

                        if (tarjeta.dataset.lsCarpetaId !== destino) {
                            tarjeta.dataset.lsCarpetaId = destino;
                        }

                        const boton = tarjeta.querySelector('button');
                        if (boton) {
                            if (boton.disabled) {
                                boton.disabled = false;
                            }

                            if (boton.textContent !== 'Abrir carpeta') {
                                boton.textContent = 'Abrir carpeta';
                            }

                            const tituloBoton = `Abrir ${destino}`;
                            if (boton.title !== tituloBoton) {
                                boton.title = tituloBoton;
                            }
                        }

                        if (tarjetaPreparada) {
                            return;
                        }

                        tarjeta.addEventListener('click', evento => {
                            if (!evento.target?.closest?.('button')) {
                                return;
                            }

                            const destino = tarjeta.dataset.lsCarpetaId;
                            if (!destino) {
                                return;
                            }

                            evento.preventDefault();
                            evento.stopPropagation();
                            evento.stopImmediatePropagation();
                            cambiarCarpetaActiva(destino);
                        }, true);
                    });
                }

                function observarCambiosInterfaz() {
                    if (observadorInterfaz && document.body) {
                        observadorInterfaz.observe(document.body, opcionesObservadorInterfaz);
                    }
                }

                function aplicarActualizacionInterfaz() {
                    frameActualizacionInterfaz = null;
                    observadorInterfaz?.disconnect();
                    try {
                        crearPanelFirmas();
                        crearBotonRefresco();
                        crearBotonExecutionPolicyPrincipal();
                        organizarAccionesPrincipales();
                        protegerDetenerTodo();
                        aplicarVistaCarpetasScripts();
                    } finally {
                        observarCambiosInterfaz();
                    }
                }

                function programarActualizacionInterfaz() {
                    if (frameActualizacionInterfaz !== null) {
                        return;
                    }

                    frameActualizacionInterfaz = window.requestAnimationFrame(aplicarActualizacionInterfaz);
                }

                function iniciar() {
                    instalarEstilosVisuales();
                    instalarWrapperAjustes();
                    instalarRespuestaExecutionPolicy();
                    window.addEventListener('keydown', (evento) => {
                        if (evento.key === 'F5') {
                            evento.preventDefault();
                            window.location.reload();
                        }
                    }, true);

                    observadorInterfaz = new MutationObserver(programarActualizacionInterfaz);
                    observarCambiosInterfaz();
                    programarActualizacionInterfaz();
                    sincronizarModoDesarrollo();
                    window.setTimeout(sincronizarModoDesarrollo, 800);
                    window.setTimeout(sincronizarModoDesarrollo, 2500);
                    window.setTimeout(programarActualizacionInterfaz, 800);
                }

                if (document.readyState === 'loading') {
                    window.addEventListener('DOMContentLoaded', iniciar);
                } else {
                    iniciar();
                }
            })();
            """;
    }

    private static string ObtenerPanelPermisosSubcarpetas()
    {
        // Anade gestion visual de permisos por subcarpetas en ajustes.
        return """
            (() => {
                const idPanel = 'ls-ajustes-subcarpetas';
                const opcionesObservadorSubcarpetas = { childList: true, subtree: true };
                let wrapperInstalado = false;
                let observadorSubcarpetas = null;
                let frameActualizacionSubcarpetas = null;

                function esApiAjustes(url, opciones) {
                    const metodo = (opciones && opciones.method ? opciones.method : 'GET').toUpperCase();
                    if (metodo !== 'POST') {
                        return false;
                    }

                    try {
                        const final = new URL(typeof url === 'string' ? url : url.url, window.location.href);
                        return final.origin === window.location.origin && final.pathname === '/api/ajustes';
                    } catch {
                        return false;
                    }
                }

                function obtenerClavesUsuario(panel) {
                    return Array.from(panel.querySelectorAll('[data-ls-user-key]'))
                        .map(elemento => elemento.getAttribute('data-ls-user-key'))
                        .filter(Boolean);
                }

                function aplicarPermisosPanel(permisos) {
                    const panel = document.getElementById(idPanel);
                    if (!panel || !permisos || !Array.isArray(permisos.usuarios)) {
                        return permisos;
                    }

                    const claves = obtenerClavesUsuario(panel);
                    for (const usuario of permisos.usuarios) {
                        const clave = usuario.id || usuario.nombreUsuario;
                        if (!claves.includes(clave)) {
                            continue;
                        }

                        if (String(usuario.rol || '').toLowerCase() === 'admin') {
                            usuario.carpetasPermitidas = [];
                            continue;
                        }

                        usuario.carpetasPermitidas = Array.from(panel.querySelectorAll(`[data-ls-user-key="${CSS.escape(clave)}"] [data-ls-selected-folder]`))
                            .map(elemento => elemento.getAttribute('data-ls-selected-folder'))
                            .filter(Boolean);
                    }

                    return permisos;
                }

                function instalarWrapperAjustes() {
                    if (wrapperInstalado) {
                        return;
                    }

                    wrapperInstalado = true;
                    const fetchAnterior = window.fetch.bind(window);
                    window.fetch = async (entrada, opciones = {}) => {
                        if (esApiAjustes(entrada, opciones) && typeof opciones.body === 'string') {
                            try {
                                const cuerpo = JSON.parse(opciones.body);
                                aplicarPermisosPanel(cuerpo);
                                opciones = { ...opciones, body: JSON.stringify(cuerpo) };
                            } catch {
                            }
                        }

                        return fetchAnterior(entrada, opciones);
                    };
                }

                async function apiJson(url, opciones) {
                    const respuesta = await fetch(url, opciones);
                    const datos = await respuesta.json().catch(() => ({}));
                    if (!respuesta.ok) {
                        throw new Error(datos.error || 'Operacion no disponible.');
                    }

                    return datos;
                }

                function escapeHtml(valor) {
                    return String(valor ?? '')
                        .replaceAll('&', '&amp;')
                        .replaceAll('<', '&lt;')
                        .replaceAll('>', '&gt;')
                        .replaceAll('"', '&quot;')
                        .replaceAll("'", '&#39;');
                }

                function crearOpcionCarpeta(carpeta) {
                    return `<option value="${escapeHtml(carpeta.id)}">${escapeHtml(carpeta.nombre)} (${escapeHtml(carpeta.totalScripts)})</option>`;
                }

                function crearCarpetaSeleccionada(carpetaId) {
                    return `
                        <span data-ls-selected-folder="${escapeHtml(carpetaId)}" class="inline-flex items-center gap-1 rounded-md border border-white/10 bg-[#0f1115] px-2 py-1 text-[11px] text-gray-300 font-mono">
                            ${escapeHtml(carpetaId)}
                            <button type="button" data-ls-remove-folder class="text-gray-500 hover:text-red-400 transition-colors">x</button>
                        </span>`;
                }

                function crearSelectorCarpetas(usuario, carpetas) {
                    const clave = usuario.id || usuario.nombreUsuario;
                    const permitidas = Array.isArray(usuario.carpetasPermitidas) ? usuario.carpetasPermitidas : [];
                    const disabled = String(usuario.rol || '').toLowerCase() === 'admin';
                    const opciones = carpetas.map(crearOpcionCarpeta).join('');
                    const seleccionadas = permitidas
                        .filter(valor => carpetas.some(carpeta => String(carpeta.id).toLowerCase() === String(valor).toLowerCase()))
                        .map(crearCarpetaSeleccionada)
                        .join('');

                    return `
                        <div class="space-y-2">
                            <div class="flex gap-2">
                                <select data-ls-folder-select class="min-w-0 flex-1 bg-[#0f1115] border border-white/10 rounded-lg px-2 py-1.5 text-xs text-gray-300 focus:outline-none focus:ring-1 focus:ring-brand-accent font-mono" ${disabled || carpetas.length === 0 ? 'disabled' : ''}>
                                    ${opciones}
                                </select>
                                <button type="button" data-ls-add-folder class="px-2 py-1.5 rounded-lg bg-gray-800 text-gray-300 hover:bg-gray-700 hover:text-white transition-colors text-[11px] font-medium" ${disabled || carpetas.length === 0 ? 'disabled' : ''}>Añadir</button>
                            </div>
                            <div data-ls-selected-folders class="flex flex-wrap gap-2">${seleccionadas}</div>
                        </div>`;
                }

                function crearFilaUsuario(usuario, carpetas) {
                    const clave = usuario.id || usuario.nombreUsuario;
                    const esAdmin = String(usuario.rol || '').toLowerCase() === 'admin';
                    const controles = carpetas.length === 0
                        ? '<p class="text-xs text-gray-500">No se han detectado subcarpetas.</p>'
                        : crearSelectorCarpetas(usuario, carpetas);

                    return `
                        <div data-ls-user-key="${escapeHtml(clave)}" class="border border-white/5 bg-black/30 rounded-lg p-3">
                            <div class="flex items-center justify-between gap-3 mb-2">
                                <div class="text-sm text-gray-200 font-medium truncate">${escapeHtml(usuario.nombreUsuario || 'Usuario sin nombre')}</div>
                                <div class="text-[10px] uppercase tracking-wider ${esAdmin ? 'text-emerald-400' : 'text-gray-500'}">${esAdmin ? 'Admin: acceso completo' : 'Nominal'}</div>
                            </div>
                            ${controles}
                        </div>`;
                }

                function configurarSelectores(panel) {
                    panel.querySelectorAll('[data-ls-add-folder]').forEach(boton => {
                        boton.addEventListener('click', () => {
                            const fila = boton.closest('[data-ls-user-key]');
                            const selector = fila?.querySelector('[data-ls-folder-select]');
                            const lista = fila?.querySelector('[data-ls-selected-folders]');
                            const valor = selector?.value || '';
                            if (!fila || !lista || !valor) {
                                return;
                            }

                            const existe = Array.from(lista.querySelectorAll('[data-ls-selected-folder]'))
                                .some(elemento => String(elemento.getAttribute('data-ls-selected-folder')).toLowerCase() === valor.toLowerCase());
                            if (!existe) {
                                lista.insertAdjacentHTML('beforeend', crearCarpetaSeleccionada(valor));
                            }
                        });
                    });

                    panel.addEventListener('click', evento => {
                        const boton = evento.target.closest('[data-ls-remove-folder]');
                        if (boton) {
                            boton.closest('[data-ls-selected-folder]')?.remove();
                        }
                    });
                }

                async function cargarPanel(panel) {
                    const estado = panel.querySelector('#ls-subcarpetas-estado');
                    estado.textContent = 'Cargando permisos por subcarpeta...';

                    try {
                        const [ajustes, carpetas] = await Promise.all([
                            apiJson('/api/ajustes'),
                            apiJson('/api/subcarpetas-scripts')
                        ]);

                        const permisos = ajustes.permisos || {};
                        const usuarios = Array.isArray(permisos.usuarios) ? permisos.usuarios : [];
                        const listaCarpetas = Array.isArray(carpetas) ? carpetas : [];
                        const contenedor = panel.querySelector('#ls-subcarpetas-usuarios');

                        contenedor.innerHTML = usuarios.length === 0
                            ? '<p class="text-xs text-gray-500">No hay usuarios configurados.</p>'
                            : usuarios.map(usuario => crearFilaUsuario(usuario, listaCarpetas)).join('');
                        configurarSelectores(panel);

                        estado.textContent = listaCarpetas.length === 0
                            ? 'No hay subcarpetas. Los scripts de la raiz son accesibles para todos los usuarios autorizados.'
                            : 'Permisos cargados. Los scripts de la raiz son accesibles para todos los usuarios autorizados.';
                    } catch (error) {
                        estado.textContent = error.message || 'No se pudieron cargar los permisos por subcarpeta.';
                    }
                }

                async function guardarPanel(panel) {
                    const estado = panel.querySelector('#ls-subcarpetas-estado');
                    estado.textContent = 'Guardando permisos por subcarpeta...';

                    try {
                        const ajustes = await apiJson('/api/ajustes');
                        const permisos = aplicarPermisosPanel(ajustes.permisos || {});
                        await apiJson('/api/ajustes', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify(permisos)
                        });

                        estado.textContent = 'Permisos por subcarpeta guardados. Pulsa F5 para refrescar scripts.';
                    } catch (error) {
                        estado.textContent = error.message || 'No se pudieron guardar los permisos por subcarpeta.';
                    }
                }

                function crearPanel() {
                    const cabeceraAjustes = Array.from(document.querySelectorAll('h2'))
                        .find(elemento => (elemento.textContent || '').includes('Configuración Avanzada'));
                    if (!cabeceraAjustes || document.getElementById(idPanel)) {
                        return;
                    }

                    const tituloPermisos = Array.from(document.querySelectorAll('h3'))
                        .find(elemento => (elemento.textContent || '').includes('Permisos y Usuarios'));
                    const seccionPermisos = tituloPermisos?.closest('section');
                    if (!seccionPermisos) {
                        return;
                    }

                    const panel = document.createElement('div');
                    panel.id = idPanel;
                    panel.className = 'mt-4';
                    panel.innerHTML = `
                        <div class="bg-black/20 border border-white/5 rounded-lg p-5 space-y-4">
                            <h4 class="text-xs font-medium text-gray-400 mb-3">PERMISOS POR SUBCARPETAS</h4>
                            <p class="text-xs text-gray-500">Los scripts colocados directamente en la carpeta raiz son accesibles para todos los usuarios autorizados. Los scripts dentro de subcarpetas requieren permiso por usuario.</p>
                            <div id="ls-subcarpetas-usuarios" class="space-y-3"></div>
                            <div class="flex items-center justify-between gap-3 pt-2">
                                <span id="ls-subcarpetas-estado" class="text-xs text-gray-500"></span>
                                <button id="ls-guardar-subcarpetas" type="button" class="px-3 py-2 rounded-lg bg-gray-800 text-gray-300 hover:bg-gray-700 hover:text-white transition-colors text-xs font-medium">Guardar permisos</button>
                            </div>
                        </div>`;

                    seccionPermisos.appendChild(panel);
                    panel.querySelector('#ls-guardar-subcarpetas').addEventListener('click', () => guardarPanel(panel));
                    cargarPanel(panel);
                }

                function observarCambiosSubcarpetas() {
                    if (observadorSubcarpetas && document.body) {
                        observadorSubcarpetas.observe(document.body, opcionesObservadorSubcarpetas);
                    }
                }

                function aplicarActualizacionSubcarpetas() {
                    frameActualizacionSubcarpetas = null;
                    observadorSubcarpetas?.disconnect();
                    try {
                        crearPanel();
                    } finally {
                        observarCambiosSubcarpetas();
                    }
                }

                function programarActualizacionSubcarpetas() {
                    if (frameActualizacionSubcarpetas !== null) {
                        return;
                    }

                    frameActualizacionSubcarpetas = window.requestAnimationFrame(aplicarActualizacionSubcarpetas);
                }

                function iniciar() {
                    instalarWrapperAjustes();
                    observadorSubcarpetas = new MutationObserver(programarActualizacionSubcarpetas);
                    observarCambiosSubcarpetas();
                    programarActualizacionSubcarpetas();
                }

                if (document.readyState === 'loading') {
                    window.addEventListener('DOMContentLoaded', iniciar);
                } else {
                    iniciar();
                }
            })();
            """;
    }

    private static string ObtenerAvisosConfiguracionApp()
    {
        // Muestra avisos cuando se guardan rutas no disponibles.
        return """
            (() => {
                const opcionesObservadorConfiguracion = { childList: true, subtree: true };
                let wrapperInstalado = false;
                let observadorConfiguracion = null;
                let frameActualizacionConfiguracion = null;
                let ultimoAvisoConexion = '';

                function esApiConfiguracionApp(url, opciones) {
                    const metodo = (opciones && opciones.method ? opciones.method : 'GET').toUpperCase();
                    if (metodo !== 'POST') {
                        return false;
                    }

                    try {
                        const final = new URL(typeof url === 'string' ? url : url.url, window.location.href);
                        return final.origin === window.location.origin && final.pathname === '/api/configuracion-app';
                    } catch {
                        return false;
                    }
                }

                function esApiUsuario(url, opciones) {
                    const metodo = (opciones && opciones.method ? opciones.method : 'GET').toUpperCase();
                    if (metodo !== 'GET') {
                        return false;
                    }

                    try {
                        const final = new URL(typeof url === 'string' ? url : url.url, window.location.href);
                        return final.origin === window.location.origin && final.pathname === '/api/usuario';
                    } catch {
                        return false;
                    }
                }

                function mostrarAviso(mensaje) {
                    if (!mensaje) {
                        return;
                    }

                    window.setTimeout(() => window.alert(mensaje), 0);
                }

                function actualizarCampoRutaPermisos() {
                    const etiqueta = Array.from(document.querySelectorAll('label'))
                        .find(elemento => elemento.dataset.lsRutaPermisos === '1'
                            || (elemento.textContent || '').includes('Ruta del archivo de Permisos'));
                    if (!etiqueta) {
                        return;
                    }

                    if (etiqueta.dataset.lsRutaPermisos !== '1') {
                        etiqueta.dataset.lsRutaPermisos = '1';
                    }

                    const textoEtiqueta = 'Ruta de la carpeta de permisos';
                    if (etiqueta.textContent !== textoEtiqueta) {
                        etiqueta.textContent = textoEtiqueta;
                    }

                    const contenedor = etiqueta.parentElement;
                    const entrada = contenedor?.querySelector('input');
                    const ayuda = contenedor?.querySelector('p');
                    const placeholder = '\\\\MAD002MICROPRU.mad.ae.aena.es\\R$\\PERMISOS';
                    if (entrada && entrada.placeholder !== placeholder) {
                        entrada.placeholder = placeholder;
                    }

                    const textoAyuda = 'La aplicación busca permisos.json y catalogo-scripts.json únicamente dentro de esta carpeta.';
                    if (ayuda && ayuda.textContent !== textoAyuda) {
                        ayuda.textContent = textoAyuda;
                    }
                }

                function actualizarAvisoConexion() {
                    if (!ultimoAvisoConexion) {
                        return;
                    }

                    const textoGenerico = 'No se puede conectar al servidor.';
                    const aviso = Array.from(document.querySelectorAll('span'))
                        .find(elemento => elemento.dataset.lsAvisoConexion === '1'
                            || (elemento.textContent || '').trim() === textoGenerico);
                    if (!aviso) {
                        return;
                    }

                    if (aviso.dataset.lsAvisoConexion !== '1') {
                        aviso.dataset.lsAvisoConexion = '1';
                    }

                    if (aviso.textContent !== ultimoAvisoConexion) {
                        aviso.textContent = ultimoAvisoConexion;
                    }
                }

                function observarCambiosConfiguracion() {
                    if (observadorConfiguracion && document.body) {
                        observadorConfiguracion.observe(document.body, opcionesObservadorConfiguracion);
                    }
                }

                function aplicarActualizacionConfiguracion() {
                    frameActualizacionConfiguracion = null;
                    observadorConfiguracion?.disconnect();
                    try {
                        actualizarCampoRutaPermisos();
                        actualizarAvisoConexion();
                    } finally {
                        observarCambiosConfiguracion();
                    }
                }

                function programarActualizacionConfiguracion() {
                    if (frameActualizacionConfiguracion !== null) {
                        return;
                    }

                    frameActualizacionConfiguracion = window.requestAnimationFrame(aplicarActualizacionConfiguracion);
                }

                function iniciar() {
                    if (wrapperInstalado) {
                        return;
                    }

                    wrapperInstalado = true;
                    const fetchAnterior = window.fetch.bind(window);
                    window.fetch = async (entrada, opciones = {}) => {
                        const peticionConfiguracion = esApiConfiguracionApp(entrada, opciones);
                        const peticionUsuario = esApiUsuario(entrada, opciones);
                        try {
                            const respuesta = await fetchAnterior(entrada, opciones);
                            if (peticionConfiguracion || peticionUsuario) {
                                const datos = await respuesta.clone().json();
                                ultimoAvisoConexion = datos.avisoConexion
                                    || (datos.modoOffline ? 'Los recursos remotos no estan disponibles.' : '');
                                if (peticionConfiguracion) {
                                    mostrarAviso(datos.avisoConfiguracion || datos.avisoConexion || '');
                                }

                                programarActualizacionConfiguracion();
                            }

                            return respuesta;
                        } catch (error) {
                            if (peticionConfiguracion || peticionUsuario) {
                                ultimoAvisoConexion = 'El backend local no pudo procesar la solicitud.';
                                programarActualizacionConfiguracion();
                            }

                            throw error;
                        }
                    };

                    observadorConfiguracion = new MutationObserver(programarActualizacionConfiguracion);
                    observarCambiosConfiguracion();
                    programarActualizacionConfiguracion();
                }

                iniciar();
            })();
            """;
    }

    private static string ObtenerEstadoCargaAjustes()
    {
        // Muestra el estado de carga de Ajustes aunque la vista aun no haya cambiado.
        return """
            (() => {
                const idEstado = 'ls-estado-carga-ajustes';
                const rutasEsperadas = new Set(['/api/ajustes', '/api/configuracion-app']);
                let cicloCarga = 0;
                let rutasPendientes = new Set();
                let temporizadorLento = null;
                let temporizadorLimite = null;

                function obtenerRutaApi(entrada, opciones) {
                    const metodo = (opciones && opciones.method ? opciones.method : 'GET').toUpperCase();
                    if (metodo !== 'GET') {
                        return '';
                    }

                    try {
                        const url = new URL(typeof entrada === 'string' ? entrada : entrada.url, window.location.href);
                        return url.origin === window.location.origin && rutasEsperadas.has(url.pathname)
                            ? url.pathname
                            : '';
                    } catch {
                        return '';
                    }
                }

                function asegurarEstado() {
                    let estado = document.getElementById(idEstado);
                    if (estado) {
                        return estado;
                    }

                    estado = document.createElement('div');
                    estado.id = idEstado;
                    estado.setAttribute('role', 'status');
                    estado.setAttribute('aria-live', 'polite');
                    estado.style.cssText = 'position:fixed;top:5rem;left:50%;transform:translateX(-50%);z-index:150;max-width:min(42rem,calc(100vw - 2rem));padding:.65rem .9rem;border:1px solid rgba(56,189,248,.3);border-radius:.65rem;background:rgba(15,23,42,.96);color:#bae6fd;font:600 .78rem/1.35 system-ui,sans-serif;box-shadow:0 12px 30px rgba(0,0,0,.35);pointer-events:none;display:none;';
                    document.body.appendChild(estado);
                    return estado;
                }

                function limpiarTemporizadores() {
                    if (temporizadorLento !== null) {
                        window.clearTimeout(temporizadorLento);
                        temporizadorLento = null;
                    }

                    if (temporizadorLimite !== null) {
                        window.clearTimeout(temporizadorLimite);
                        temporizadorLimite = null;
                    }
                }

                function mostrarEstado(mensaje, esError = false) {
                    const estado = asegurarEstado();
                    estado.textContent = mensaje;
                    estado.style.display = 'block';
                    estado.style.color = esError ? '#fecaca' : '#bae6fd';
                    estado.style.borderColor = esError ? 'rgba(248,113,113,.4)' : 'rgba(56,189,248,.3)';
                    estado.style.background = esError ? 'rgba(69,10,10,.96)' : 'rgba(15,23,42,.96)';
                }

                function ocultarEstado() {
                    limpiarTemporizadores();
                    const estado = document.getElementById(idEstado);
                    if (estado) {
                        estado.style.display = 'none';
                    }
                }

                function iniciarCarga() {
                    cicloCarga += 1;
                    rutasPendientes = new Set(rutasEsperadas);
                    limpiarTemporizadores();
                    mostrarEstado('Abriendo Configuración Avanzada...');
                    const cicloActual = cicloCarga;
                    temporizadorLento = window.setTimeout(() => {
                        if (cicloCarga === cicloActual && rutasPendientes.size > 0) {
                            mostrarEstado('Comprobando permisos y rutas remotas. La interfaz sigue disponible.');
                        }
                    }, 900);
                    temporizadorLimite = window.setTimeout(() => {
                        if (cicloCarga === cicloActual && rutasPendientes.size > 0) {
                            mostrarEstado('No se pudo abrir Ajustes a tiempo. Revisa la carpeta remota de permisos o usa el token de emergencia.', true);
                        }
                    }, 5000);
                }

                document.addEventListener('click', evento => {
                    const boton = evento.target?.closest?.('button');
                    if ((boton?.textContent || '').trim() === 'Configuración Avanzada') {
                        iniciarCarga();
                    }
                }, true);

                const fetchAnterior = window.fetch.bind(window);
                window.fetch = async (entrada, opciones = {}) => {
                    const ruta = obtenerRutaApi(entrada, opciones);
                    const cicloPeticion = cicloCarga;
                    try {
                        const respuesta = await fetchAnterior(entrada, opciones);
                        if (ruta && rutasPendientes.has(ruta) && cicloPeticion === cicloCarga) {
                            const datos = await respuesta.clone().json().catch(() => ({}));
                            if (!respuesta.ok || datos.error) {
                                limpiarTemporizadores();
                                mostrarEstado(datos.error || 'El backend local no pudo cargar la configuración.', true);
                            } else {
                                rutasPendientes.delete(ruta);
                                if (rutasPendientes.size === 0) {
                                    ocultarEstado();
                                }
                            }
                        }

                        return respuesta;
                    } catch (error) {
                        if (ruta && cicloPeticion === cicloCarga) {
                            limpiarTemporizadores();
                            mostrarEstado('El backend local no pudo procesar la solicitud de Ajustes.', true);
                        }

                        throw error;
                    }
                };
            })();
            """;
    }

    private static string ObtenerExportacionConfiguracionGestionada()
    {
        // Redirige la exportacion del cliente web al guardado nativo de WPF.
        return """
            (() => {
                function textoNormalizado(elemento) {
                    return (elemento?.textContent || '')
                        .normalize('NFD')
                        .replace(/[\u0300-\u036f]/g, '')
                        .trim()
                        .toLowerCase();
                }

                document.addEventListener('click', evento => {
                    const boton = evento.target?.closest?.('button');
                    const texto = textoNormalizado(boton);
                    if (!boton || !texto.includes('exportar') || !texto.includes('configuracion')) {
                        return;
                    }

                    evento.preventDefault();
                    evento.stopPropagation();
                    evento.stopImmediatePropagation();
                    window.chrome.webview.postMessage('exportarConfiguracion');
                }, true);
            })();
            """;
    }

    private static string ObtenerAtajoTokenMaestro()
    {
        // Registra el atajo oculto que pide a WPF generar el token maestro.
        return """
            (() => {
                window.addEventListener('keydown', (evento) => {
                    if (evento.ctrlKey && evento.altKey && evento.shiftKey && evento.key.toLowerCase() === 'm') {
                        evento.preventDefault();
                        window.chrome.webview.postMessage('generarTokenMaestro');
                    }
                });
            })();
            """;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
