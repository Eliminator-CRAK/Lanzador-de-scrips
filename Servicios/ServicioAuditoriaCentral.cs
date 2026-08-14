// (Autor: Alex Roman)
// Descripcion: Registra la auditoria en la base cifrada del servidor central.

using System.Collections.Concurrent;
using System.IO;
using System.Security.Principal;
using LanzadorScripts.Modelos;
using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servicios;

public sealed class ServicioAuditoriaCentral : IServicioAuditoria
{
    private const int MaximoPendientes = 1000;
    private readonly Func<ConfiguracionLanzador> _obtenerConfiguracion;
    private readonly ConcurrentQueue<EventoAuditoriaServidorCentral> _pendientes = new();
    private readonly SemaphoreSlim _bloqueoReintento = new(1, 1);
    private volatile string _ultimoError = string.Empty;
    private int _totalPendientes;
    private int _desechado;

    public ServicioAuditoriaCentral(Func<ConfiguracionLanzador>? obtenerConfiguracion = null)
    {
        _obtenerConfiguracion = obtenerConfiguracion
            ?? (() => new ServicioConfiguracion().Cargar());
    }

    public string UltimoError => _ultimoError;

    public string RutaAuditoria
    {
        get
        {
            var configuracion = _obtenerConfiguracion();
            return $"{configuracion.ServidorCentral}:{configuracion.PuertoServidorCentral}/base-central";
        }
    }

    public int TotalPendientes => Volatile.Read(ref _totalPendientes);

    public bool Disponible => string.IsNullOrWhiteSpace(_ultimoError) && TotalPendientes == 0;

    public Task<ResultadoRegistroAuditoria> RegistrarInicioEjecucionAsync(
        Guid ejecucionId,
        ScriptInterno script,
        UsuarioCliente usuario,
        string sha256)
    {
        return RegistrarAsync(
            CrearEvento(
                "ejecucion.inicio",
                "permitido",
                usuario.NombreUsuario,
                script.Id,
                script.Nombre,
                sha256,
                ejecucionId,
                null,
                string.Empty,
                string.Empty),
            conservarPendiente: false);
    }

    public Task<ResultadoRegistroAuditoria> RegistrarFinEjecucionAsync(
        Guid ejecucionId,
        ScriptInterno script,
        UsuarioCliente usuario,
        string sha256,
        string resultado,
        int? codigoSalida,
        string? detalle)
    {
        return RegistrarAsync(
            CrearEvento(
                "ejecucion.fin",
                resultado,
                usuario.NombreUsuario,
                script.Id,
                script.Nombre,
                sha256,
                ejecucionId,
                codigoSalida,
                string.Empty,
                detalle ?? string.Empty),
            conservarPendiente: true);
    }

    public Task<ResultadoRegistroAuditoria> RegistrarDenegacionAsync(
        string accion,
        string usuario,
        string? scriptId,
        string motivo)
    {
        return RegistrarAsync(
            CrearEvento(
                accion,
                "denegado",
                usuario,
                scriptId,
                null,
                null,
                null,
                null,
                motivo,
                string.Empty),
            conservarPendiente: true);
    }

    public Task<ResultadoRegistroAuditoria> RegistrarErrorInternoAsync(string accion, string detalle)
    {
        return RegistrarAsync(
            CrearEvento(
                accion,
                "error",
                WindowsIdentity.GetCurrent().Name,
                null,
                null,
                null,
                null,
                null,
                "Error interno",
                detalle),
            conservarPendiente: true);
    }

    public Task<ResultadoRegistroAuditoria> RegistrarEventoSeguridadAsync(
        string accion,
        string usuario,
        string? scriptId,
        string resultado,
        string detalle)
    {
        return RegistrarAsync(
            CrearEvento(
                accion,
                resultado,
                usuario,
                scriptId,
                null,
                null,
                null,
                null,
                string.Empty,
                detalle),
            conservarPendiente: true);
    }

    public async Task<bool> VaciarPendientesAsync(TimeSpan tiempoMaximo)
    {
        if (!await _bloqueoReintento.WaitAsync(tiempoMaximo).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            return await VaciarPendientesSinBloqueoAsync(tiempoMaximo).ConfigureAwait(false);
        }
        finally
        {
            _bloqueoReintento.Release();
        }
    }

    public ResultadoDisponibilidadAuditoria ComprobarDisponibilidad()
    {
        try
        {
            var configuracion = _obtenerConfiguracion();
            var cliente = CrearCliente(configuracion, TimeSpan.FromSeconds(3));
            var respuesta = cliente.Enviar<object, EstadoServidorCentral>(
                OperacionesServidor.Salud,
                new { });
            if (!respuesta.Exito || respuesta.Datos is null || !respuesta.Datos.BaseInicializada)
            {
                _ultimoError = respuesta.Mensaje;
                return ResultadoDisponibilidadAuditoria.Error(
                    ObtenerRutaAuditoriaSegura(configuracion),
                    respuesta.Mensaje);
            }

            if (TotalPendientes == 0)
            {
                _ultimoError = string.Empty;
            }

            return ResultadoDisponibilidadAuditoria.Correcto(
                ObtenerRutaAuditoriaSegura(configuracion),
                Disponible);
        }
        catch (Exception ex)
        {
            _ultimoError = $"No se pudo comprobar el servidor central ({ex.GetType().Name}).";
            return ResultadoDisponibilidadAuditoria.Error(
                "servidor-central/base-central",
                _ultimoError);
        }
    }

