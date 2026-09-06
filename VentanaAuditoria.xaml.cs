// (Autor: Alex Roman)
// Descripcion: Consulta y presenta la auditoria del servidor central.

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LanzadorScripts.Protocolo;
using LanzadorScripts.Servicios;
using MessageBox = System.Windows.MessageBox;

namespace LanzadorScripts;

public partial class VentanaAuditoria : Window
{
    private readonly ServicioConfiguracion _servicioConfiguracion = new();
    private readonly ServicioDatosCentralizados _datos;
    private readonly ObservableCollection<FilaAuditoria> _filas = [];
    private bool _cargando;

    public VentanaAuditoria()
    {
        InitializeComponent();
        _datos = new ServicioDatosCentralizados(_servicioConfiguracion.Cargar);
        TablaAuditoria.ItemsSource = _filas;
        FiltroResultado.SelectedIndex = 0;
        var configuracion = _servicioConfiguracion.Cargar();
        TextoServidor.Text = $"{configuracion.ServidorCentral}:{configuracion.PuertoServidorCentral}";
        Loaded += VentanaAuditoria_Loaded;
    }

    private async void VentanaAuditoria_Loaded(object sender, RoutedEventArgs e)
    {
        await CargarAsync();
    }

    private async void Buscar_Click(object sender, RoutedEventArgs e)
    {
        await CargarAsync();
    }

    private async void Actualizar_Click(object sender, RoutedEventArgs e)
    {
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        if (_cargando)
        {
            return;
        }

        _cargando = true;
        Progreso.Visibility = Visibility.Visible;
        TextoEstado.Text = "Consultando el servidor central...";
        try
        {
            var usuario = FiltroUsuario.SelectedItem as string;
            if (string.Equals(usuario, "Todos", StringComparison.OrdinalIgnoreCase))
            {
                usuario = null;
            }

            var resultado = FiltroResultado.SelectedItem is ComboBoxItem opcion
                ? Convert.ToString(opcion.Content)
                : null;
            if (string.Equals(resultado, "Todos", StringComparison.OrdinalIgnoreCase))
            {
                resultado = null;
            }

            var respuesta = await Task.Run(() => _datos.ConsultarAuditoria(
                new FiltroAuditoriaServidorCentral(
                    usuario,
                    InicioDiaUtc(FiltroDesde.SelectedDate),
                    FinDiaUtc(FiltroHasta.SelectedDate),
                    resultado,
                    string.IsNullOrWhiteSpace(FiltroScript.Text)
                        ? null
                        : FiltroScript.Text.Trim(),
                    1000,
                    0)));
            if (!respuesta.Exito || respuesta.Datos is null)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(respuesta.Mensaje)
                    ? "No se pudo consultar la auditoria."
                    : respuesta.Mensaje);
            }

            ActualizarUsuarios(respuesta.Datos.Usuarios, usuario);
            _filas.Clear();
            foreach (var evento in respuesta.Datos.Eventos)
            {
                _filas.Add(new FilaAuditoria(
                    evento.FechaLocal,
                    evento.UsuarioWindows,
                    evento.Equipo,
                    evento.Accion,
                    evento.ScriptNombre ?? evento.ScriptId ?? string.Empty,
                    evento.Resultado,
                    evento.CodigoSalida,
                    string.IsNullOrWhiteSpace(evento.Detalle) ? evento.Motivo : evento.Detalle));
            }

            TextoEstado.Text = $"{_filas.Count:N0} evento(s) mostrados de {respuesta.Datos.Total:N0}.";
        }
        catch (Exception ex)
        {
            TextoEstado.Text = ServicioRedaccionSecretos.Sanitizar(ex.Message);
            MessageBox.Show(
                this,
                TextoEstado.Text,
                "Auditoría de scripts",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            Progreso.Visibility = Visibility.Collapsed;
            _cargando = false;
        }
    }

    private void ActualizarUsuarios(IReadOnlyList<string> usuarios, string? seleccionado)
    {
        FiltroUsuario.Items.Clear();
        FiltroUsuario.Items.Add("Todos");
        foreach (var usuario in usuarios)
        {
            FiltroUsuario.Items.Add(usuario);
        }

        FiltroUsuario.SelectedItem = FiltroUsuario.Items
            .Cast<object>()
            .FirstOrDefault(item => string.Equals(
                Convert.ToString(item),
                seleccionado,
                StringComparison.OrdinalIgnoreCase))
            ?? "Todos";
    }

    private static DateTimeOffset? InicioDiaUtc(DateTime? fecha)
    {
        return fecha.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(fecha.Value.Date, DateTimeKind.Local)).ToUniversalTime()
            : null;
    }

    private static DateTimeOffset? FinDiaUtc(DateTime? fecha)
    {
        return fecha.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(
                fecha.Value.Date.AddDays(1).AddTicks(-1),
                DateTimeKind.Local)).ToUniversalTime()
            : null;
    }

    private sealed record FilaAuditoria(
        DateTimeOffset Fecha,
        string Usuario,
        string Equipo,
        string Accion,
        string Script,
        string Resultado,
        int? CodigoSalida,
        string Detalle);
}
