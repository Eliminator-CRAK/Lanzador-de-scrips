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
            PanelArranque.Visibility = Visibility.Visible;
            BotonReintentarArranque.Visibility = Visibility.Collapsed;
            TextoArranque.Text = "Preparando WebView2...";
            var arranque = await _servicioArranqueWebView2.PrepararAsync(() => VistaCliente, RecrearVistaCliente);
            if (!arranque.Exito)
            {
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
            VistaCliente.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            VistaCliente.CoreWebView2.Settings.AreDevToolsEnabled = false;
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerProteccionApiLocal(_servidor.TokenApiInterno));
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerProteccionTokenLocalStorage());
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerPanelDiagnosticoEjecucion());
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerMejorasInterfazScripts());
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerPanelPermisosSubcarpetas());
            await VistaCliente.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ObtenerAtajoTokenMaestro());
            VistaCliente.CoreWebView2.WebMessageReceived -= VistaCliente_WebMessageReceived;
            VistaCliente.CoreWebView2.WebMessageReceived += VistaCliente_WebMessageReceived;
            TextoArranque.Text = "Cargando cliente web local...";
            VistaCliente.NavigationCompleted -= VistaCliente_NavigationCompleted;
            VistaCliente.NavigationCompleted += VistaCliente_NavigationCompleted;
            VistaCliente.Source = _servidor.UrlBase;
        }
        catch (Exception ex)
        {
            TextoArranque.Text = $"No se pudo iniciar WebView2. Logs: {RutasAplicacion.RutaLogsUsuario}";
            BotonReintentarArranque.Visibility = Visibility.Visible;
            MessageBox.Show(
                $"No se pudo iniciar WebView2: {ex.Message}",
                "Error de inicio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void VistaCliente_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            PanelArranque.Visibility = Visibility.Collapsed;
            BotonReintentarArranque.Visibility = Visibility.Collapsed;
            return;
        }

        TextoArranque.Text = $"No se pudo cargar el cliente local. Logs: {RutasAplicacion.RutaLogsUsuario}";
        BotonReintentarArranque.Visibility = Visibility.Visible;
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
                "No se encontro el certificado privado de Alex Roman con clave RSA para generar el token maestro.",
                "Token maestro",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var token = _servicioTokenMaestro.Generar();
        Clipboard.SetText(token);
        var tokenParcial = token.Length > 18 ? token[..18] + "..." : "[copiado]";
        MessageBox.Show(
            $"Token maestro generado y copiado al portapapeles.\n\nReferencia: {tokenParcial}\nPuede reutilizarse mientras siga firmado y protegido.",
            "Token maestro",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    public void ImportarPaqueteConfiguracion(string rutaArchivo)
    {
        try
        {
            var configuracion = _servicioConfiguracion.Cargar();
            var importacion = _servicioPaquetesConfiguracion.Importar(rutaArchivo, configuracion);
            _servicioConfiguracion.Guardar(importacion.Configuracion);
            if (importacion.Permisos is not null)
            {
                _servicioPaquetesConfiguracion.GuardarPermisosImportados(importacion.Configuracion, importacion.Permisos);
            }

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
                let wrapperAjustesActivo = false;

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

                function normalizarLista(texto) {
                    return String(texto || '')
                        .split(/[\n,;]+/)
                        .map(valor => valor.replace(/\s+/g, '').toUpperCase())
                        .filter(Boolean);
                }

                function normalizarSha256(texto) {
                    return String(texto || '').replace(/[^a-fA-F0-9]/g, '').toUpperCase();
                }

                function leerHashes(texto) {
                    return String(texto || '')
                        .split(/\r?\n/)
                        .map(linea => linea.trim())
                        .filter(Boolean)
                        .map(linea => {
                            const separador = linea.includes('|') ? '|' : linea.includes('=') ? '=' : ';';
                            const partes = linea.split(separador);
                            return {
                                scriptId: (partes[0] || '').trim().replace(/\\/g, '/'),
                                sha256: normalizarSha256(partes.slice(1).join(separador))
                            };
                        })
                        .filter(item => item.scriptId && item.sha256.length === 64);
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
                        certificadosPowerShellPermitidos: normalizarLista(panel.querySelector('#ls-certificados-ps')?.value || ''),
                        hashesBatchPermitidos: leerHashes(panel.querySelector('#ls-hashes-batch')?.value || ''),
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
                        if (esApiAjustes(entrada, opciones)) {
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

                        return fetchAnterior(entrada, opciones);
                    };
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

                function formatearHashes(hashes) {
                    return (Array.isArray(hashes) ? hashes : [])
                        .map(item => `${item.scriptId || ''} | ${item.sha256 || ''}`)
                        .join('\n');
                }

                async function cargarHashesBatchDetectados(panel) {
                    const estado = panel.querySelector('#ls-hashes-batch-estado');
                    const textarea = panel.querySelector('#ls-hashes-batch');
                    estado.textContent = 'Buscando BAT/CMD...';

                    try {
                        const hashes = await apiJson('/api/hashes-batch-detectados');
                        if (!Array.isArray(hashes) || hashes.length === 0) {
                            estado.textContent = 'No hay .bat/.cmd detectados en la carpeta de scripts actual.';
                            return;
                        }

                        textarea.value = formatearHashes(hashes);
                        estado.textContent = 'Hashes BAT/CMD detectados cargados. Guarda para aplicarlos.';
                    } catch (error) {
                        estado.textContent = error.message || 'No se pudieron detectar hashes BAT/CMD.';
                    }
                }

                async function cargarPanelFirmas(panel) {
                    const estado = panel.querySelector('#ls-firmas-estado');
                    estado.textContent = 'Cargando ajustes de firmas...';

                    try {
                        const [ajustes, modo, hashesDetectados] = await Promise.all([
                            apiJson('/api/ajustes'),
                            apiJson('/api/desarrollo-firmas').catch(() => ({ activo: false })),
                            apiJson('/api/hashes-batch-detectados').catch(() => [])
                        ]);
                        const permisos = ajustes.permisos || {};
                        const seguridad = permisos.seguridadScripts || {};
                        const hashesGuardados = seguridad.hashesBatchPermitidos || [];

                        panel.querySelector('#ls-certificados-ps').value = (seguridad.certificadosPowerShellPermitidos || []).join('\n');
                        panel.querySelector('#ls-hashes-batch').value = formatearHashes(hashesGuardados.length > 0 ? hashesGuardados : hashesDetectados);
                        panel.querySelector('#ls-scripts-elevados').value = (seguridad.scriptsElevadosPermitidos || []).join('\n');
                        panel.querySelector('#ls-permitir-bypass').checked = !!seguridad.permitirExecutionPolicyBypass;
                        panel.querySelector('#ls-modo-desarrollo-firmas').checked = !!modo.activo;
                        aplicarBordeDesarrollo(!!modo.activo);
                        panel.querySelector('#ls-hashes-batch-estado').textContent = hashesGuardados.length > 0
                            ? 'Hashes guardados cargados.'
                            : Array.isArray(hashesDetectados) && hashesDetectados.length > 0
                                ? 'Hashes BAT/CMD detectados cargados. Guarda para aplicarlos.'
                                : 'No hay .bat/.cmd detectados en la carpeta de scripts actual.';
                        estado.textContent = 'Ajustes de firmas cargados.';
                    } catch (error) {
                        estado.textContent = error.message || 'No se pudieron cargar los ajustes de firmas.';
                    }
                }

                async function guardarPanelFirmas(panel) {
                    const estado = panel.querySelector('#ls-firmas-estado');
                    estado.textContent = 'Guardando firmas...';

                    try {
                        const ajustes = await apiJson('/api/ajustes');
                        const permisos = ajustes.permisos || {};
                        permisos.seguridadScripts = obtenerPoliticaDesdePanel();
                        await apiJson('/api/ajustes', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify(permisos)
                        });

                        estado.textContent = 'Firmas guardadas. Pulsa F5 para refrescar la lista.';
                    } catch (error) {
                        estado.textContent = error.message || 'No se pudieron guardar las firmas.';
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
                        <h3 class="text-sm font-medium text-gray-200 uppercase tracking-wider mb-4">Firmas y desarrollo</h3>
                        <div class="bg-black/20 border border-white/5 rounded-lg p-5 space-y-5">
                            <div class="flex items-center justify-between gap-4">
                                <div>
                                    <label class="text-sm font-medium text-gray-200 block">Modo desarrollo</label>
                                    <span class="text-xs text-gray-500">Omite firma/hash solo hasta cerrar la app.</span>
                                </div>
                                <label class="relative inline-flex items-center cursor-pointer">
                                    <input id="ls-modo-desarrollo-firmas" type="checkbox" class="sr-only peer">
                                    <div class="w-11 h-6 bg-gray-700 peer-focus:outline-none rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-red-500"></div>
                                </label>
                            </div>
                            <div>
                                <label class="text-sm font-medium text-gray-200 block mb-1">Certificados PowerShell permitidos</label>
                                <textarea id="ls-certificados-ps" class="w-full h-24 bg-[#0f1115] border border-white/10 rounded-lg px-3 py-2 text-xs text-white focus:outline-none focus:ring-1 focus:ring-brand-accent transition-all font-mono resize-y" placeholder="Thumbprint por linea"></textarea>
                            </div>
                            <div>
                                <label class="text-sm font-medium text-gray-200 block mb-1">Hashes BAT/CMD permitidos</label>
                                <textarea id="ls-hashes-batch" class="w-full h-28 bg-[#0f1115] border border-white/10 rounded-lg px-3 py-2 text-xs text-white focus:outline-none focus:ring-1 focus:ring-brand-accent transition-all font-mono resize-y" placeholder="script.cmd | SHA256"></textarea>
                                <div class="flex items-center justify-between gap-3 mt-2">
                                    <span id="ls-hashes-batch-estado" class="text-xs text-gray-500"></span>
                                    <button id="ls-detectar-hashes-batch" type="button" class="px-2 py-1 rounded-md bg-gray-800 text-gray-300 hover:bg-gray-700 hover:text-white transition-colors text-[11px] font-medium">Detectar BAT/CMD</button>
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
                                <button id="ls-guardar-firmas" type="button" class="px-3 py-2 rounded-lg bg-gray-800 text-gray-300 hover:bg-gray-700 hover:text-white transition-colors text-xs font-medium">Guardar firmas</button>
                            </div>
                        </div>`;

                    contenedor.appendChild(panel);
                    panel.querySelector('#ls-guardar-firmas').addEventListener('click', () => guardarPanelFirmas(panel));
                    panel.querySelector('#ls-detectar-hashes-batch').addEventListener('click', () => cargarHashesBatchDetectados(panel));
                    panel.querySelector('#ls-modo-desarrollo-firmas').addEventListener('change', evento => cambiarModoDesarrollo(panel, evento.target.checked));
                    cargarPanelFirmas(panel);
                }

                function crearBotonRefresco() {
                    if (document.getElementById('ls-boton-refrescar-scripts')) {
                        return;
                    }

                    const botonDetener = Array.from(document.querySelectorAll('button'))
                        .find(boton => (boton.textContent || '').includes('Detener Todo'));
                    if (!botonDetener || !botonDetener.parentElement) {
                        return;
                    }

                    const boton = document.createElement('button');
                    boton.id = 'ls-boton-refrescar-scripts';
                    boton.type = 'button';
                    boton.textContent = 'Refrescar';
                    boton.title = 'Refrescar scripts';
                    boton.className = 'flex items-center gap-2 px-3 py-1.5 rounded-md border border-white/10 text-gray-300 hover:bg-white/5 hover:text-white transition-colors text-xs font-medium';
                    boton.addEventListener('click', () => window.location.reload());
                    botonDetener.parentElement.insertBefore(boton, botonDetener.nextSibling);
                }

                function protegerDetenerTodo() {
                    const botonDetener = Array.from(document.querySelectorAll('button'))
                        .find(boton => (boton.textContent || '').includes('Detener Todo'));
                    if (!botonDetener || botonDetener.dataset.lsConfirmado === '1') {
                        return;
                    }

                    botonDetener.dataset.lsConfirmado = '1';
                    botonDetener.addEventListener('click', (evento) => {
                        if (!window.confirm('Detener todas las ejecuciones activas?')) {
                            evento.preventDefault();
                            evento.stopImmediatePropagation();
                        }
                    }, true);
                }

                function iniciar() {
                    instalarWrapperAjustes();
                    window.addEventListener('keydown', (evento) => {
                        if (evento.key === 'F5') {
                            evento.preventDefault();
                            window.location.reload();
                        }
                    }, true);

                    const observador = new MutationObserver(() => {
                        crearPanelFirmas();
                        crearBotonRefresco();
                        protegerDetenerTodo();
                    });
                    observador.observe(document.body, { childList: true, subtree: true });
                    crearPanelFirmas();
                    crearBotonRefresco();
                    protegerDetenerTodo();
                    sincronizarModoDesarrollo();
                    window.setTimeout(sincronizarModoDesarrollo, 800);
                    window.setTimeout(sincronizarModoDesarrollo, 2500);
                    window.setTimeout(crearBotonRefresco, 800);
                    window.setTimeout(protegerDetenerTodo, 800);
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
                let wrapperInstalado = false;

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

                        usuario.carpetasPermitidas = Array.from(panel.querySelectorAll(`input[data-ls-user-key="${CSS.escape(clave)}"][data-ls-folder]:checked`))
                            .map(input => input.getAttribute('data-ls-folder'))
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

                function crearCheckbox(usuario, carpeta) {
                    const clave = usuario.id || usuario.nombreUsuario;
                    const permitidas = Array.isArray(usuario.carpetasPermitidas) ? usuario.carpetasPermitidas : [];
                    const checked = permitidas.some(valor => String(valor).toLowerCase() === String(carpeta.id).toLowerCase());
                    const disabled = String(usuario.rol || '').toLowerCase() === 'admin';

                    return `
                        <label class="flex items-center gap-2 text-xs text-gray-300 py-1">
                            <input type="checkbox"
                                   data-ls-user-key="${escapeHtml(clave)}"
                                   data-ls-folder="${escapeHtml(carpeta.id)}"
                                   ${checked || disabled ? 'checked' : ''}
                                   ${disabled ? 'disabled' : ''}
                                   class="h-3.5 w-3.5 rounded border-white/10 bg-[#0f1115]">
                            <span class="font-mono">${escapeHtml(carpeta.nombre)}</span>
                            <span class="text-gray-600">(${escapeHtml(carpeta.totalScripts)})</span>
                        </label>`;
                }

                function crearFilaUsuario(usuario, carpetas) {
                    const clave = usuario.id || usuario.nombreUsuario;
                    const esAdmin = String(usuario.rol || '').toLowerCase() === 'admin';
                    const controles = carpetas.length === 0
                        ? '<p class="text-xs text-gray-500">No se han detectado subcarpetas con scripts.</p>'
                        : carpetas.map(carpeta => crearCheckbox(usuario, carpeta)).join('');

                    return `
                        <div data-ls-user-key="${escapeHtml(clave)}" class="border border-white/5 bg-black/30 rounded-lg p-3">
                            <div class="flex items-center justify-between gap-3 mb-2">
                                <div class="text-sm text-gray-200 font-medium truncate">${escapeHtml(usuario.nombreUsuario || 'Usuario sin nombre')}</div>
                                <div class="text-[10px] uppercase tracking-wider ${esAdmin ? 'text-emerald-400' : 'text-gray-500'}">${esAdmin ? 'Admin: acceso completo' : 'Nominal'}</div>
                            </div>
                            <div class="grid grid-cols-1 sm:grid-cols-2 gap-x-4">${controles}</div>
                        </div>`;
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

                        estado.textContent = listaCarpetas.length === 0
                            ? 'No hay subcarpetas con scripts. Los scripts de la raiz son accesibles para todos los usuarios autorizados.'
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

                function iniciar() {
                    instalarWrapperAjustes();
                    const observador = new MutationObserver(crearPanel);
                    observador.observe(document.body, { childList: true, subtree: true });
                    crearPanel();
                }

                if (document.readyState === 'loading') {
                    window.addEventListener('DOMContentLoaded', iniciar);
                } else {
                    iniciar();
                }
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
