// (Autor: Alex Roman)
// Descripcion: Modelo visual de una ejecucion de script.

using System.Text;
using LanzadorScripts.Modelos;

namespace LanzadorScripts.ModelosVista;

public sealed class ModeloEjecucionScript : ObjetoNotificable
{
    private readonly StringBuilder _constructorSalida = new();
    private EstadoEjecucion _estado = EstadoEjecucion.Pendiente;
    private string _textoSalida = string.Empty;
    private string _textoEntrada = string.Empty;
    private string _rutaLog = string.Empty;
    private int? _codigoSalida;
    private DateTime? _inicio;
    private DateTime? _fin;

    public ModeloEjecucionScript(InformacionScript script)
    {
        Id = Guid.NewGuid();
        Script = script;
    }

    public Guid Id { get; }

    public InformacionScript Script { get; }

    public EstadoEjecucion Estado
    {
        get => _estado;
        set
        {
            if (AsignarPropiedad(ref _estado, value))
            {
                NotificarCambioPropiedad(nameof(TextoEstado));
                NotificarCambioPropiedad(nameof(EstaEjecutando));
                NotificarCambioPropiedad(nameof(PuedeCancelar));
                NotificarCambioPropiedad(nameof(TextoCabecera));
            }
        }
    }

    public string TextoEstado => Estado switch
    {
        EstadoEjecucion.Pendiente => "pendiente",
        EstadoEjecucion.Ejecutando => "ejecutando",
        EstadoEjecucion.Finalizada => "finalizada",
        EstadoEjecucion.Error => "error",
        EstadoEjecucion.Cancelada => "cancelada",
        _ => "desconocido"
    };

    public string TextoSalida
    {
        get => _textoSalida;
        private set => AsignarPropiedad(ref _textoSalida, value);
    }

    public string TextoEntrada
    {
        get => _textoEntrada;
        set => AsignarPropiedad(ref _textoEntrada, value);
    }

    public string RutaLog
    {
        get => _rutaLog;
        set => AsignarPropiedad(ref _rutaLog, value);
    }

    public int? CodigoSalida
    {
        get => _codigoSalida;
        set => AsignarPropiedad(ref _codigoSalida, value);
    }

    public DateTime? Inicio
    {
        get => _inicio;
        set => AsignarPropiedad(ref _inicio, value);
    }

    public DateTime? Fin
    {
        get => _fin;
        set => AsignarPropiedad(ref _fin, value);
    }

    public bool EstaEjecutando => Estado == EstadoEjecucion.Ejecutando;

    public bool PuedeCancelar => Estado is EstadoEjecucion.Pendiente or EstadoEjecucion.Ejecutando;

    public string TextoCabecera => $"{Script.Nombre} - {TextoEstado}";

    public void AgregarSalida(string texto)
    {
        _constructorSalida.Append(texto);
        TextoSalida = _constructorSalida.ToString();
    }
}
