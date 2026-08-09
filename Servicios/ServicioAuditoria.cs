// (Autor: Alex Roman)
// Descripcion: Registra eventos inmutables en la carpeta remota de auditoria.

using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace LanzadorScripts.Servicios;

public sealed class ServicioAuditoria : IDisposable
{
    public const string NombreCarpetaAuditoria = "Auditoria";

    private const int VersionEsquema = 1;
    private const int LongitudMaximaEvento = 64 * 1024;
    private const int MaximoEventosPendientes = 1000;
    private static readonly TimeSpan IntervaloReintento = TimeSpan.FromSeconds(10);
    private static readonly SecurityIdentifier Administradores = new(
        WellKnownSidType.BuiltinAdministratorsSid,
        null);
    private static readonly SecurityIdentifier Sistema = new(
        WellKnownSidType.LocalSystemSid,
        null);
    private static readonly SecurityIdentifier DerechosPropietario = new("S-1-3-4");
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<string> _obtenerRutaPermisos;
    private readonly Func<IdentidadAuditoria> _obtenerIdentidad;
    private readonly Func<DateTimeOffset> _obtenerHoraLocal;
    private readonly bool _protegerEventos;
    private readonly SemaphoreSlim _bloqueo = new(1, 1);
    private readonly Queue<EventoAuditoria> _pendientes = new();
    private readonly System.Threading.Timer _temporizador;
    private volatile string _ultimoError = string.Empty;
    private volatile string _bloqueoCritico = string.Empty;
    private volatile string _rutaAuditoria = string.Empty;
    private int _totalPendientes;
    private int _reintentoActivo;
    private int _desechado;

    public ServicioAuditoria()
        : this(
            () => new ServicioConfiguracion().Cargar().RutaPermisos,
            ObtenerIdentidadActual,
            () => DateTimeOffset.Now)
    {
    }

    internal ServicioAuditoria(
        Func<string> obtenerRutaPermisos,
        Func<IdentidadAuditoria>? obtenerIdentidad = null,
        Func<DateTimeOffset>? obtenerHoraLocal = null,
        bool protegerEventos = true)
    {
        _obtenerRutaPermisos = obtenerRutaPermisos
            ?? throw new ArgumentNullException(nameof(obtenerRutaPermisos));
        _obtenerIdentidad = obtenerIdentidad ?? ObtenerIdentidadActual;
        _obtenerHoraLocal = obtenerHoraLocal ?? (() => DateTimeOffset.Now);
        _protegerEventos = protegerEventos;
        _temporizador = new System.Threading.Timer(
            ReintentarPendientes,
            null,
            IntervaloReintento,
            IntervaloReintento);
    }

    public string UltimoError => _ultimoError;

    public string RutaAuditoria => _rutaAuditoria;

    public int TotalPendientes => Volatile.Read(ref _totalPendientes);

    public bool Disponible => string.IsNullOrWhiteSpace(_ultimoError)
        && string.IsNullOrWhiteSpace(_bloqueoCritico)
        && TotalPendientes == 0;

