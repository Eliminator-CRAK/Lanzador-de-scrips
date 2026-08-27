// (Autor: Alex Roman)
// Descripcion: Coordina las operaciones de la consola administrativa.

using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LanzadorScripts.Protocolo;
using LanzadorScripts.Servidor.Core;
using Microsoft.Win32;

namespace LanzadorScripts.Servidor.Administracion;

public partial class MainWindow : Window
{
    private readonly ServicioControlWindows _controlServicio = new();
    private readonly RutasServidor _rutas = new();
    private readonly ConfiguracionServidor _configuracion;
    private readonly ClienteAdministracionLocal _cliente;
    private readonly GeneradorCatalogoServidor _generadorCatalogo = new();
    private readonly ObservableCollection<UsuarioServidorCentral> _usuarios = [];
    private readonly ObservableCollection<AuditoriaVista> _auditoria = [];
    private readonly ObservableCollection<CatalogoVista> _catalogo = [];
    private string? _usuarioSeleccionadoId;
    private bool _ocupado;

    public MainWindow()
    {
        InitializeComponent();
        _configuracion = new AlmacenConfiguracionServidor(_rutas).CargarOCrear();
        _cliente = new ClienteAdministracionLocal(TimeSpan.FromSeconds(8));
        TextoCuentaActual.Text = WindowsIdentity.GetCurrent().Name;
        TextoRutasServidor.Text = $"Base: {_rutas.RutaBaseDatos}\nCopias: {_rutas.RutaCopias}\nLogs: {_rutas.RutaLogs}";
        TablaUsuarios.ItemsSource = _usuarios;
        TablaAuditoria.ItemsSource = _auditoria;
        TablaCatalogo.ItemsSource = _catalogo;
        CampoRolUsuario.SelectedIndex = 1;
        CampoRutaScripts.Text = _configuracion.RutaScripts;
        FiltroResultadoAuditoria.SelectedIndex = 0;
        Vistas.SelectedIndex = 0;
        Loaded += MainWindow_Loaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        // Solicita una barra de titulo oscura coherente con la consola.
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var modoOscuro = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref modoOscuro, sizeof(int));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ActualizarResumenAsync();
    }

    private async void ActualizarTodo_Click(object sender, RoutedEventArgs e)
    {
        await ActualizarVistaActualAsync();
    }

    private async void MostrarResumen_Click(object sender, RoutedEventArgs e)
    {
        SeleccionarVista(0, "Resumen del servidor", "Estado operativo y controles principales");
        await ActualizarResumenAsync();
    }

    private async void MostrarUsuarios_Click(object sender, RoutedEventArgs e)
    {
        SeleccionarVista(1, "Usuarios y permisos", "Cuentas autorizadas para administrar o ejecutar scripts");
        await CargarUsuariosAsync();
    }

    private async void MostrarAuditoria_Click(object sender, RoutedEventArgs e)
    {
        SeleccionarVista(2, "Auditoría", "Consulta central por usuario, fecha, resultado y script");
        await CargarAuditoriaAsync();
    }

    private async void MostrarCatalogo_Click(object sender, RoutedEventArgs e)
    {
        SeleccionarVista(3, "Catálogo de scripts", "Hashes autorizados almacenados en la base central");
        await CargarCatalogoAsync();
    }

    private void MostrarMantenimiento_Click(object sender, RoutedEventArgs e)
    {
        SeleccionarVista(4, "Mantenimiento", "Integridad, copias y ciclo de vida del servicio");
    }

    private async Task ActualizarVistaActualAsync()
    {
        switch (Vistas.SelectedIndex)
        {
            case 0:
                await ActualizarResumenAsync();
                break;
            case 1:
                await CargarUsuariosAsync();
                break;
            case 2:
                await CargarAuditoriaAsync();
                break;
            case 3:
                await CargarCatalogoAsync();
                break;
            case 4:
                await ComprobarIntegridadAsync();
                break;
        }
    }

    private async Task ActualizarResumenAsync()
    {
        await EjecutarOperacionAsync("Comprobando el servidor...", async () =>
        {
            var estadoServicio = await Task.Run(_controlServicio.ObtenerEstado);
            AplicarEstadoServicio(estadoServicio);
            if (!estadoServicio.EnEjecucion)
            {
                LimpiarMetricas();
                return;
            }

            var respuesta = await _cliente.EnviarAsync<object, EstadoServidorCentral>(
                OperacionesServidor.Salud,
                new { },
                CancellationToken.None);
            if (!respuesta.Exito || respuesta.Datos is null)
            {
                TextoEstadoConexion.Text = respuesta.Mensaje;
                TextoEstadoConexion.Foreground = (Brush)FindResource("Rojo");
                LimpiarMetricas();
                return;
            }

            var estado = respuesta.Datos;
            AplicarEstadoAutenticacion(estado);
            MetricaBase.Text = estado.BaseIntegra ? "Íntegra" : "Revisar";
            MetricaBase.Foreground = estado.BaseIntegra
                ? (Brush)FindResource("Verde")
                : (Brush)FindResource("Rojo");
            MetricaUsuarios.Text = estado.TotalUsuarios.ToString("N0");
            MetricaAuditoria.Text = estado.TotalAuditorias.ToString("N0");
            MetricaPuerto.Text = estado.Puerto.ToString();
            TextoUltimaAuditoria.Text = estado.UltimaAuditoriaUtc.HasValue
                ? estado.UltimaAuditoriaUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")
                : "Todavía no hay eventos de auditoría.";
        });
    }

    private async Task CargarUsuariosAsync()
    {
        await EjecutarOperacionAsync("Cargando usuarios...", async () =>
        {
            var respuesta = await _cliente.EnviarAsync<object, List<UsuarioServidorCentral>>(
                OperacionesServidor.ListarUsuarios,
                new { },
                CancellationToken.None);
            ExigirRespuesta(respuesta);
            _usuarios.Clear();
            foreach (var usuario in respuesta.Datos!)
            {
                _usuarios.Add(usuario);
            }
        });
    }

    private async Task CargarAuditoriaAsync()
    {
        await EjecutarOperacionAsync("Consultando auditoría...", async () =>
        {
            var usuario = FiltroUsuarioAuditoria.SelectedItem as string;
            if (string.Equals(usuario, "Todos", StringComparison.OrdinalIgnoreCase))
            {
                usuario = null;
            }

            var resultado = ObtenerContenidoCombo(FiltroResultadoAuditoria);
            if (string.Equals(resultado, "Todos", StringComparison.OrdinalIgnoreCase))
            {
                resultado = null;
            }

            var filtro = new FiltroAuditoriaServidorCentral(
                usuario,
                ConvertirInicioDiaUtc(FiltroDesdeAuditoria.SelectedDate),
                ConvertirFinDiaUtc(FiltroHastaAuditoria.SelectedDate),
                resultado,
                string.IsNullOrWhiteSpace(FiltroScriptAuditoria.Text)
                    ? null
                    : FiltroScriptAuditoria.Text.Trim(),
                1000,
                0);
            var respuesta = await _cliente.EnviarAsync<FiltroAuditoriaServidorCentral, PaginaAuditoriaServidorCentral>(
                OperacionesServidor.ConsultarAuditoria,
                filtro,
                CancellationToken.None);
            ExigirRespuesta(respuesta);
            var pagina = respuesta.Datos!;
            var seleccionado = FiltroUsuarioAuditoria.SelectedItem as string ?? "Todos";
            FiltroUsuarioAuditoria.Items.Clear();
            FiltroUsuarioAuditoria.Items.Add("Todos");
            foreach (var cuenta in pagina.Usuarios)
            {
                FiltroUsuarioAuditoria.Items.Add(cuenta);
            }

            FiltroUsuarioAuditoria.SelectedItem = FiltroUsuarioAuditoria.Items
                .Cast<object>()
                .FirstOrDefault(item => string.Equals(
                    Convert.ToString(item),
                    seleccionado,
                    StringComparison.OrdinalIgnoreCase))
                ?? "Todos";
            _auditoria.Clear();
            foreach (var evento in pagina.Eventos)
            {
                _auditoria.Add(new AuditoriaVista(
                    evento.FechaLocal,
                    evento.UsuarioWindows,
                    evento.Equipo,
                    evento.Accion,
                    evento.ScriptNombre ?? evento.ScriptId ?? string.Empty,
                    evento.Resultado,
                    evento.CodigoSalida,
                    string.IsNullOrWhiteSpace(evento.Detalle) ? evento.Motivo : evento.Detalle));
            }
        });
    }

    private async Task CargarCatalogoAsync()
    {
        await EjecutarOperacionAsync("Cargando catálogo...", async () =>
        {
            await CargarCatalogoSinEstadoAsync();
        });
    }

    private async Task CargarCatalogoSinEstadoAsync()
    {
        // Actualiza el catálogo incluso dentro de otra operación administrativa.
        var respuesta = await _cliente.EnviarAsync<object, CatalogoServidorCentral>(
            OperacionesServidor.ObtenerCatalogo,
            new { },
            CancellationToken.None);
        ExigirRespuesta(respuesta);
        var datos = respuesta.Datos!;
        _catalogo.Clear();
        if (datos.Catalogo["scripts"] is JsonArray scripts)
        {
            foreach (var nodo in scripts.OfType<JsonObject>())
            {
                _catalogo.Add(new CatalogoVista(
                    nodo["scriptId"]?.GetValue<string>() ?? string.Empty,
                    nodo["extension"]?.GetValue<string>() ?? string.Empty,
                    nodo["longitud"]?.GetValue<long>() ?? 0,
                    nodo["sha256"]?.GetValue<string>() ?? string.Empty));
            }
        }

        TextoEstadoCatalogo.Text = $"Conjunto {datos.ConjuntoId} · revisión {datos.Revision} · {_catalogo.Count} scripts";
    }

    private async void InstalarServicio_Click(object sender, RoutedEventArgs e)
    {
        await EjecutarOperacionAsync("Instalando el servicio...", async () =>
        {
            await Task.Run(() =>
            {
                _controlServicio.Instalar(_configuracion.Puerto);
                _controlServicio.Iniciar();
            });
            await Task.Delay(1000);
            await ActualizarResumenSinEstadoAsync();
        });
    }

    private async void IniciarServicio_Click(object sender, RoutedEventArgs e)
    {
        await ControlarServicioAsync("Iniciando el servicio...", _controlServicio.Iniciar);
    }

    private async void DetenerServicio_Click(object sender, RoutedEventArgs e)
    {
        await ControlarServicioAsync("Deteniendo el servicio...", _controlServicio.Detener);
    }

    private async void ReiniciarServicio_Click(object sender, RoutedEventArgs e)
    {
        await ControlarServicioAsync("Reiniciando el servicio...", _controlServicio.Reiniciar);
    }

    private async void DesinstalarServicio_Click(object sender, RoutedEventArgs e)
    {
        var respuesta = MessageBox.Show(
            "Se desinstalará el servicio Windows. La base de datos y las copias se conservarán.",
            "Desinstalar servicio",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (respuesta != MessageBoxResult.Yes)
        {
            return;
        }

        await EjecutarOperacionAsync("Desinstalando el servicio...", async () =>
        {
            await Task.Run(_controlServicio.Desinstalar);
            await ActualizarResumenSinEstadoAsync();
        });
    }

    private async Task ControlarServicioAsync(string estado, Action operacion)
    {
        await EjecutarOperacionAsync(estado, async () =>
        {
            await Task.Run(operacion);
            await Task.Delay(750);
            await ActualizarResumenSinEstadoAsync();
        });
    }

    private async Task ActualizarResumenSinEstadoAsync()
    {
        var estadoServicio = await Task.Run(_controlServicio.ObtenerEstado);
        AplicarEstadoServicio(estadoServicio);
        if (!estadoServicio.EnEjecucion)
        {
            LimpiarMetricas();
            return;
        }

        var respuesta = await _cliente.EnviarAsync<object, EstadoServidorCentral>(
            OperacionesServidor.Salud,
            new { },
            CancellationToken.None);
        if (respuesta.Exito && respuesta.Datos is not null)
        {
            AplicarEstadoAutenticacion(respuesta.Datos);
            MetricaBase.Text = respuesta.Datos.BaseIntegra ? "Íntegra" : "Revisar";
            MetricaUsuarios.Text = respuesta.Datos.TotalUsuarios.ToString("N0");
            MetricaAuditoria.Text = respuesta.Datos.TotalAuditorias.ToString("N0");
            MetricaPuerto.Text = respuesta.Datos.Puerto.ToString();
        }
        else
        {
            TextoEstadoConexion.Text = respuesta.Mensaje;
            TextoEstadoConexion.Foreground = (Brush)FindResource("Rojo");
        }
    }

    private void AplicarEstadoAutenticacion(EstadoServidorCentral estado)
    {
        if (estado.AutenticacionRemotaPreparada)
        {
            TextoEstadoConexion.Text =
                $"Canal local disponible. Kerberos remoto preparado: {estado.SpnServidor}.";
            TextoEstadoConexion.Foreground = (Brush)FindResource("Verde");
            return;
        }

        TextoEstadoConexion.Text =
            $"Canal local disponible, pero Kerberos remoto no esta preparado: "
            + estado.MensajeAutenticacion;
        TextoEstadoConexion.Foreground = (Brush)FindResource("Ambar");
    }

    private void TablaUsuarios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TablaUsuarios.SelectedItem is not UsuarioServidorCentral usuario)
        {
            return;
        }

        _usuarioSeleccionadoId = usuario.Id;
        CampoCuentaUsuario.Text = usuario.NombreUsuario;
        CampoRolUsuario.SelectedIndex = usuario.Rol == "admin" ? 0 : 1;
        CampoMaximoUsuario.Text = usuario.MaxScriptsSimultaneos.ToString();
        CampoCarpetasUsuario.Text = string.Join(Environment.NewLine, usuario.CarpetasPermitidas);
        CampoUsuarioActivo.IsChecked = usuario.Activo;
    }

    private void NuevoUsuario_Click(object sender, RoutedEventArgs e)
    {
        LimpiarEditorUsuario();
    }

    private async void GuardarUsuario_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CampoMaximoUsuario.Text, out var maximo))
        {
            MostrarError("El máximo de ejecuciones debe ser un número entero.");
            return;
        }

        var carpetas = CampoCarpetasUsuario.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var solicitud = new GuardarUsuarioServidorCentral(
            _usuarioSeleccionadoId,
            CampoCuentaUsuario.Text.Trim(),
            ObtenerContenidoCombo(CampoRolUsuario) ?? "nominal",
            maximo,
            carpetas,
            CampoUsuarioActivo.IsChecked == true);
        await EjecutarOperacionAsync("Guardando usuario...", async () =>
        {
            var respuesta = await _cliente.EnviarAsync<GuardarUsuarioServidorCentral, UsuarioServidorCentral>(
                OperacionesServidor.GuardarUsuario,
                solicitud,
                CancellationToken.None);
            ExigirRespuesta(respuesta);
            await CargarUsuariosSinEstadoAsync();
            LimpiarEditorUsuario();
        });
    }

    private async void EliminarUsuario_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_usuarioSeleccionadoId))
        {
            MostrarError("Selecciona un usuario antes de eliminarlo.");
            return;
        }

        if (MessageBox.Show(
                "¿Eliminar la cuenta seleccionada de la base central?",
                "Eliminar usuario",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var id = _usuarioSeleccionadoId;
        await EjecutarOperacionAsync("Eliminando usuario...", async () =>
        {
            var respuesta = await _cliente.EnviarAsync<EliminarUsuarioServidorCentral, bool>(
                OperacionesServidor.EliminarUsuario,
                new EliminarUsuarioServidorCentral(id),
                CancellationToken.None);
            ExigirRespuesta(respuesta);
            await CargarUsuariosSinEstadoAsync();
            LimpiarEditorUsuario();
        });
    }

    private async Task CargarUsuariosSinEstadoAsync()
    {
        var respuesta = await _cliente.EnviarAsync<object, List<UsuarioServidorCentral>>(
            OperacionesServidor.ListarUsuarios,
            new { },
            CancellationToken.None);
        ExigirRespuesta(respuesta);
        _usuarios.Clear();
        foreach (var usuario in respuesta.Datos!)
        {
            _usuarios.Add(usuario);
        }
    }

    private async void BuscarAuditoria_Click(object sender, RoutedEventArgs e)
    {
        await CargarAuditoriaAsync();
    }

    private async void CargarCatalogo_Click(object sender, RoutedEventArgs e)
    {
        await CargarCatalogoAsync();
    }

    private void SeleccionarCarpetaScripts_Click(object sender, RoutedEventArgs e)
    {
        var dialogo = new OpenFolderDialog
        {
            Title = "Selecciona la carpeta local de scripts",
            InitialDirectory = Directory.Exists(CampoRutaScripts.Text)
                ? CampoRutaScripts.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            Multiselect = false
        };
        if (dialogo.ShowDialog(this) == true)
        {
            CampoRutaScripts.Text = dialogo.FolderName;
        }
    }

    private async void RegenerarCatalogo_Click(object sender, RoutedEventArgs e)
    {
        var ruta = CampoRutaScripts.Text.Trim();
        if (MessageBox.Show(
                $"Se calcularán de nuevo los hashes de todos los scripts de:\n\n{ruta}\n\nLa base actual se copiará antes del cambio.",
                "Recrear catálogo",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await EjecutarOperacionAsync("Recreando el catálogo...", async () =>
        {
            var actual = await _cliente.EnviarAsync<object, CatalogoServidorCentral>(
                OperacionesServidor.ObtenerCatalogo,
                new { },
                CancellationToken.None);
            ExigirRespuesta(actual);
            var catalogo = await Task.Run(() => _generadorCatalogo.Generar(
                ruta,
                actual.Datos!.ConjuntoId));
            var copia = await _cliente.EnviarAsync<object, ResultadoCopiaServidorCentral>(
                OperacionesServidor.CrearCopiaSeguridad,
                new { },
                CancellationToken.None);
            ExigirRespuesta(copia);
            var guardado = await _cliente.EnviarAsync<JsonObject, CatalogoServidorCentral>(
                OperacionesServidor.GuardarCatalogo,
                catalogo,
                CancellationToken.None);
            ExigirRespuesta(guardado);
            _configuracion.RutaScripts = ruta;
            new AlmacenConfiguracionServidor(_rutas).Guardar(_configuracion);
            CampoRutaScripts.Text = _configuracion.RutaScripts;
            await CargarCatalogoSinEstadoAsync();
        });
    }

    private async void ComprobarIntegridad_Click(object sender, RoutedEventArgs e)
    {
        await ComprobarIntegridadAsync();
    }

    private async Task ComprobarIntegridadAsync()
    {
        await EjecutarOperacionAsync("Comprobando la base...", async () =>
        {
            var respuesta = await _cliente.EnviarAsync<object, ResultadoIntegridadServidorCentral>(
                OperacionesServidor.ComprobarIntegridad,
                new { },
                CancellationToken.None);
            ExigirRespuesta(respuesta);
            TextoIntegridad.Text = respuesta.Datos!.Mensaje;
            TextoIntegridad.Foreground = respuesta.Datos.Integra
                ? (Brush)FindResource("Verde")
                : (Brush)FindResource("Rojo");
        });
    }

    private async void CrearCopia_Click(object sender, RoutedEventArgs e)
    {
        await EjecutarOperacionAsync("Creando copia de seguridad...", async () =>
        {
            var respuesta = await _cliente.EnviarAsync<object, ResultadoCopiaServidorCentral>(
                OperacionesServidor.CrearCopiaSeguridad,
                new { },
                CancellationToken.None);
            ExigirRespuesta(respuesta);
            TextoCopia.Text = $"Copia creada: {respuesta.Datos!.NombreArchivo} ({respuesta.Datos.Longitud:N0} bytes).";
        });
    }

    private void SeleccionarVista(int indice, string titulo, string subtitulo)
    {
        Vistas.SelectedIndex = indice;
        TituloVista.Text = titulo;
        SubtituloVista.Text = subtitulo;
    }

    private async Task EjecutarOperacionAsync(string estado, Func<Task> operacion)
    {
        if (_ocupado)
        {
            return;
        }

        _ocupado = true;
        ProgresoOperacion.Visibility = Visibility.Visible;
        TextoEstadoOperacion.Text = estado;
        try
        {
            await operacion();
            TextoEstadoOperacion.Text = "Operación completada";
        }
        catch (Exception ex)
        {
            TextoEstadoOperacion.Text = "La operación no se completó";
            MostrarError(ex.Message);
        }
        finally
        {
            ProgresoOperacion.Visibility = Visibility.Collapsed;
            _ocupado = false;
        }
    }

    private static void ExigirRespuesta<T>(RespuestaTipada<T> respuesta)
    {
        if (!respuesta.Exito || respuesta.Datos is null)
        {
            throw new InvalidOperationException(respuesta.Mensaje);
        }
    }

    private void AplicarEstadoServicio(EstadoServicioVista estado)
    {
        TextoEstadoServicio.Text = estado.Estado;
        IndicadorServicio.Fill = estado.EnEjecucion
            ? (Brush)FindResource("Verde")
            : estado.Instalado
                ? (Brush)FindResource("Ambar")
                : (Brush)FindResource("Rojo");
        BotonInstalarServicio.IsEnabled = !estado.Instalado;
        BotonIniciarServicio.IsEnabled = estado.Instalado && !estado.EnEjecucion;
        BotonDetenerServicio.IsEnabled = estado.EnEjecucion;
        BotonReiniciarServicio.IsEnabled = estado.EnEjecucion;
        if (!estado.EnEjecucion)
        {
            TextoEstadoConexion.Text = estado.Instalado
                ? "El canal central no está activo."
                : "Instala el servicio para crear la base y aceptar clientes.";
            TextoEstadoConexion.Foreground = (Brush)FindResource("TextoSecundario");
        }
    }

    private void LimpiarMetricas()
    {
        AplicarEstadoBaseLocal();
        MetricaUsuarios.Text = "--";
        MetricaAuditoria.Text = "--";
        MetricaPuerto.Text = _configuracion.Puerto.ToString();
        TextoUltimaAuditoria.Text = "Sin datos disponibles.";
    }

    private void LimpiarEditorUsuario()
    {
        _usuarioSeleccionadoId = null;
        TablaUsuarios.SelectedItem = null;
        CampoCuentaUsuario.Clear();
        CampoRolUsuario.SelectedIndex = 1;
        CampoMaximoUsuario.Text = "5";
        CampoCarpetasUsuario.Clear();
        CampoUsuarioActivo.IsChecked = true;
    }

    private void AplicarEstadoBaseLocal()
    {
        // Distingue una base creada de un fallo exclusivo del canal administrativo.
        var creada = File.Exists(_rutas.RutaBaseDatos);
        MetricaBase.Text = creada ? "Creada" : "Pendiente";
        MetricaBase.Foreground = (Brush)FindResource(creada ? "Verde" : "Ambar");
        MetricaBase.ToolTip = _rutas.RutaBaseDatos;
    }

    private static string? ObtenerContenidoCombo(ComboBox combo)
    {
        return combo.SelectedItem switch
        {
            ComboBoxItem item => Convert.ToString(item.Content),
            string texto => texto,
            _ => null
        };
    }

    private static DateTimeOffset? ConvertirInicioDiaUtc(DateTime? fecha)
    {
        if (!fecha.HasValue)
        {
            return null;
        }

        var local = DateTime.SpecifyKind(fecha.Value.Date, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUniversalTime();
    }

    private static DateTimeOffset? ConvertirFinDiaUtc(DateTime? fecha)
    {
        if (!fecha.HasValue)
        {
            return null;
        }

        var local = DateTime.SpecifyKind(fecha.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local);
        return new DateTimeOffset(local).ToUniversalTime();
    }

    private static void MostrarError(string mensaje)
    {
        MessageBox.Show(
            mensaje,
            "LanzadorScripts Servidor",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int atributo,
        ref int valor,
        int tamano);
}
