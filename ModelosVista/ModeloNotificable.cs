// (Autor: Alex Roman)
// Descripcion: Notifica cambios de estado a los controles WPF.

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LanzadorScripts.ModelosVista;

public abstract class ModeloNotificable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Asignar<T>(ref T campo, T valor, [CallerMemberName] string? propiedad = null)
    {
        if (EqualityComparer<T>.Default.Equals(campo, valor)) return false;
        campo = valor;
        Notificar(propiedad);
        return true;
    }

    protected void Notificar([CallerMemberName] string? propiedad = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propiedad));
}
