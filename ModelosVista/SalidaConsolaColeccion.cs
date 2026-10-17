// (Autor: Alex Roman)
// Descripcion: Notifica una sola vez los recortes de salida de la consola.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using LanzadorScripts.Servicios;

namespace LanzadorScripts.ModelosVista;

public sealed class SalidaConsolaColeccion : ObservableCollection<EventoCliente>
{
    internal void RetirarIniciales(int cantidad)
    {
        if (Items is List<EventoCliente> lista) lista.RemoveRange(0, cantidad);
        else for (var i = 0; i < cantidad; i++) Items.RemoveAt(0);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
