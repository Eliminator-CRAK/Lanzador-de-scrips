// (Autor: Alex Roman)
// Descripcion: Inicializa el cliente WPF nativo y mantiene su ciclo de vida.

using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using LanzadorScripts.Protocolo;
using LanzadorScripts.Servicios;
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
    private const uint MsgfltAllow = 1;
    private const double GrosorRedimensionVentana = 10;
    private static readonly uint MensajeCerrarMantenimiento = RegisterWindowMessage(
        "LanzadorScripts.CerrarMantenimiento.v1");

    private readonly ServicioLogInicio _servicioLogInicio = new();
    private readonly bool _esPortable = RutasAplicacion.Distribucion.EsPortable;
    private readonly ServicioActualizacionesCliente? _servicioActualizaciones;
    private readonly ServicioIconoBandeja? _servicioIconoBandeja;
    private ServicioClienteNativo? _servidorLocalIntegrado;
    private VentanaAuditoria? _ventanaAuditoria;
    private WindowState _estadoAntesOcultar = WindowState.Normal;
    private bool _inicioProgramado;
    private bool _cargaClienteEnCurso;
    private bool _cierreDefinitivo;
    private bool _cierreEnCurso;
    private bool _confirmacionCierreAbierta;
    private bool _comprobacionActualizacionIniciada;
    private bool _actualizacionEnCurso;
    private int _recursosLiberados;

    public VentanaPrincipal()
    {
        InitializeComponent();
        VistaCliente.SolicitarAuditoria += (_, _) => MostrarAuditoria();
        if (!_esPortable)
        {
            _servicioActualizaciones = new ServicioActualizacionesCliente();
        }

        if (!_esPortable)
        {
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

        PreviewKeyDown += VentanaPrincipal_PreviewKeyDown;
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
        // La portable finaliza; la instalada permanece disponible en la bandeja.
        if (!_cierreDefinitivo)
        {
            e.Cancel = true;
            if (_esPortable)
            {
                SolicitarCierreDesdeVentana();
            }
            else
            {
                OcultarEnSegundoPlano();
            }

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
        _ = ServicioIdentidadBarraTareas.ConfigurarVentana(
            new WindowInteropHelper(this).Handle,
            RutasAplicacion.Distribucion,
            ServicioEjecutableAplicacion.ResolverRutaRelanzable());
        AplicarEstiloNativoVentana();
        InstalarRedimensionNativo();
    }

    private async void CargarClienteAsync()
    {
        // La vista aparece antes de consultar configuracion y permisos centrales.
        if (_cargaClienteEnCurso || _cierreEnCurso) return;
        _cargaClienteEnCurso = true;
        PanelArranque.Visibility = Visibility.Visible;
        BotonReintentarArranque.Visibility = Visibility.Collapsed;
        TextoArranque.Text = "Consultando configuracion y permisos...";
        try
        {
            _servidorLocalIntegrado ??= new ServicioClienteNativo();
            await VistaCliente.InicializarAsync(_servidorLocalIntegrado);
            PanelArranque.Visibility = Visibility.Collapsed;
            PanelArranque.IsHitTestVisible = false;
            IniciarComprobacionActualizacion();
            await _servicioLogInicio.RegistrarAsync("cliente.wpf_listo", "Cliente WPF nativo preparado.");
        }
        catch (Exception ex)
        {
            TextoArranque.Text = ServicioRedaccionSecretos.Sanitizar(ex.Message);
            BotonReintentarArranque.Visibility = Visibility.Visible;
            await _servicioLogInicio.RegistrarExcepcionAsync("cliente.arranque_error", "cliente_nativo", string.Empty, ex);
        }
        finally { _cargaClienteEnCurso = false; }
    }

    private void BotonReintentarArranque_Click(object sender, RoutedEventArgs e) => CargarClienteAsync();

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
        if (_esPortable)
        {
            SolicitarCierreDesdeVentana();
            return;
        }

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
        await SolicitarCierreDefinitivoAsync("bandeja");
    }

    private async void SolicitarCierreDesdeVentana()
    {
        await SolicitarCierreDefinitivoAsync("ventana");
    }

    private async Task SolicitarCierreDefinitivoAsync(string origen)
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

        await CerrarDefinitivamenteAsync(ejecuciones.Count, origen);
    }

    public async void SolicitarCierrePorMantenimiento()
    {
        // Cierra para Windows Installer solo cuando no hay scripts activos.
        if (_cierreEnCurso || _confirmacionCierreAbierta)
        {
            return;
        }

        var ejecuciones = _servidorLocalIntegrado?.ObtenerEjecucionesActivas()
            ?? Array.Empty<EjecucionActivaResumen>();
        if (ejecuciones.Count > 0)
        {
            MostrarVentana(WindowState.Normal);
            MessageBox.Show(
                this,
                $"Windows Installer necesita cerrar LanzadorScripts, pero hay {ejecuciones.Count} script(s) en ejecucion. Finalicelos y vuelva a intentar la operacion.",
                "LanzadorScripts",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            await _servicioLogInicio.RegistrarAsync(
                "aplicacion.cierre_mantenimiento_bloqueado",
                "El cierre de mantenimiento se bloqueo porque hay scripts activos.",
                new Dictionary<string, string?>
                {
                    ["ejecucionesActivas"] = ejecuciones.Count.ToString()
                });
            return;
        }

        await CerrarDefinitivamenteAsync(0, "mantenimiento");
    }

    private async Task CerrarDefinitivamenteAsync(int ejecucionesActivas, string origen)
    {
        // Finaliza recursos locales y entrega un codigo reconocido por el lanzador.
        if (_cierreEnCurso)
        {
            return;
        }

        _cierreEnCurso = true;
        _cierreDefinitivo = true;
        MostrarVentana(WindowState.Normal);
        if (!string.Equals(origen, "actualizacion", StringComparison.Ordinal))
        {
            PanelCierre.Visibility = Visibility.Visible;
            PanelCierre.IsHitTestVisible = true;
        }
        await Dispatcher.Yield(DispatcherPriority.Render);
        await _servicioLogInicio.RegistrarAsync(
            "aplicacion.cierre_confirmado",
            $"Se inicio el cierre definitivo desde {origen}.",
            new Dictionary<string, string?>
            {
                ["ejecucionesActivas"] = ejecucionesActivas.ToString(),
                ["origen"] = origen
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

    private void IniciarComprobacionActualizacion()
    {
        // Consulta una sola vez por proceso y nunca en la portable.
        if (_servicioActualizaciones is null || _comprobacionActualizacionIniciada)
        {
            return;
        }

        _comprobacionActualizacionIniciada = true;
        _ = ComprobarActualizacionAsync();
    }

    private async Task ComprobarActualizacionAsync()
    {
        try
        {
            var resultado = await _servicioActualizaciones!.ConsultarAsync(CancellationToken.None);
            if (!resultado.Disponible || resultado.Actualizacion is null)
            {
                BotonActualizarAplicacion.Visibility = Visibility.Collapsed;
                if (!string.IsNullOrWhiteSpace(resultado.Mensaje))
                {
                    await _servicioLogInicio.RegistrarAsync(
                        "actualizacion.consulta_omitida",
                        ServicioRedaccionSecretos.Sanitizar(resultado.Mensaje));
                }

                return;
            }

            TextoBotonActualizacion.Text = $"Actualizar a {resultado.Actualizacion.Version}";
            BotonActualizarAplicacion.Visibility = Visibility.Visible;
            await _servicioLogInicio.RegistrarAsync(
                "actualizacion.disponible",
                $"Version disponible: {resultado.Actualizacion.Version}.");
        }
        catch (Exception ex)
        {
            // Una comprobacion opcional nunca impide usar la aplicacion.
            BotonActualizarAplicacion.Visibility = Visibility.Collapsed;
            await _servicioLogInicio.RegistrarExcepcionAsync(
                "actualizacion.consulta_error",
                "cliente",
                string.Empty,
                ex);
        }
    }

    private async void BotonActualizarAplicacion_Click(object sender, RoutedEventArgs e)
    {
        if (_servicioActualizaciones is null || _actualizacionEnCurso)
        {
            return;
        }

        var ejecuciones = _servidorLocalIntegrado?.ObtenerEjecucionesActivas()
            ?? Array.Empty<EjecucionActivaResumen>();
        if (ejecuciones.Count > 0)
        {
            MessageBox.Show(
                this,
                $"Hay {ejecuciones.Count} script(s) en ejecución. Finalícelos antes de actualizar.",
                "Actualización pendiente",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _actualizacionEnCurso = true;
        BotonActualizarAplicacion.IsEnabled = false;
        // Bloquea la vista mientras se prepara el actualizador firmado.
        VistaCliente.Visibility = Visibility.Hidden;
        PanelActualizacion.Visibility = Visibility.Visible;
        PanelActualizacion.IsHitTestVisible = true;
        TextoFaseActualizacion.Text = "Comprobando la versión disponible...";
        ProgresoActualizacion.IsIndeterminate = true;
        await Dispatcher.Yield(DispatcherPriority.Render);
        try
        {
            var consulta = await _servicioActualizaciones.ConsultarAsync(CancellationToken.None);
            if (!consulta.Disponible || consulta.Actualizacion is null)
            {
                BotonActualizarAplicacion.Visibility = Visibility.Collapsed;
                throw new InvalidOperationException(
                    "La actualización ya no está disponible en el servidor.");
            }

            var progreso = new Progress<ProgresoActualizacionCliente>(ActualizarProgresoActualizacion);
            var paquete = await _servicioActualizaciones.DescargarYPrepararAsync(
                consulta.Actualizacion,
                progreso,
                CancellationToken.None);
            if (_servidorLocalIntegrado?.ObtenerEjecucionesActivas().Count > 0)
            {
                throw new InvalidOperationException(
                    "Hay scripts en ejecucion. Finalicelos antes de actualizar.");
            }

            TextoFaseActualizacion.Text = "Actualizando...";
            ProgresoActualizacion.IsIndeterminate = true;
            await Dispatcher.Yield(DispatcherPriority.Render);
            _ = ServicioActualizacionesCliente.IniciarActualizador(paquete);
            await _servicioLogInicio.RegistrarAsync(
                "actualizacion.iniciada",
                $"Se entrego la instalacion de la version {paquete.Version} al actualizador firmado.");
            await CerrarDefinitivamenteAsync(0, "actualizacion");
        }
        catch (Exception ex)
        {
            PanelActualizacion.Visibility = Visibility.Collapsed;
            PanelActualizacion.IsHitTestVisible = false;
            VistaCliente.Visibility = Visibility.Visible;
            BotonActualizarAplicacion.IsEnabled = true;
            _actualizacionEnCurso = false;
            await _servicioLogInicio.RegistrarExcepcionAsync(
                "actualizacion.error",
                "cliente",
                string.Empty,
                ex);
            MessageBox.Show(
                this,
                ex.Message,
                "No se pudo actualizar LanzadorScripts",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ActualizarProgresoActualizacion(ProgresoActualizacionCliente progreso)
    {
        TextoFaseActualizacion.Text = progreso.Fase switch
        {
            "Descargando" => "Descargando...",
            "Verificando" => "Verificando...",
            _ => progreso.Fase
        };
        if (progreso.Fase == "Descargando" && progreso.BytesTotales > 0)
        {
            ProgresoActualizacion.IsIndeterminate = false;
            ProgresoActualizacion.Value = Math.Clamp(
                progreso.BytesCompletados * 100d / progreso.BytesTotales,
                0,
                100);
        }
        else
        {
            ProgresoActualizacion.IsIndeterminate = true;
        }
    }

    private async Task LiberarRecursosAsync()
    {
        // Detiene ejecuciones y auditoria fuera del hilo visual.
        if (Interlocked.Exchange(ref _recursosLiberados, 1) != 0)
        {
            return;
        }

        var servidor = _servidorLocalIntegrado;
        _servidorLocalIntegrado = null;
        Exception? errorLiberacion = null;
        try
        {
            _ventanaAuditoria?.Close();
            _ventanaAuditoria = null;
            VistaCliente.Dispose();
        }
        catch (Exception ex)
        {
            errorLiberacion = ex;
        }

        try
        {
            if (servidor is not null)
            {
                await Task.Run(servidor.Dispose).WaitAsync(TimeSpan.FromSeconds(32));
            }
        }
        catch (Exception ex)
        {
            errorLiberacion ??= ex;
        }

        try
        {
            _servicioIconoBandeja?.Dispose();
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
            _ventanaAuditoria?.Close();
            _ventanaAuditoria = null;
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
            servidor?.Dispose();
        }
        catch
        {
        }

        try
        {
            _servicioIconoBandeja?.Dispose();
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
        if (MensajeCerrarMantenimiento != 0)
        {
            // Permite solo el mensaje registrado de cierre desde Windows Installer.
            ChangeWindowMessageFilterEx(
                hwnd,
                MensajeCerrarMantenimiento,
                MsgfltAllow,
                IntPtr.Zero);
        }
    }

    private IntPtr ProcesarMensajeVentana(IntPtr hwnd, int mensaje, IntPtr wParam, IntPtr lParam, ref bool procesado)
    {
        if (MensajeCerrarMantenimiento != 0
            && unchecked((uint)mensaje) == MensajeCerrarMantenimiento)
        {
            procesado = true;
            Dispatcher.BeginInvoke(new Action(SolicitarCierrePorMantenimiento));
            return IntPtr.Zero;
        }

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

    private void VentanaPrincipal_PreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.M
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            e.Handled = true;
            MostrarAuditoria();
        }
    }

    private async void MostrarAuditoria()
    {
        try
        {
            if (_servidorLocalIntegrado is null) return;
            var sesion = await _servidorLocalIntegrado.ObtenerSesionAsync();
            if (!sesion.Usuario.EstaAutorizado || sesion.Usuario.Rol != "admin") return;
        }
        catch (Exception ex)
        {
            await _servicioLogInicio.RegistrarExcepcionAsync("auditoria.autorizacion_error", "cliente_nativo", string.Empty, ex);
            return;
        }
        if (_ventanaAuditoria is { IsLoaded: true })
        {
            _ventanaAuditoria.Activate();
            return;
        }

        _ventanaAuditoria = new VentanaAuditoria
        {
            Owner = this
        };
        _ventanaAuditoria.Closed += (_, _) => _ventanaAuditoria = null;
        _ventanaAuditoria.Show();
    }

    public void ImportarPaqueteConfiguracion(string rutaArchivo)
    {
        // Desde 1.10 la configuracion se administra exclusivamente en el servidor.
        MessageBox.Show(this, "La configuracion se administra desde la consola del servidor.",
            "Configuracion central", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hwnd,
        uint message,
        uint action,
        IntPtr changeFilterStruct);
}
