// (Autor: Alex Roman)
// Descripcion: Ventana principal del lanzador de scripts PowerShell.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LanzadorScripts.Modelos;
using LanzadorScripts.ModelosVista;
using LanzadorScripts.Servicios;

namespace LanzadorScripts;

public partial class VentanaPrincipal : Window
{
    private readonly ServicioConfiguracion _servicioConfiguracion = new();
    private readonly ServicioPermisos _servicioPermisos = new();
    private readonly ServicioDescubrimientoScripts _servicioDescubrimientoScripts = new();
    private readonly ObservableCollection<InformacionScript> _scriptsDisponibles = [];
    private readonly ObservableCollection<ModeloEjecucionScript> _ejecuciones = [];
    private ConfiguracionLanzador _configuracion = new();
    private GestorEjecucionScripts _gestorEjecucionScripts;

    public VentanaPrincipal()
    {
        InitializeComponent();

        _configuracion = _servicioConfiguracion.Cargar();
        _gestorEjecucionScripts = new GestorEjecucionScripts(
            Dispatcher,
            _configuracion.RutaLogs,
            _configuracion.MaximoEjecucionesParalelas);

        _gestorEjecucionScripts.RecuentosCambiados += (_, _) => ActualizarRecuentoEjecuciones();
        ListaScripts.ItemsSource = _scriptsDisponibles;
        PestanasEjecucion.ItemsSource = _ejecuciones;

        CargarConfiguracionEnInterfaz();
        RefrescarScripts();
        ActualizarRecuentoEjecuciones();
    }

    private void CargarConfiguracionEnInterfaz()
    {
        TextoMaximoParalelo.Text = _configuracion.MaximoEjecucionesParalelas.ToString();

        var textoAdministrador = _servicioPermisos.EsAdministrador ? "Administrador" : "Sin elevacion";
        TextoIdentidad.Text = $"{_servicioPermisos.UsuarioActual} - {textoAdministrador}";
    }

    private void GuardarConfiguracion_Click(object sender, RoutedEventArgs e)
    {
        GuardarConfiguracionDesdeInterfaz();
        RefrescarScripts();
    }

    private void RefrescarScripts_Click(object sender, RoutedEventArgs e)
    {
        GuardarConfiguracionDesdeInterfaz();
        RefrescarScripts();
    }

    private void EjecutarScriptSeleccionado_Click(object sender, RoutedEventArgs e)
    {
        if (ListaScripts.SelectedItem is not InformacionScript script)
        {
            System.Windows.MessageBox.Show("Selecciona un script.", "Lanzador", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!script.EstaAutorizado)
        {
            System.Windows.MessageBox.Show(
                "El usuario actual no esta autorizado para ejecutar scripts en subcarpetas.",
                "Permisos",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var ejecucion = new ModeloEjecucionScript(script);
        _ejecuciones.Add(ejecucion);
        PestanasEjecucion.SelectedItem = ejecucion;
        _gestorEjecucionScripts.Encolar(ejecucion);
        ActualizarRecuentoEjecuciones();
    }

    private async void EnviarEntrada_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModeloEjecucionScript ejecucion)
        {
            return;
        }

        var entrada = ejecucion.TextoEntrada;
        ejecucion.TextoEntrada = string.Empty;
        await _gestorEjecucionScripts.EnviarEntradaAsync(ejecucion, entrada);
    }

    private async void Entrada_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || (sender as FrameworkElement)?.DataContext is not ModeloEjecucionScript ejecucion)
        {
            return;
        }

        e.Handled = true;
        var entrada = ejecucion.TextoEntrada;
        ejecucion.TextoEntrada = string.Empty;
        await _gestorEjecucionScripts.EnviarEntradaAsync(ejecucion, entrada);
    }

    private void CancelarEjecucion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModeloEjecucionScript ejecucion)
        {
            _gestorEjecucionScripts.Cancelar(ejecucion);
        }
    }

    private void AbrirLog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModeloEjecucionScript ejecucion)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ejecucion.RutaLog) || !File.Exists(ejecucion.RutaLog))
        {
            System.Windows.MessageBox.Show("El log aun no existe.", "Log", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = ejecucion.RutaLog,
            UseShellExecute = true
        });
    }

    private void CerrarEjecucion_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModeloEjecucionScript ejecucion)
        {
            return;
        }

        if (ejecucion.PuedeCancelar)
        {
            var resultado = System.Windows.MessageBox.Show(
                "La ejecucion sigue activa o pendiente. Si cierras la pestaña se cancelara.",
                "Cerrar ejecucion",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (resultado != MessageBoxResult.OK)
            {
                return;
            }

            _gestorEjecucionScripts.Cancelar(ejecucion);
        }

        _ejecuciones.Remove(ejecucion);
        ActualizarRecuentoEjecuciones();
    }

    private void TextoSalida_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox cuadroTexto)
        {
            cuadroTexto.ScrollToEnd();
        }
    }

    private void GuardarConfiguracionDesdeInterfaz()
    {
        _configuracion.MaximoEjecucionesParalelas = int.TryParse(TextoMaximoParalelo.Text, out var maximo) ? maximo : 5;
        _configuracion.Normalizar();

        _servicioConfiguracion.Guardar(_configuracion);
        _gestorEjecucionScripts.ActualizarConfiguracion(
            _configuracion.RutaLogs,
            _configuracion.MaximoEjecucionesParalelas);
        CargarConfiguracionEnInterfaz();
    }

    private void RefrescarScripts()
    {
        _scriptsDisponibles.Clear();
        _servicioPermisos.Cargar(_configuracion.RutaScripts);

        var scripts = _servicioDescubrimientoScripts.Descubrir(_configuracion.RutaScripts, _servicioPermisos);
        foreach (var script in scripts)
        {
            _scriptsDisponibles.Add(script);
        }

        TextoRecuentoScripts.Text = $"{_scriptsDisponibles.Count} scripts encontrados";
        TextoOrigen.Text = "Origen configurado por administracion";

        if (!Directory.Exists(_configuracion.RutaScripts))
        {
            TextoRecuentoScripts.Text = "Origen no disponible";
        }
    }

    private void ActualizarRecuentoEjecuciones()
    {
        TextoRecuentoEjecuciones.Text =
            $"Ejecutando: {_gestorEjecucionScripts.RecuentoEjecucionesActivas}  Pendientes: {_gestorEjecucionScripts.RecuentoEjecucionesPendientes}  Max: {_configuracion.MaximoEjecucionesParalelas}";
    }
}
