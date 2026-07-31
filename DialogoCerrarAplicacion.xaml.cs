// (Autor: Alex Roman)
// Descripcion: Gestiona la confirmacion del cierre definitivo.

using System.Windows;
using LanzadorScripts.Servicios;

namespace LanzadorScripts;

public partial class DialogoCerrarAplicacion : Window
{
    public DialogoCerrarAplicacion(IReadOnlyList<EjecucionActivaResumen> ejecuciones)
    {
        ArgumentNullException.ThrowIfNull(ejecuciones);
        if (ejecuciones.Count == 0)
        {
            throw new ArgumentException("El dialogo requiere al menos una ejecucion activa.", nameof(ejecuciones));
        }

        InitializeComponent();
        Ejecuciones = ejecuciones;
        DataContext = this;
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