    public void Cerrar(TimeSpan tiempoMaximo)
    {
        if (Interlocked.Exchange(ref _desechado, 1) != 0)
        {
            return;
        }

        var espera = tiempoMaximo < TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Min(30, tiempoMaximo.TotalSeconds));
        try
        {
            VaciarPendientesAsync(espera).Wait(espera);
        }
        catch
        {
            // El cierre no espera indefinidamente al servidor.
        }
    }

    public void Dispose()
    {
        Cerrar(TimeSpan.FromSeconds(5));
    }

    private async Task<ResultadoRegistroAuditoria> RegistrarAsync(
        EventoAuditoriaServidorCentral evento,
        bool conservarPendiente)
    {
        if (Volatile.Read(ref _desechado) != 0)
        {
            return ResultadoRegistroAuditoria.Error("El servicio de auditoria ya esta cerrado.");
        }

        if (!await _bloqueoReintento.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
        {
            if (conservarPendiente && Volatile.Read(ref _desechado) == 0)
            {
                Encolar(evento);
            }

            return ResultadoRegistroAuditoria.Error(
                string.IsNullOrWhiteSpace(_ultimoError)
                    ? "La auditoria esta ocupada y no pudo confirmar el evento."
                    : _ultimoError);
        }

        try
        {
            if (Volatile.Read(ref _desechado) != 0)
            {
                return ResultadoRegistroAuditoria.Error("El servicio de auditoria ya esta cerrado.");
            }

            if (!_pendientes.IsEmpty
                && !await VaciarPendientesSinBloqueoAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
            {
                if (conservarPendiente)
                {
                    Encolar(evento);
                }

                return ResultadoRegistroAuditoria.Error(_ultimoError);
            }

            var resultado = await EnviarAsync(evento, CancellationToken.None).ConfigureAwait(false);
            if (resultado.Exito)
            {
                _ultimoError = string.Empty;
                return ResultadoRegistroAuditoria.Correcto(evento.EventoId);
            }

            _ultimoError = resultado.Mensaje;
            if (conservarPendiente)
            {
                Encolar(evento);
            }

            return ResultadoRegistroAuditoria.Error(resultado.Mensaje);
        }
        finally
        {
            _bloqueoReintento.Release();
        }
    }

    private async Task<bool> VaciarPendientesSinBloqueoAsync(TimeSpan tiempoMaximo)
    {
        var limite = DateTimeOffset.UtcNow + tiempoMaximo;
        while (_pendientes.TryPeek(out var evento) && DateTimeOffset.UtcNow <= limite)
        {
            var resultado = await EnviarAsync(evento, CancellationToken.None).ConfigureAwait(false);
            if (!resultado.Exito)
            {
                _ultimoError = resultado.Mensaje;
                return false;
            }

            _pendientes.TryDequeue(out _);
            Interlocked.Decrement(ref _totalPendientes);
        }

        if (_pendientes.IsEmpty)
        {
            _ultimoError = string.Empty;
        }

        return _pendientes.IsEmpty;
    }

    private async Task<RespuestaTipada<bool>> EnviarAsync(
        EventoAuditoriaServidorCentral evento,
        CancellationToken cancelacion)
    {
        try
        {
            var configuracion = _obtenerConfiguracion();
            return await CrearCliente(configuracion, TimeSpan.FromSeconds(5))
                .EnviarAsync<EventoAuditoriaServidorCentral, bool>(
                    OperacionesServidor.RegistrarAuditoria,
                    evento,
                    cancelacion)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or IOException)
        {
            return RespuestaTipada<bool>.Error(
                "configuracion_invalida",
                $"No se pudo preparar la conexion de auditoria ({ex.GetType().Name}).");
        }
    }

    private void Encolar(EventoAuditoriaServidorCentral evento)
    {
        var total = Interlocked.Increment(ref _totalPendientes);
        if (total > MaximoPendientes)
        {
            Interlocked.Decrement(ref _totalPendientes);
            _ultimoError = "La cola de auditoria en memoria ha alcanzado su limite.";
            return;
        }

        _pendientes.Enqueue(evento);
    }

    private static EventoAuditoriaServidorCentral CrearEvento(
        string accion,
        string resultado,
        string usuario,
        string? scriptId,
        string? scriptNombre,
        string? sha256,
        Guid? ejecucionId,
        int? codigoSalida,
        string motivo,
        string detalle)
    {
        var ahora = DateTimeOffset.Now;
        return new EventoAuditoriaServidorCentral(
            Guid.NewGuid().ToString("N"),
            Limitar(accion, 200),
            Limitar(resultado, 100),
            usuario,
            ObtenerSidActual(),
            Environment.MachineName,
            LimitarOpcional(scriptId, 1024),
            LimitarOpcional(scriptNombre, 512),
            LimitarOpcional(sha256, 64),
            ejecucionId?.ToString("N"),
            codigoSalida,
            Limitar(motivo, 2000),
            Limitar(detalle, 8000),
            ahora.ToUniversalTime(),
            ahora);
    }

    private static ClienteServidorCentral CrearCliente(
        ConfiguracionLanzador configuracion,
        TimeSpan tiempo)
    {
        return new ClienteServidorCentral(
            configuracion.ServidorCentral,
            configuracion.PuertoServidorCentral,
            tiempo);
    }

    private static string ObtenerRutaAuditoriaSegura(ConfiguracionLanzador configuracion)
    {
        return $"{configuracion.ServidorCentral}:{configuracion.PuertoServidorCentral}/base-central";
    }

    private static string ObtenerSidActual()
    {
        return WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
    }

    private static string Limitar(string? valor, int maximo)
    {
        var texto = valor?.Trim() ?? string.Empty;
        return texto.Length <= maximo ? texto : texto[..maximo];
    }

    private static string? LimitarOpcional(string? valor, int maximo)
    {
        return string.IsNullOrWhiteSpace(valor) ? null : Limitar(valor, maximo);
    }
}
