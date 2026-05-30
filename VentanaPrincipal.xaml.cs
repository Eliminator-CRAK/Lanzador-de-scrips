// (Autor: Alex Roman)
// Descripcion: Inicializa el cliente web y su backend local.

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using LanzadorScripts.Servicios;

namespace LanzadorScripts;

public partial class VentanaPrincipal : Window
{
    private readonly ServidorLocalWeb _servidor;
    private readonly ServicioTokenMaestro _servicioTokenMaestro = new();
    private readonly ServicioConfiguracion _servicioConfiguracion = new();
    private readonly ServicioPaquetesConfiguracion _servicioPaquetesConfiguracion = new();
    private readonly ServicioArranqueWebView2 _servicioArranqueWebView2 = new();

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
            var arranque = await _servicioArranqueWebView2.PrepararAsync(() => VistaCliente, RecrearVistaCliente);
            if (!arranque.Exito)
            {
                MessageBox.Show(
                    arranque.Mensaje,
                    "No se pudo preparar WebView2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
                return;
            }

            VistaCliente.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            VistaCliente.CoreWebView2.Settings.AreDevToolsEnabled = false;
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerProteccionApiLocal(_servidor.TokenApiInterno));
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerProteccionTokenLocalStorage());
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerPanelDiagnosticoEjecucion());
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
                "Sin mi certificado es imposible generar el token :)",
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

    private static string ObtenerProteccionApiLocal(string tokenApi)
    {
        // Inyecta el token local de arranque en fetch y EventSource.
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
                        return;
                    }

                    const tipo = respuesta.headers.get('content-type') || '';
                    if (!tipo.includes('application/json')) {
                        return;
                    }

                    await respuesta.clone().json().then((datos) => {
                        const nuevoToken = datos && (datos.tokenAdmin || datos.TokenAdmin);
                        if (typeof nuevoToken === 'string' && nuevoToken.length > 0) {
                            tokenAdmin = nuevoToken;
                        }
                    }).catch(() => {});
                }

                window.__lanzadorScripts = {
                    obtenerTokenApi: () => tokenApi,
                    obtenerTokenAdmin: () => tokenAdmin
                };

                window.fetch = async (entrada, opciones = {}) => {
                    const url = typeof entrada === 'string' ? entrada : entrada.url;
                    const cabeceras = new Headers(opciones.headers || (entrada && entrada.headers) || {});
                    if (esApiLocal(url)) {
                        cabeceras.set('X-LanzadorScripts-ApiToken', tokenApi);
                        if (tokenAdmin && !cabeceras.has('Authorization')) {
                            cabeceras.set('Authorization', 'Bearer ' + tokenAdmin);
                        }
                    }

                    const respuesta = await fetchOriginal(entrada, { ...opciones, headers: cabeceras });
                    await capturarTokenAdmin(respuesta, url);
                    return respuesta;
                };

                if (typeof eventSourceOriginal === 'function') {
                    window.EventSource = function(url, configuracion) {
                        if (esApiLocal(url)) {
                            const final = new URL(url, window.location.href);
                            final.searchParams.set('apiToken', tokenApi);
                            return new eventSourceOriginal(final.toString(), configuracion);
                        }

                        return new eventSourceOriginal(url, configuracion);
                    };
                }
            })();
            """;
    }

    private static string ObtenerPanelDiagnosticoEjecucion()
    {
        // Añade un panel flotante para consultar diagnostico de ejecucion.
        return """
            (() => {
                window.addEventListener('DOMContentLoaded', () => {
                    if (document.getElementById('ls-diagnostico-boton')) {
                        return;
                    }

                    const boton = document.createElement('button');
                    boton.id = 'ls-diagnostico-boton';
                    boton.textContent = 'Diagnóstico';
                    boton.style.cssText = 'position:fixed;right:18px;bottom:18px;z-index:2147483647;background:#111827;color:#e5e7eb;border:1px solid #374151;border-radius:8px;padding:8px 12px;font:12px Segoe UI,Arial,sans-serif;box-shadow:0 10px 25px rgba(0,0,0,.35);';
                    document.body.appendChild(boton);

                    const panel = document.createElement('div');
                    panel.id = 'ls-diagnostico-panel';
                    panel.style.cssText = 'display:none;position:fixed;right:18px;bottom:58px;width:min(560px,calc(100vw - 36px));max-height:70vh;overflow:auto;z-index:2147483647;background:#111827;color:#e5e7eb;border:1px solid #374151;border-radius:8px;padding:14px;font:12px Segoe UI,Arial,sans-serif;box-shadow:0 10px 35px rgba(0,0,0,.45);';
                    document.body.appendChild(panel);

                    async function cargar() {
                        panel.innerHTML = '<div style="margin-bottom:10px;font-weight:600">Diagnóstico de ejecución</div><div>Cargando scripts...</div>';
                        const scripts = await fetch('/api/scripts').then(r => r.json());
                        const opciones = (Array.isArray(scripts) ? scripts : []).map(s => `<option value="${s.id}">${s.nombre}</option>`).join('');
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

                    boton.addEventListener('click', async () => {
                        panel.style.display = panel.style.display === 'none' ? 'block' : 'none';
                        if (panel.style.display === 'block') {
                            try {
                                await cargar();
                            } catch (error) {
                                panel.innerHTML = '<strong>Diagnóstico de ejecución</strong><pre style="white-space:pre-wrap">No se pudo cargar el diagnóstico.</pre>';
                            }
                        }
                    });
                });
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