    public Task<ResultadoRegistroAuditoria> RegistrarInicioEjecucionAsync(
        Guid ejecucionId,
        ScriptInterno script,
        UsuarioCliente usuario,
        string sha256)
    {
        return CrearYRegistrarAsync(
            () => CrearEvento(
                "ejecucion.inicio",
                "permitido",
                usuario.NombreUsuario,
                script.Id,
                script.Nombre,
                sha256,
                ejecucionId,
                null,
                null,
                null),
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
        return CrearYRegistrarAsync(
            () => CrearEvento(
                "ejecucion.fin",
                resultado,
                usuario.NombreUsuario,
                script.Id,
                script.Nombre,
                sha256,
                ejecucionId,
                codigoSalida,
                null,
                detalle),
            conservarPendiente: true);
    }

    public Task<ResultadoRegistroAuditoria> RegistrarDenegacionAsync(
        string accion,
        string usuario,
        string? scriptId,
        string motivo)
    {
        return CrearYRegistrarAsync(
            () => CrearEvento(
                accion,
                "denegado",
                usuario,
                scriptId,
                null,
                null,
                null,
                null,
                motivo,
                null),
            conservarPendiente: true);
    }

    public Task<ResultadoRegistroAuditoria> RegistrarErrorInternoAsync(string accion, string detalle)
    {
        return CrearYRegistrarAsync(
            () => CrearEvento(
                accion,
                "error",
                ObtenerNombreUsuarioSeguro(),
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
        return CrearYRegistrarAsync(
            () => CrearEvento(
                accion,
                resultado,
                usuario,
                scriptId,
                null,
                null,
                null,
                null,
                null,
                detalle),
            conservarPendiente: true);
    }

    public async Task<bool> VaciarPendientesAsync(TimeSpan tiempoMaximo)
    {
        var limite = DateTime.UtcNow + tiempoMaximo;
        do
        {
            await _bloqueo.WaitAsync().ConfigureAwait(false);
            bool vacio;
            try
            {
                vacio = await VaciarPendientesSinBloqueoAsync().ConfigureAwait(false);
            }
            finally
            {
                _bloqueo.Release();
            }

            if (vacio)
            {
                return true;
            }

            if (DateTime.UtcNow >= limite)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        } while (DateTime.UtcNow <= limite);

        return TotalPendientes == 0;
    }

    public void Dispose()
    {
        Cerrar(TimeSpan.FromSeconds(30));
    }

    internal void Cerrar(TimeSpan tiempoMaximo)
    {
        if (Interlocked.Exchange(ref _desechado, 1) != 0)
        {
            return;
        }

        _temporizador.Dispose();
        var espera = tiempoMaximo < TimeSpan.Zero ? TimeSpan.Zero : tiempoMaximo;
        if (TotalPendientes == 0 && Volatile.Read(ref _reintentoActivo) == 0)
        {
            _bloqueo.Dispose();
            return;
        }

        // Aisla una operacion SMB bloqueada para respetar el limite global de cierre.
        var vaciado = Task.Run(() => VaciarPendientesAsync(espera));
        var completado = false;
        try
        {
            completado = vaciado.Wait(espera);
        }
        catch
        {
            completado = vaciado.IsCompleted;
        }

        if (completado)
        {
            _bloqueo.Dispose();
        }
    }

    internal static string ResolverRutaAuditoria(string rutaPermisos)
    {
        var carpetaPermisos = RutasArtefactosProtegidos.Resolver(rutaPermisos).Carpeta;
        var ruta = ServicioRutasSeguras.ResolverCarpetaAbsoluta(
            Path.Combine(carpetaPermisos, NombreCarpetaAuditoria),
            "auditoria remota");
        if (!ServicioRutasSeguras.EstaDentroDeCarpeta(carpetaPermisos, ruta)
            || string.Equals(carpetaPermisos, ruta, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("La auditoria remota queda fuera de la carpeta de permisos.");
        }

        return ruta;
    }

    internal static string CrearNombreCarpetaUsuario(IdentidadAuditoria identidad)
    {
        var nombre = PerfilAplicacion.Normalizar(
            identidad.NombreUsuario.Replace('\\', '_'));
        return $"{nombre}__{PerfilAplicacion.CrearIdentificadorSid(identidad.Sid)}";
    }

    public ResultadoDisponibilidadAuditoria ComprobarDisponibilidad()
    {
        try
        {
            var ruta = ResolverRutaAuditoria(_obtenerRutaPermisos());
            _rutaAuditoria = ruta;
            if (!Directory.Exists(ruta))
            {
                return ResultadoDisponibilidadAuditoria.Error(
                    ServicioRedaccionSecretos.Sanitizar(ruta),
                    "La carpeta remota de auditoria no esta preparada.");
            }

            ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(ruta);
            return ResultadoDisponibilidadAuditoria.Correcto(
                ServicioRedaccionSecretos.Sanitizar(ruta),
                Disponible);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return ResultadoDisponibilidadAuditoria.Error(
                string.Empty,
                $"No se pudo comprobar la auditoria remota ({ex.GetType().Name}).");
        }
    }

    private Task<ResultadoRegistroAuditoria> CrearYRegistrarAsync(
        Func<EventoAuditoria> crearEvento,
        bool conservarPendiente)
    {
        try
        {
            return RegistrarAsync(crearEvento(), conservarPendiente);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return Task.FromResult(ResultadoRegistroAuditoria.Error(
                $"No se pudo preparar la auditoria remota ({ex.GetType().Name})."));
        }
    }

    private async Task<ResultadoRegistroAuditoria> RegistrarAsync(
        EventoAuditoria evento,
        bool conservarPendiente)
    {
        if (Volatile.Read(ref _desechado) != 0)
        {
            return ResultadoRegistroAuditoria.Error("El servicio de auditoria ya esta cerrado.");
        }

        if (!string.IsNullOrWhiteSpace(_bloqueoCritico))
        {
            return ResultadoRegistroAuditoria.Error(_bloqueoCritico);
        }

        await _bloqueo.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await VaciarPendientesSinBloqueoAsync().ConfigureAwait(false))
            {
                if (conservarPendiente)
                {
                    EncolarSinBloqueo(evento);
                }

                return ResultadoRegistroAuditoria.Error(_ultimoError);
            }

            var resultado = await EscribirEventoAsync(evento).ConfigureAwait(false);
            if (resultado.Exito)
            {
                _ultimoError = string.Empty;
                return resultado;
            }

            _ultimoError = resultado.Mensaje;
            if (conservarPendiente)
            {
                EncolarSinBloqueo(evento);
            }

            return resultado;
        }
        finally
        {
            _bloqueo.Release();
        }
    }

    private async Task<bool> VaciarPendientesSinBloqueoAsync()
    {
        while (_pendientes.Count > 0)
        {
            var evento = _pendientes.Peek();
            var resultado = await EscribirEventoAsync(evento).ConfigureAwait(false);
            if (!resultado.Exito)
            {
                _ultimoError = resultado.Mensaje;
                return false;
            }

            _pendientes.Dequeue();
            Volatile.Write(ref _totalPendientes, _pendientes.Count);
        }

        _ultimoError = string.Empty;
        return true;
    }

    private async Task<ResultadoRegistroAuditoria> EscribirEventoAsync(EventoAuditoria evento)
    {
        try
        {
            var raizAuditoria = ResolverRutaAuditoria(_obtenerRutaPermisos());
            if (!Directory.Exists(raizAuditoria))
            {
                return ResultadoRegistroAuditoria.Error(
                    "La carpeta remota de auditoria no esta preparada por un administrador.");
            }

            ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(raizAuditoria);
            var identidad = new IdentidadAuditoria(
                evento.UsuarioWindows,
                evento.UsuarioSid,
                evento.Equipo);
            var carpetaUsuario = ServicioRutasSeguras.ResolverCarpetaAbsoluta(
                Path.Combine(raizAuditoria, CrearNombreCarpetaUsuario(identidad)),
                "auditoria del usuario");
            if (!ServicioRutasSeguras.EstaDentroDeCarpeta(raizAuditoria, carpetaUsuario))
            {
                return ResultadoRegistroAuditoria.Error("La carpeta de auditoria del usuario no es segura.");
            }

            if (_protegerEventos)
            {
                PrepararCarpetaUsuario(carpetaUsuario, identidad);
            }
            else
            {
                Directory.CreateDirectory(carpetaUsuario);
                ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(carpetaUsuario);
            }

            _rutaAuditoria = raizAuditoria;
            var nombreEquipo = PerfilAplicacion.Normalizar(evento.Equipo);
            var nombreArchivo = $"{evento.FechaUtc:yyyyMMddTHHmmssfffffffZ}_{nombreEquipo}_{evento.EventoId:N}.json";
            var rutaArchivo = ServicioRutasSeguras.ResolverArchivoEnCarpeta(
                carpetaUsuario,
                nombreArchivo,
                "evento de auditoria",
                ".json");
            var datos = JsonSerializer.SerializeToUtf8Bytes(evento, OpcionesJson);
            if (datos.Length == 0 || datos.Length > LongitudMaximaEvento)
            {
                return ResultadoRegistroAuditoria.Error("El evento de auditoria tiene un tamano no valido.");
            }

            try
            {
                await using var archivo = new FileStream(
                    rutaArchivo,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await archivo.WriteAsync(datos).ConfigureAwait(false);
                await archivo.FlushAsync().ConfigureAwait(false);
                archivo.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(rutaArchivo))
            {
                if (!EventoExistenteCoincide(rutaArchivo, datos))
                {
                    return ResultadoRegistroAuditoria.Error(
                        "Ya existe un evento de auditoria distinto con el mismo identificador.");
                }
            }

            if (_protegerEventos)
            {
                InmovilizarArchivo(rutaArchivo, identidad);
            }

            return ResultadoRegistroAuditoria.Correcto(rutaArchivo);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or System.Security.SecurityException
            or JsonException)
        {
            return ResultadoRegistroAuditoria.Error(
                $"No se pudo escribir la auditoria remota ({ex.GetType().Name}).");
        }
    }

    private static bool EventoExistenteCoincide(string rutaArchivo, byte[] esperado)
    {
        try
        {
            var informacion = new FileInfo(rutaArchivo);
            if (informacion.Length != esperado.Length
                || informacion.Length <= 0
                || informacion.Length > LongitudMaximaEvento)
            {
                return false;
            }

            var existente = File.ReadAllBytes(rutaArchivo);
            return CryptographicOperations.FixedTimeEquals(existente, esperado);
        }
        catch
        {
            return false;
        }
    }

    private static void PrepararCarpetaUsuario(
        string carpetaUsuario,
        IdentidadAuditoria identidad)
    {
        // Limita la carpeta al usuario identificado y a los administradores.
        var usuario = new SecurityIdentifier(identidad.Sid);
        var directorio = new DirectoryInfo(carpetaUsuario);
        directorio.Create();
        ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(directorio.FullName);

        var propietario = directorio
            .GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (propietario is null
            || (!propietario.Equals(usuario)
                && !propietario.Equals(Administradores)
                && !propietario.Equals(Sistema)))
        {
            throw new UnauthorizedAccessException(
                "La carpeta de auditoria pertenece a otra identidad.");
        }

        var herencia = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var seguridad = new DirectorySecurity();
        seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguridad.AddAccessRule(CrearReglaDirectorio(
            Administradores,
            FileSystemRights.FullControl,
            herencia));
        seguridad.AddAccessRule(CrearReglaDirectorio(
            Sistema,
            FileSystemRights.FullControl,
            herencia));
        seguridad.AddAccessRule(CrearReglaDirectorio(
            usuario,
            FileSystemRights.ReadAndExecute | FileSystemRights.Write,
            herencia));
        seguridad.AddAccessRule(CrearReglaDirectorio(
            DerechosPropietario,
            FileSystemRights.ReadAndExecute | FileSystemRights.Write,
            herencia));
        directorio.SetAccessControl(seguridad);
    }

    private static void InmovilizarArchivo(
        string rutaArchivo,
        IdentidadAuditoria identidad)
    {
        // Retira escritura y borrado una vez confirmado el contenido.
        var usuario = new SecurityIdentifier(identidad.Sid);
        var archivo = new FileInfo(rutaArchivo);
        var seguridad = new FileSecurity();
        seguridad.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        seguridad.AddAccessRule(CrearReglaArchivo(
            Administradores,
            FileSystemRights.FullControl));
        seguridad.AddAccessRule(CrearReglaArchivo(
            Sistema,
            FileSystemRights.FullControl));
        seguridad.AddAccessRule(CrearReglaArchivo(
            usuario,
            FileSystemRights.ReadAndExecute));
        seguridad.AddAccessRule(CrearReglaArchivo(
            DerechosPropietario,
            FileSystemRights.ReadAndExecute));
        archivo.SetAccessControl(seguridad);
        archivo.Attributes |= FileAttributes.ReadOnly;
    }

    private static FileSystemAccessRule CrearReglaDirectorio(
        IdentityReference identidad,
        FileSystemRights permisos,
        InheritanceFlags herencia)
    {
        return new FileSystemAccessRule(
            identidad,
            permisos,
            herencia,
            PropagationFlags.None,
            AccessControlType.Allow);
    }

    private static FileSystemAccessRule CrearReglaArchivo(
        IdentityReference identidad,
        FileSystemRights permisos)
    {
        return new FileSystemAccessRule(
            identidad,
            permisos,
            AccessControlType.Allow);
    }

    private void EncolarSinBloqueo(EventoAuditoria evento)
    {
        if (_pendientes.Any(pendiente => pendiente.EventoId == evento.EventoId))
        {
            return;
        }

        if (_pendientes.Count >= MaximoEventosPendientes)
        {
            _bloqueoCritico = "La cola de auditoria remota ha alcanzado su limite y la ejecucion queda bloqueada hasta reiniciar la aplicacion.";
            _ultimoError = _bloqueoCritico;
            return;
        }

        _pendientes.Enqueue(evento);
        Volatile.Write(ref _totalPendientes, _pendientes.Count);
    }

    private void ReintentarPendientes(object? estado)
    {
        if (TotalPendientes == 0
            || Volatile.Read(ref _desechado) != 0
            || Interlocked.Exchange(ref _reintentoActivo, 1) != 0)
        {
            return;
        }

        _ = ReintentarPendientesSeguroAsync();
    }

    private async Task ReintentarPendientesSeguroAsync()
    {
        try
        {
            await VaciarPendientesAsync(TimeSpan.Zero).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            Volatile.Write(ref _reintentoActivo, 0);
        }
    }

    private EventoAuditoria CrearEvento(
        string accion,
        string resultado,
        string usuarioWindows,
        string? scriptId,
        string? scriptNombre,
        string? scriptSha256,
        Guid? ejecucionId,
        int? codigoSalida,
        string? motivo,
        string? detalle)
    {
        var identidad = _obtenerIdentidad();
        var fechaLocal = _obtenerHoraLocal();
        var usuarioEfectivo = string.IsNullOrWhiteSpace(identidad.NombreUsuario)
            ? usuarioWindows
            : identidad.NombreUsuario;
        return Sanitizar(new EventoAuditoria(
            VersionEsquema,
            Guid.NewGuid(),
            accion,
            resultado,
            usuarioEfectivo,
            identidad.Sid,
            scriptId,
            scriptNombre,
            scriptSha256,
            ejecucionId,
            codigoSalida,
            motivo,
            detalle,
            fechaLocal.ToUniversalTime(),
            fechaLocal,
            identidad.Equipo));
    }

    private static EventoAuditoria Sanitizar(EventoAuditoria evento)
    {
        return evento with
        {
            UsuarioWindows = ServicioRedaccionSecretos.Sanitizar(evento.UsuarioWindows),
            UsuarioSid = ServicioRedaccionSecretos.Sanitizar(evento.UsuarioSid),
            Equipo = ServicioRedaccionSecretos.Sanitizar(evento.Equipo),
            ScriptId = ServicioRedaccionSecretos.Sanitizar(evento.ScriptId),
            ScriptNombre = ServicioRedaccionSecretos.Sanitizar(evento.ScriptNombre),
            ScriptSha256 = ServicioRedaccionSecretos.Sanitizar(evento.ScriptSha256),
            Motivo = ServicioRedaccionSecretos.Sanitizar(evento.Motivo),
            Detalle = ServicioRedaccionSecretos.Sanitizar(evento.Detalle)
        };
    }

    private string ObtenerNombreUsuarioSeguro()
    {
        try
        {
            return _obtenerIdentidad().NombreUsuario;
        }
        catch
        {
            return Environment.UserDomainName + "\\" + Environment.UserName;
        }
    }

    private static IdentidadAuditoria ObtenerIdentidadActual()
    {
        using var identidad = WindowsIdentity.GetCurrent();
        return new IdentidadAuditoria(
            identidad.Name,
            identidad.User?.Value ?? string.Empty,
            Environment.MachineName);
    }
}

public sealed record ResultadoRegistroAuditoria(bool Exito, string Mensaje, string? RutaArchivo)
{
    public static ResultadoRegistroAuditoria Correcto(string rutaArchivo)
    {
        return new ResultadoRegistroAuditoria(true, string.Empty, rutaArchivo);
    }

    public static ResultadoRegistroAuditoria Error(string mensaje)
    {
        return new ResultadoRegistroAuditoria(false, mensaje, null);
    }
}

public sealed record ResultadoDisponibilidadAuditoria(
    bool Exito,
    string RutaSanitizada,
    string Mensaje)
{
    public static ResultadoDisponibilidadAuditoria Correcto(
        string rutaSanitizada,
        bool sinPendientes)
    {
        return new ResultadoDisponibilidadAuditoria(
            sinPendientes,
            rutaSanitizada,
            sinPendientes ? string.Empty : "Hay eventos de auditoria pendientes de confirmar.");
    }

    public static ResultadoDisponibilidadAuditoria Error(
        string rutaSanitizada,
        string mensaje)
    {
        return new ResultadoDisponibilidadAuditoria(false, rutaSanitizada, mensaje);
    }
}

internal sealed record IdentidadAuditoria(
    string NombreUsuario,
    string Sid,
    string Equipo);

internal sealed record EventoAuditoria(
    int VersionEsquema,
    Guid EventoId,
    string Accion,
    string Resultado,
    string UsuarioWindows,
    string UsuarioSid,
    string? ScriptId,
    string? ScriptNombre,
    string? ScriptSha256,
    Guid? EjecucionId,
    int? CodigoSalida,
    string? Motivo,
    string? Detalle,
    DateTimeOffset FechaUtc,
    DateTimeOffset FechaLocal,
    string Equipo);
