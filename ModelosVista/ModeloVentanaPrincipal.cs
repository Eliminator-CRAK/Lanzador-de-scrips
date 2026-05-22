// (Autor: Alex Roman)
// Descripcion: Estado visual centralizado de la ventana principal.

using System.Collections.ObjectModel;
using System.Windows;
using LanzadorScripts.Modelos;

namespace LanzadorScripts.ModelosVista;

public sealed class ModeloVentanaPrincipal : ObjetoNotificable
{
    private string _busquedaScripts = string.Empty;
    private bool _mostrarAjustes;
    private string _identidadUsuario = string.Empty;
    private string _modoUsuario = string.Empty;
    private string _textoRecuentoScripts = string.Empty;
    private string _textoRecuentoEjecuciones = string.Empty;
    private int _ejecucionesActivas;
    private int _ejecucionesPendientes;
    private int _maximoEjecucionesParalelas = 5;
    private bool _origenDisponible = true;

    public ObservableCollection<InformacionScript> ScriptsDisponibles { get; } = [];

    public ObservableCollection<InformacionScript> ScriptsFiltrados { get; } = [];

    public ObservableCollection<ModeloEjecucionScript> Ejecuciones { get; } = [];

    public string BusquedaScripts
    {
        get => _busquedaScripts;
        set
        {
            if (AsignarPropiedad(ref _busquedaScripts, value))
            {
                AplicarFiltroScripts();
            }
        }
    }

    public bool MostrarAjustes
    {
        get => _mostrarAjustes;
        set
        {
            if (AsignarPropiedad(ref _mostrarAjustes, value))
            {
                NotificarCambioPropiedad(nameof(VisibilidadScripts));
                NotificarCambioPropiedad(nameof(VisibilidadAjustes));
            }
        }
    }

    public Visibility VisibilidadScripts => MostrarAjustes ? Visibility.Collapsed : Visibility.Visible;

    public Visibility VisibilidadAjustes => MostrarAjustes ? Visibility.Visible : Visibility.Collapsed;

    public string IdentidadUsuario
    {
        get => _identidadUsuario;
        set => AsignarPropiedad(ref _identidadUsuario, value);
    }

    public string ModoUsuario
    {
        get => _modoUsuario;
        set => AsignarPropiedad(ref _modoUsuario, value);
    }

    public string TextoRecuentoScripts
    {
        get => _textoRecuentoScripts;
        set => AsignarPropiedad(ref _textoRecuentoScripts, value);
    }

    public string TextoRecuentoEjecuciones
    {
        get => _textoRecuentoEjecuciones;
        set => AsignarPropiedad(ref _textoRecuentoEjecuciones, value);
    }

    public int EjecucionesActivas
    {
        get => _ejecucionesActivas;
        set => AsignarPropiedad(ref _ejecucionesActivas, value);
    }

    public int EjecucionesPendientes
    {
        get => _ejecucionesPendientes;
        set => AsignarPropiedad(ref _ejecucionesPendientes, value);
    }

    public int MaximoEjecucionesParalelas
    {
        get => _maximoEjecucionesParalelas;
        set => AsignarPropiedad(ref _maximoEjecucionesParalelas, Math.Clamp(value, 1, 50));
    }

    public bool OrigenDisponible
    {
        get => _origenDisponible;
        set
        {
            if (AsignarPropiedad(ref _origenDisponible, value))
            {
                NotificarCambioPropiedad(nameof(TextoEstadoOrigen));
            }
        }
    }

    public string TextoEstadoOrigen => OrigenDisponible
        ? "Origen configurado por administracion"
        : "Origen no disponible";

    public void ReemplazarScripts(IEnumerable<InformacionScript> scripts)
    {
        ScriptsDisponibles.Clear();
        foreach (var script in scripts)
        {
            ScriptsDisponibles.Add(script);
        }

        AplicarFiltroScripts();
    }

    public void AplicarFiltroScripts()
    {
        ScriptsFiltrados.Clear();
        var filtro = BusquedaScripts.Trim();

        foreach (var script in ScriptsDisponibles)
        {
            if (string.IsNullOrWhiteSpace(filtro)
                || script.Nombre.Contains(filtro, StringComparison.OrdinalIgnoreCase)
                || script.TextoPermiso.Contains(filtro, StringComparison.OrdinalIgnoreCase))
            {
                ScriptsFiltrados.Add(script);
            }
        }

        TextoRecuentoScripts = OrigenDisponible
            ? $"{ScriptsFiltrados.Count} de {ScriptsDisponibles.Count} scripts"
            : "Origen no disponible";
    }
}
