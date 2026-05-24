// (Autor: Alex Roman)
// Descripcion: Inicializa el cliente web y su backend local.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using LanzadorScripts.Servicios;

namespace LanzadorScripts;

public partial class VentanaPrincipal : Window
{
    private readonly ServidorLocalWeb _servidor;
    private readonly ServicioTokenMaestro _servicioTokenMaestro = new();
    private readonly ServicioConfiguracion _servicioConfiguracion = new();
    private readonly ServicioPaquetesConfiguracion _servicioPaquetesConfiguracion = new();
    private readonly ServicioInstalacionWebView2 _servicioInstalacionWebView2 = new();

    public VentanaPrincipal()
    {
        InitializeComponent();
        _servidor = ServidorLocalWeb.Iniciar();
        CargarClienteAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _servidor.Dispose();
        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        AplicarEstiloNativoVentana();
    }

    private async void CargarClienteAsync()
    {
        try
        {
            Directory.CreateDirectory(RutasAplicacion.RutaPerfilWebView2);

            var runtimeFijo = Directory.Exists(RutasAplicacion.RutaRuntimeWebView2Fijo)
                ? RutasAplicacion.RutaRuntimeWebView2Fijo
                : null;
            var instalacion = await _servicioInstalacionWebView2.AsegurarInstaladoAsync(runtimeFijo);
            if (!instalacion.Exito)
            {
                MessageBox.Show(
                    instalacion.Mensaje,
                    "No se pudo preparar WebView2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
                return;
            }

            var entorno = await CoreWebView2Environment.CreateAsync(runtimeFijo, RutasAplicacion.RutaPerfilWebView2);

            await VistaCliente.EnsureCoreWebView2Async(entorno);
            VistaCliente.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            VistaCliente.CoreWebView2.Settings.AreDevToolsEnabled = false;
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerProteccionTokenLocalStorage());
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerAtajoTokenMaestro());
            VistaCliente.CoreWebView2.WebMessageReceived += VistaCliente_WebMessageReceived;
            VistaCliente.Source = _servidor.UrlBase;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No se pudo iniciar WebView2: {ex.Message}",
                "Error de inicio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
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
        WindowState = WindowState.Minimized;
    }

    private void BotonMaximizar_Click(object sender, RoutedEventArgs e)
    {
        AlternarMaximizado();
    }

    private void BotonCerrar_Click(object sender, RoutedEventArgs e)
    {
        Close();
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

    private void VistaCliente_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.TryGetWebMessageAsString() == "generarTokenMaestro")
        {
            MostrarTokenMaestro();
        }
    }

    private void MostrarTokenMaestro()
    {
        if (!_servicioTokenMaestro.PuedeGenerar())
        {
            MessageBox.Show(
                "Este usuario no tiene el certificado privado de Alex Roman para generar tokens maestros.",
                "Token maestro",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var token = _servicioTokenMaestro.Generar();
        Clipboard.SetText(token);
        MessageBox.Show(
            $"Token maestro generado y copiado al portapapeles:\n\n{token}",
            "Token maestro",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public void ImportarPaqueteConfiguracion(string rutaArchivo)
    {
        try
        {
            var configuracion = _servicioConfiguracion.Cargar();
            var actualizada = _servicioPaquetesConfiguracion.Importar(rutaArchivo, configuracion);
            _servicioConfiguracion.Guardar(actualizada);
            VistaCliente.CoreWebView2?.Reload();
            MessageBox.Show(
                "Configuracion importada correctamente para este usuario.",
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
}
