// (Autor: Alex Roman)
// Descripcion: Base para notificar cambios a la interfaz.

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LanzadorScripts.ModelosVista;

public abstract class ObjetoNotificable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool AsignarPropiedad<T>(ref T campo, T valor, [CallerMemberName] string? nombrePropiedad = null)
    {
        if (EqualityComparer<T>.Default.Equals(campo, valor))
        {
            return false;
        }

        campo = valor;
        NotificarCambioPropiedad(nombrePropiedad);
        return true;
    }

    protected void NotificarCambioPropiedad([CallerMemberName] string? nombrePropiedad = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nombrePropiedad));
    }
}
