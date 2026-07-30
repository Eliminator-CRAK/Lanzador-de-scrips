// (Autor: Alex Roman)
// Descripcion: Gestiona la confirmacion del cierre definitivo.

using System.Windows;
using LanzadorScripts.Servicios;

namespace LanzadorScripts;

public partial class DialogoCerrarAplicacion : Window
{
    public DialogoCerrarAplicacion(IReadOnlyList<EjecucionActivaResumen> ejecuciones)
    {
        InitializeComponent();
        Ejecuciones = ejecuciones;
        DataContext = this;
        TextoSinEjecuciones.Visibility = ejecuciones.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ListaEjecuciones.Visibility = ejecuciones.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public IReadOnlyList<EjecucionActivaResumen> Ejecuciones { get; }

    private void Confirmar_Click(object sender, RoutedEventArgs e)
    {
        // Confirma la cancelacion de recursos y scripts.
        DialogResult = true;
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e)
    {
        // Mantiene la aplicacion activa.
        DialogResult = false;
    }
}
