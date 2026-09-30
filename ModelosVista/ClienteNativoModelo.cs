// (Autor: Alex Roman)
// Descripcion: Mantiene navegacion, ajustes y consolas independientemente de la vista WPF.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using LanzadorScripts.Servicios;

namespace LanzadorScripts.ModelosVista;

public sealed class ClienteNativoModelo : ModeloNotificable, IDisposable
{
    private readonly IClienteNativo _cliente;
    private readonly CancellationTokenSource _vida = new();
    private CancellationTokenSource? _busqueda;
    private string _buscar = "";
    private string _carpeta = "";
    private string _estado = "";
    private string _usuario = "";
    private string _rutaScripts = "";
    private bool _administrador;
    private bool _cargando;
    private bool _ajustes;
    private bool _bypass;
    private bool _desarrollo;
    private int _limite = 1;
    private int _numeroConsola;
    private bool _iniciando;
    private bool _desechado;

    public ClienteNativoModelo(IClienteNativo cliente)
    {
        _cliente = cliente;
        Consolas.CollectionChanged += (_, _) => ActualizarResumen();
        Consolas.Add(CrearConsola());
    }

    public ObservableCollection<ElementoScriptNativo> Scripts { get; } = [];
    public ObservableCollection<ConsolaNativaModelo> Consolas { get; } = [];
    public ObservableCollection<UsuarioPermisoModelo> Usuarios { get; } = [];
    public ObservableCollection<EntradaCatalogoModelo> Catalogo { get; } = [];
    public IReadOnlyList<string> Carpetas { get; private set; } = [];
    public IReadOnlyList<string> ScriptsAdmin { get; set; } = [];
    public IReadOnlyList<string> ScriptsElevados { get; set; } = [];
    public string Version => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.2");
    public string Buscar { get => _buscar; set { if (Asignar(ref _buscar, value)) _ = RefrescarAsync(); } }
    public string Carpeta => string.IsNullOrEmpty(_carpeta) ? "Scripts" : "Scripts / " + _carpeta;
    public string Estado { get => _estado; private set => Asignar(ref _estado, value); }
    public string Usuario { get => _usuario; private set => Asignar(ref _usuario, value); }
    public string RutaScripts { get => _rutaScripts; private set => Asignar(ref _rutaScripts, value); }
    public bool Administrador { get => _administrador; private set => Asignar(ref _administrador, value); }
    public bool Cargando { get => _cargando; private set => Asignar(ref _cargando, value); }
    public bool AjustesAbiertos { get => _ajustes; private set => Asignar(ref _ajustes, value); }
    public bool PermitirBypass { get => _bypass; set => Asignar(ref _bypass, value); }
    public bool ModoDesarrollo { get => _desarrollo; private set => Asignar(ref _desarrollo, value); }
    public int Activas => Consolas.Count(c => c.Activa);
    public int Limite => _limite;
    public bool PuedeDetenerTodas => Activas > 0;
    public bool PuedeLimpiarFinalizadas => Consolas.Any(c => c.Script is not null && !c.Activa);
    public string Resumen => $"Ejecutando: {Activas}   Max: {_limite}";

    public async Task InicializarAsync()
    {
        var sesion = await _cliente.ObtenerSesionAsync(_vida.Token);
        Usuario = sesion.Usuario.NombreUsuario;
        Administrador = sesion.Usuario.EstaAutorizado && sesion.Usuario.Rol == "admin";
        _limite = sesion.Usuario.MaxScriptsSimultaneos;
        Notificar(nameof(Limite));
        RutaScripts = sesion.RutaScripts;
        ModoDesarrollo = sesion.ModoDesarrolloFirmas;
        Estado = sesion.Usuario.EstaAutorizado ? sesion.AvisoConexion : sesion.Usuario.MotivoBloqueo;
        ActualizarResumen();
        await RefrescarAsync();
    }

