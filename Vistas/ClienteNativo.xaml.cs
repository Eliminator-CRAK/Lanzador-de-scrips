// (Autor: Alex Roman)
// Descripcion: Enlaza los comandos visuales con el modelo del cliente nativo.

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using LanzadorScripts.ModelosVista;
using LanzadorScripts.Servicios;

namespace LanzadorScripts.Vistas;

public partial class ClienteNativo : UserControl, IDisposable
{
    public ClienteNativoModelo? Modelo { get; private set; }
    public event EventHandler? SolicitarAuditoria;
    public ClienteNativo() => InitializeComponent();

    public async Task InicializarAsync(IClienteNativo cliente)
    {
        Modelo ??= new ClienteNativoModelo(cliente);
        DataContext = Modelo;
        var configuracion = new ServicioConfiguracion().Cargar();
        ServidorCentral.Text = $"{configuracion.ServidorCentral}:{configuracion.PuertoServidorCentral}";
        await Modelo.InicializarAsync();
    }

    private async Task OperarAsync(Func<Task> operacion)
    {
        try { await operacion(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ServicioRedaccionSecretos.Sanitizar(ex.Message), "Lanzador de Scripts", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Refrescar_Click(object sender, RoutedEventArgs e) { if (Modelo is not null) await OperarAsync(Modelo.InicializarAsync); }
    private async void Subir_Click(object sender, RoutedEventArgs e) { if (Modelo is not null) await OperarAsync(Modelo.SubirCarpetaAsync); }
    private async void Ejecutar_Click(object sender, RoutedEventArgs e)
    {
        if (Modelo is not null && sender is Button { Tag: ElementoScriptNativo script }) await OperarAsync(() => Modelo.EjecutarAsync(script));
    }
    private async void ListadoScripts_DobleClick(object sender, MouseButtonEventArgs e)
    {
        if (Modelo is not null && ListadoScripts.SelectedItem is ElementoScriptNativo script) await OperarAsync(() => Modelo.EjecutarAsync(script));
    }
    private async void Consola_Enviar(object sender, RoutedEventArgs e)
    {
        if (Modelo is not null && sender is ConsolaNativa { DataContext: ConsolaNativaModelo consola }) await OperarAsync(() => Modelo.EnviarAsync(consola));
    }
    private async void Consola_Cerrar(object sender, RoutedEventArgs e)
    {
        if (Modelo is null || sender is not ConsolaNativa { DataContext: ConsolaNativaModelo consola }) return;
        if (consola.Activa && MessageBox.Show(Window.GetWindow(this), $"Detener {consola.NombreScript}?", "Detener ejecucion", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await OperarAsync(() => Modelo.CerrarConsolaAsync(consola));
    }
    private async void Limpiar_Click(object sender, RoutedEventArgs e)
    {
        if (Modelo is null) return;
        if (Modelo.Activas > 0 && MessageBox.Show(Window.GetWindow(this), "Detener todas las ejecuciones activas?", "Limpiar consolas", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await OperarAsync(Modelo.LimpiarAsync);
    }
    private async void Ajustes_Click(object sender, RoutedEventArgs e) { if (Modelo is not null) await OperarAsync(Modelo.AbrirAjustesAsync); }
    private void Volver_Click(object sender, RoutedEventArgs e) => Modelo?.CerrarAjustes();
    private async void Guardar_Click(object sender, RoutedEventArgs e)
    {
        if (Modelo is null || !TablaUsuarios.CommitEdit(DataGridEditingUnit.Cell, true) || !TablaUsuarios.CommitEdit(DataGridEditingUnit.Row, true)) return;
        BotonGuardar.IsEnabled = false;
        try { await OperarAsync(Modelo.GuardarAjustesAsync); }
        finally { BotonGuardar.IsEnabled = true; }
    }
    private void AgregarUsuario_Click(object sender, RoutedEventArgs e) => Modelo?.Usuarios.Add(new UsuarioPermisoModelo(new UsuarioPermisoNativo(Guid.NewGuid().ToString("N"), "", "nominal", 5, [])));
    private void EliminarUsuario_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: UsuarioPermisoModelo usuario }) Modelo?.Usuarios.Remove(usuario); }
    private void CarpetasUsuario_Click(object sender, RoutedEventArgs e)
    {
        if (Modelo is null || sender is not Button { Tag: UsuarioPermisoModelo usuario }) return;
        var seleccion = Seleccionar("Carpetas permitidas", Modelo.Carpetas, usuario.CarpetasPermitidas);
        if (seleccion is not null) usuario.CarpetasPermitidas = seleccion;
    }
    private void Elevados_Click(object sender, RoutedEventArgs e)
    {
        if (Modelo is null) return;
        var seleccion = Seleccionar("Scripts elevados permitidos", Modelo.Catalogo.Select(s => s.ScriptId).ToArray(), Modelo.ScriptsElevados);
        if (seleccion is not null) Modelo.ScriptsElevados = seleccion;
    }
    private void ScriptsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (Modelo is null) return;
        var seleccion = Seleccionar("Scripts de administradores", Modelo.Catalogo.Select(s => s.ScriptId).ToArray(), Modelo.ScriptsAdmin);
        if (seleccion is not null) Modelo.ScriptsAdmin = seleccion;
    }
    private IReadOnlyList<string>? Seleccionar(string titulo, IReadOnlyList<string> disponibles, IReadOnlyList<string> seleccionados)
    {
        var dialogo = new DialogoSeleccionNativa(titulo, disponibles, seleccionados) { Owner = Window.GetWindow(this) };
        return dialogo.ShowDialog() == true ? dialogo.Seleccionados : null;
    }
    private async void RefrescarCatalogo_Click(object sender, RoutedEventArgs e) { if (Modelo is not null) await OperarAsync(Modelo.RefrescarCatalogoAsync); }
    private async void PublicarCatalogo_Click(object sender, RoutedEventArgs e)
    {
        if (Modelo is null) return;
        TablaCatalogo.CommitEdit(DataGridEditingUnit.Cell, true);
        TablaCatalogo.CommitEdit(DataGridEditingUnit.Row, true);
        await OperarAsync(Modelo.PublicarCatalogoAsync);
    }
    private async void ModoDesarrollo_Click(object sender, RoutedEventArgs e)
    {
        if (Modelo is not null) await OperarAsync(() => Modelo.CambiarDesarrolloAsync(ModoDesarrollo.IsChecked == true));
    }
    private void Auditoria_Click(object sender, RoutedEventArgs e) => SolicitarAuditoria?.Invoke(this, EventArgs.Empty);
    private async void ExecutionPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (Modelo is null || !Modelo.Administrador) return;
        if (MessageBox.Show(Window.GetWindow(this), "Aplicar ExecutionPolicy Unrestricted en este equipo?", "Politica de PowerShell", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await OperarAsync(async () =>
        {
            var resultado = await Modelo.AplicarExecutionPolicyAsync();
            MessageBox.Show(Window.GetWindow(this), resultado.Mensaje, "Politica de PowerShell", MessageBoxButton.OK, resultado.Exito ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }
    private async void Diagnostico_Click(object sender, RoutedEventArgs e)
    {
        if (Modelo is null || ListadoScripts.SelectedItem is not ElementoScriptNativo { EsCarpeta: false } script) return;
        await OperarAsync(async () =>
        {
            var diagnostico = await Modelo.DiagnosticarAsync(script.Id);
            MessageBox.Show(Window.GetWindow(this), $"{script.Nombre}\nEstado: {diagnostico.CatalogoEstado}\nExecutionPolicy: {diagnostico.ExecutionPolicy}\nSHA-256: {diagnostico.Sha256}\n{diagnostico.MotivoBloqueo}", "Diagnostico de ejecucion", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }
    public void Dispose() => Modelo?.Dispose();
}

public sealed class InvertirBooleano : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class AlturaConsolasNativas : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var viewport = values.Length > 0 && values[0] is double alto && double.IsFinite(alto) ? Math.Max(0, alto) : 0;
        var cantidad = values.Length > 1 && values[1] is int total ? Math.Max(1, total) : 1;
        // Reserva 280 pixeles de consola y 12 de separacion sin crecer con el texto.
        return Math.Max(viewport, cantidad * 292.0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