    public async Task RefrescarAsync()
    {
        // Solo la busqueda mas reciente puede reemplazar el listado visible.
        _busqueda?.Cancel();
        var peticion = CancellationTokenSource.CreateLinkedTokenSource(_vida.Token);
        _busqueda = peticion;
        Cargando = true;
        try
        {
            if (_buscar.Length > 0) await Task.Delay(200, peticion.Token);
            var scripts = await _cliente.ListarScriptsAsync(_carpeta, _buscar, peticion.Token);
            if (!ReferenceEquals(_busqueda, peticion) || _desechado) return;
            Scripts.Clear();
            foreach (var script in scripts) Scripts.Add(script);
            Estado = scripts.Count == 0 ? "Sin scripts disponibles." : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_busqueda, peticion))
            {
                Scripts.Clear();
                Estado = ServicioRedaccionSecretos.Sanitizar(ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_busqueda, peticion)) { Cargando = false; _busqueda = null; }
            peticion.Dispose();
        }
    }

    public async Task AbrirCarpetaAsync(string carpeta)
    {
        _carpeta = carpeta;
        _buscar = "";
        Notificar(nameof(Buscar));
        Notificar(nameof(Carpeta));
        await RefrescarAsync();
    }

    public Task SubirCarpetaAsync()
    {
        var separador = _carpeta.LastIndexOf('/');
        return AbrirCarpetaAsync(separador < 0 ? "" : _carpeta[..separador]);
    }

    public async Task EjecutarAsync(ElementoScriptNativo script)
    {
        if (script.EsCarpeta) { await AbrirCarpetaAsync(script.Carpeta); return; }
        if (script.EstaBloqueado) { Estado = script.MotivoBloqueo; return; }
        if (_iniciando || _desechado) return;
        _iniciando = true;
        try
        {
            var inicio = await _cliente.IniciarAsync(script.Id, _vida.Token);
            if (!inicio.Exito) { Estado = inicio.Mensaje; return; }
            var consola = Consolas.FirstOrDefault(c => c.Script is null) ?? CrearConsola();
            if (!Consolas.Contains(consola)) Consolas.Add(consola);
            consola.Iniciar(script, inicio.Datos);
            ActualizarResumen();
            _ = ObservarAsync(consola);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Estado = ServicioRedaccionSecretos.Sanitizar(ex.Message); }
        finally { _iniciando = false; }
    }

    private ConsolaNativaModelo CrearConsola()
    {
        var consola = new ConsolaNativaModelo(++_numeroConsola);
        consola.PropertyChanged += Consola_Cambiada;
        return consola;
    }

    private void Consola_Cambiada(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConsolaNativaModelo.Activa)) ActualizarResumen();
    }

    private async Task ObservarAsync(ConsolaNativaModelo consola)
    {
        try
        {
            await foreach (var evento in _cliente.ObservarAsync(consola.EjecucionId, _vida.Token))
            {
                if (_desechado) break;
                consola.Agregar(evento);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { consola.Agregar(new EventoCliente("error", ServicioRedaccionSecretos.Sanitizar(ex.Message), "#F44747", true)); }
        finally
        {
            consola.Finalizar();
            if (!_desechado && consola.CerrarAlFinalizar) RetirarConsola(consola);
            ActualizarResumen();
        }
    }

    public async Task EnviarAsync(ConsolaNativaModelo consola)
    {
        if (!consola.Activa || consola.Enviando) return;
        var texto = consola.Entrada;
        if (texto.Length > 8192 || texto.Contains('\r') || texto.Contains('\n'))
        {
            Estado = "La respuesta debe ser una linea de hasta 8192 caracteres.";
            return;
        }
        consola.Enviando = true;
        try
        {
            // Una linea vacia permite responder a Pause sin exponer el contenido escrito.
            await _cliente.EnviarEntradaAsync(consola.EjecucionId, texto);
            consola.Entrada = "";
        }
        catch (Exception ex) { Estado = ServicioRedaccionSecretos.Sanitizar(ex.Message); }
        finally { consola.Enviando = false; }
    }

    public async Task CerrarConsolaAsync(ConsolaNativaModelo consola)
    {
        consola.CerrarAlFinalizar = true;
        if (consola.Activa) await _cliente.CancelarAsync(consola.EjecucionId);
        // La consola permanece hasta recibir el fin real del proceso cancelado.
        if (consola.Activa) return;
        RetirarConsola(consola);
        ActualizarResumen();
    }

    private void RetirarConsola(ConsolaNativaModelo consola)
    {
        consola.PropertyChanged -= Consola_Cambiada;
        Consolas.Remove(consola);
        if (Consolas.Count == 0) Consolas.Add(CrearConsola());
    }

    public async Task LimpiarAsync()
    {
        foreach (var consola in Consolas.Where(c => c.Activa).ToArray())
        {
            consola.CerrarAlFinalizar = true;
            await _cliente.CancelarAsync(consola.EjecucionId);
        }
        foreach (var consola in Consolas.Where(c => !c.Activa).ToArray()) RetirarConsola(consola);
        if (Consolas.Count == 0) Consolas.Add(CrearConsola());
    }

    public async Task DetenerTodasAsync()
    {
        // Detiene las ejecuciones conservando su salida y su resultado final.
        foreach (var consola in Consolas.Where(c => c.Activa).ToArray())
            await _cliente.CancelarAsync(consola.EjecucionId);
    }

    public void LimpiarFinalizadas()
    {
        // Retira solo consolas terminadas y mantiene las ejecuciones activas.
        foreach (var consola in Consolas.Where(c => !c.Activa && c.Script is not null).ToArray())
            RetirarConsola(consola);
        if (Consolas.Count == 0) Consolas.Add(CrearConsola());
    }

    private void ActualizarResumen()
    {
        Notificar(nameof(Activas));
        Notificar(nameof(Resumen));
        Notificar(nameof(PuedeDetenerTodas));
        Notificar(nameof(PuedeLimpiarFinalizadas));
    }

    public async Task AbrirAjustesAsync()
    {
        if (!Administrador) return;
        Cargando = true;
        try
        {
            var permisos = await _cliente.ObtenerPermisosAsync(_vida.Token);
            Usuarios.Clear();
            foreach (var usuario in permisos.Usuarios) Usuarios.Add(new UsuarioPermisoModelo(usuario));
            ScriptsAdmin = permisos.ScriptsAdmin;
            ScriptsElevados = permisos.ScriptsElevadosPermitidos;
            PermitirBypass = permisos.PermitirExecutionPolicyBypass;
            Carpetas = await _cliente.ObtenerCarpetasAsync(_vida.Token);
            await RefrescarCatalogoAsync();
            AjustesAbiertos = true;
            Estado = "";
        }
        finally { Cargando = false; }
    }

    public async Task GuardarAjustesAsync()
    {
        await _cliente.GuardarPermisosAsync(new PermisosClienteNativo(Usuarios.Select(u => u.CrearContrato()).ToArray(), ScriptsAdmin, ScriptsElevados, PermitirBypass), _vida.Token);
        AjustesAbiertos = false;
        await InicializarAsync();
    }

    public void CerrarAjustes() => AjustesAbiertos = false;

    public async Task RefrescarCatalogoAsync()
    {
        var estado = await _cliente.ObtenerCatalogoAsync(_vida.Token);
        Catalogo.Clear();
        foreach (var script in estado.Scripts) Catalogo.Add(new EntradaCatalogoModelo(script));
        Estado = estado.Valido ? "" : estado.Mensaje;
    }

    public async Task PublicarCatalogoAsync()
    {
        await _cliente.PublicarCatalogoAsync(Catalogo.Where(s => s.Incluido).Select(s => s.ScriptId).ToArray(), _vida.Token);
        await RefrescarCatalogoAsync();
    }

    public async Task CambiarDesarrolloAsync(bool activo)
    {
        await _cliente.CambiarModoDesarrolloAsync(activo, _vida.Token);
        ModoDesarrollo = activo;
        await RefrescarAsync();
    }

    public Task<DiagnosticoEjecucionScript> DiagnosticarAsync(string scriptId) => _cliente.DiagnosticarAsync(scriptId, _vida.Token);
    public Task<ResultadoExecutionPolicy> AplicarExecutionPolicyAsync() => _cliente.AplicarExecutionPolicyAsync(_vida.Token);

    public void Dispose()
    {
        if (_desechado) return;
        _desechado = true;
        foreach (var consola in Consolas) { consola.Entrada = ""; consola.PropertyChanged -= Consola_Cambiada; }
        _vida.Cancel();
        _busqueda?.Cancel();
        _vida.Dispose();
    }
}

public sealed class ConsolaNativaModelo : ModeloNotificable
{
    private string _entrada = "";
    private bool _activa;
    private bool _enviando;
    public ConsolaNativaModelo(int numero) => Numero = numero;
    public int Numero { get; }
    public Guid EjecucionId { get; private set; }
    internal bool CerrarAlFinalizar { get; set; }
    public ElementoScriptNativo? Script { get; private set; }
    public string Titulo => $"Consola #{Numero}";
    public string NombreScript => Script?.Nombre ?? "";
    public bool Activa { get => _activa; private set => Asignar(ref _activa, value); }
    public bool Enviando { get => _enviando; set => Asignar(ref _enviando, value); }
    public string Entrada { get => _entrada; set => Asignar(ref _entrada, value); }
    public ObservableCollection<EventoCliente> Eventos { get; } = [];
    public void Iniciar(ElementoScriptNativo script, Guid id)
    {
        Script = script;
        EjecucionId = id;
        Activa = true;
        Notificar(nameof(NombreScript));
    }
    public void Agregar(EventoCliente evento)
    {
        if (!string.IsNullOrEmpty(evento.Mensaje)) Eventos.Add(evento);
        if (evento.Finalizado) Finalizar();
    }
    public void Finalizar() { Entrada = ""; Activa = false; }
}

public sealed class UsuarioPermisoModelo : ModeloNotificable
{
    private string _nombre;
    private string _rol;
    private int _limite;
    private IReadOnlyList<string> _carpetas;
    public UsuarioPermisoModelo(UsuarioPermisoNativo usuario)
    {
        Id = usuario.Id; _nombre = usuario.NombreUsuario; _rol = usuario.Rol; _limite = usuario.MaxScriptsSimultaneos; _carpetas = usuario.CarpetasPermitidas;
    }
    public string Id { get; }
    public string NombreUsuario { get => _nombre; set => Asignar(ref _nombre, value); }
    public string Rol { get => _rol; set => Asignar(ref _rol, value); }
    public int MaxScriptsSimultaneos { get => _limite; set => Asignar(ref _limite, value); }
    public IReadOnlyList<string> CarpetasPermitidas { get => _carpetas; set { if (Asignar(ref _carpetas, value)) Notificar(nameof(ResumenCarpetas)); } }
    public string ResumenCarpetas => _carpetas.Count == 0 ? "Raiz" : string.Join(", ", _carpetas);
    public UsuarioPermisoNativo CrearContrato() => new(Id, NombreUsuario, Rol, MaxScriptsSimultaneos, CarpetasPermitidas);
}

public sealed class EntradaCatalogoModelo : ModeloNotificable
{
    private bool _incluido;
    private readonly EstadoCatalogoScriptCliente _script;
    public EntradaCatalogoModelo(EstadoCatalogoScriptCliente script) { _script = script; _incluido = script.Incluido; }
    public string ScriptId => _script.ScriptId;
    public string Estado => _script.Estado;
    public string Sha256 => _script.Sha256;
    public long Longitud => _script.Longitud;
    public bool Incluido { get => _incluido; set => Asignar(ref _incluido, value); }
}
