// (Autor: Alex Roman)
// Descripcion: Autoriza y ejecuta las operaciones solicitadas al servidor.

using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servidor.Core;

public sealed class ProcesadorSolicitudesServidor
{
    private readonly RepositorioServidor _repositorio;
    private readonly Func<EstadoAutenticacionServidor> _obtenerEstadoAutenticacion;

    public ProcesadorSolicitudesServidor(
        RepositorioServidor repositorio,
        Func<EstadoAutenticacionServidor>? obtenerEstadoAutenticacion = null)
    {
        _repositorio = repositorio;
        _obtenerEstadoAutenticacion = obtenerEstadoAutenticacion
            ?? (() => EstadoAutenticacionServidor.Pendiente);
    }

    public RespuestaServidor Procesar(string identidadRemota, SolicitudServidor solicitud)
    {
        if (solicitud.Version != TransporteProtocolo.VersionActual
            || solicitud.SolicitudId == Guid.Empty
            || string.IsNullOrWhiteSpace(solicitud.Operacion))
        {
            return Error(solicitud.SolicitudId, "protocolo_invalido", "La solicitud no es valida.");
        }

        var cuenta = ConfiguracionServidor.NormalizarCuenta(identidadRemota);
        if (cuenta.Length == 0)
        {
            return Error(solicitud.SolicitudId, "identidad_invalida", "No se pudo identificar la cuenta de Windows.");
        }

        try
        {
            return solicitud.Operacion switch
            {
                OperacionesServidor.Salud => ProcesarSalud(cuenta, solicitud),
                OperacionesServidor.ObtenerPermisos => ProcesarObtenerPermisos(cuenta, solicitud),
                OperacionesServidor.GuardarPermisos => ProcesarGuardarPermisos(cuenta, solicitud),
                OperacionesServidor.ObtenerCatalogo => ProcesarObtenerCatalogo(cuenta, solicitud),
                OperacionesServidor.GuardarCatalogo => ProcesarGuardarCatalogo(cuenta, solicitud),
                OperacionesServidor.RegistrarAuditoria => ProcesarRegistrarAuditoria(cuenta, solicitud),
                OperacionesServidor.ConsultarAuditoria => ProcesarConsultarAuditoria(cuenta, solicitud),
                OperacionesServidor.ListarUsuarios => ProcesarListarUsuarios(cuenta, solicitud),
                OperacionesServidor.GuardarUsuario => ProcesarGuardarUsuario(cuenta, solicitud),
                OperacionesServidor.EliminarUsuario => ProcesarEliminarUsuario(cuenta, solicitud),
                OperacionesServidor.CrearCopiaSeguridad => ProcesarCopia(cuenta, solicitud),
                OperacionesServidor.ComprobarIntegridad => ProcesarIntegridad(cuenta, solicitud),
                _ => Error(
                    solicitud.SolicitudId,
                    "operacion_desconocida",
                    "La operacion solicitada no esta permitida.")
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return Error(
                solicitud.SolicitudId,
                "operacion_rechazada",
                $"La operacion no se pudo completar: {LimitarMensaje(ex.Message)}");
        }
        catch (JsonException)
        {
            return Error(
                solicitud.SolicitudId,
                "solicitud_invalida",
                "La solicitud no contiene datos validos.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error(
                solicitud.SolicitudId,
                "almacen_no_disponible",
                "El almacenamiento protegido del servidor no esta disponible.");
        }
        catch
        {
            return Error(
                solicitud.SolicitudId,
                "error_interno",
                "El servidor no pudo procesar la solicitud.");
        }
    }

    private RespuestaServidor ProcesarSalud(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EstaAutorizado(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        var autenticacion = _obtenerEstadoAutenticacion();
        var estado = _repositorio.ObtenerEstado() with
        {
            AutenticacionRemotaPreparada = autenticacion.Preparada,
            SpnServidor = autenticacion.SpnPrincipal,
            MensajeAutenticacion = autenticacion.Mensaje
        };
        return Correcta(solicitud.SolicitudId, estado);
    }

    private RespuestaServidor ProcesarObtenerPermisos(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EstaAutorizado(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        return Correcta(
            solicitud.SolicitudId,
            _repositorio.ObtenerPermisos(cuenta, incluirTodos: _repositorio.EsAdministrador(cuenta)));
    }

    private RespuestaServidor ProcesarGuardarPermisos(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EsAdministrador(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        var permisos = Deserializar<JsonObject>(solicitud.Datos);
        var resultado = _repositorio.GuardarPermisos(permisos);
        RegistrarAccionAdministrativa(cuenta, "administracion.permisos", "guardado", string.Empty);
        return Correcta(solicitud.SolicitudId, resultado, "Permisos guardados en la base central.");
    }

    private RespuestaServidor ProcesarObtenerCatalogo(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EstaAutorizado(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        return Correcta(solicitud.SolicitudId, _repositorio.ObtenerCatalogo());
    }

    private RespuestaServidor ProcesarGuardarCatalogo(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EsAdministrador(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        var catalogo = Deserializar<JsonObject>(solicitud.Datos);
        var resultado = _repositorio.GuardarCatalogo(catalogo);
        RegistrarAccionAdministrativa(cuenta, "administracion.catalogo", "guardado", string.Empty);
        return Correcta(solicitud.SolicitudId, resultado, "Catalogo guardado en la base central.");
    }

    private RespuestaServidor ProcesarRegistrarAuditoria(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EstaAutorizado(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        var recibido = Deserializar<EventoAuditoriaServidorCentral>(solicitud.Datos);
        var ahora = DateTimeOffset.Now;
        var evento = recibido with
        {
            UsuarioWindows = cuenta,
            UsuarioSid = ResolverSid(cuenta),
            FechaUtc = ahora.ToUniversalTime(),
            FechaLocal = ahora
        };
        _repositorio.RegistrarAuditoria(evento);
        return Correcta(solicitud.SolicitudId, true, "Auditoria confirmada.");
    }

    private RespuestaServidor ProcesarConsultarAuditoria(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EsAdministrador(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        var filtro = Deserializar<FiltroAuditoriaServidorCentral>(solicitud.Datos);
        return Correcta(solicitud.SolicitudId, _repositorio.ConsultarAuditoria(filtro));
    }

    private RespuestaServidor ProcesarListarUsuarios(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EsAdministrador(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        return Correcta(solicitud.SolicitudId, _repositorio.ListarUsuarios());
    }

    private RespuestaServidor ProcesarGuardarUsuario(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EsAdministrador(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        var usuario = _repositorio.GuardarUsuario(
            Deserializar<GuardarUsuarioServidorCentral>(solicitud.Datos));
        RegistrarAccionAdministrativa(
            cuenta,
            "administracion.usuario",
            "guardado",
            usuario.NombreUsuario);
        return Correcta(solicitud.SolicitudId, usuario, "Usuario guardado.");
    }

    private RespuestaServidor ProcesarEliminarUsuario(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EsAdministrador(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        var eliminacion = Deserializar<EliminarUsuarioServidorCentral>(solicitud.Datos);
        var actual = _repositorio.ListarUsuarios().FirstOrDefault(usuario =>
            string.Equals(usuario.Id, eliminacion.Id, StringComparison.OrdinalIgnoreCase));
        if (actual is not null
            && string.Equals(actual.NombreUsuario, cuenta, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("No se puede eliminar la cuenta que administra la sesion actual.");
        }

        _repositorio.EliminarUsuario(eliminacion.Id);
        RegistrarAccionAdministrativa(
            cuenta,
            "administracion.usuario",
            "eliminado",
            actual?.NombreUsuario ?? eliminacion.Id);
        return Correcta(solicitud.SolicitudId, true, "Usuario eliminado.");
    }

    private RespuestaServidor ProcesarCopia(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EsAdministrador(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        var copia = _repositorio.CrearCopiaSeguridad();
        RegistrarAccionAdministrativa(
            cuenta,
            "mantenimiento.copia",
            "creada",
            copia.NombreArchivo);
        return Correcta(solicitud.SolicitudId, copia, "Copia de seguridad creada en el servidor.");
    }

    private RespuestaServidor ProcesarIntegridad(string cuenta, SolicitudServidor solicitud)
    {
        if (!_repositorio.EsAdministrador(cuenta))
        {
            return AccesoDenegado(solicitud.SolicitudId);
        }

        return Correcta(solicitud.SolicitudId, _repositorio.ComprobarIntegridad());
    }

    private void RegistrarAccionAdministrativa(
        string cuenta,
        string accion,
        string resultado,
        string detalle)
    {
        var ahora = DateTimeOffset.Now;
        _repositorio.RegistrarAuditoria(new EventoAuditoriaServidorCentral(
            Guid.NewGuid().ToString("N"),
            accion,
            resultado,
            cuenta,
            ResolverSid(cuenta),
            Environment.MachineName,
            null,
            null,
            null,
            null,
            null,
            string.Empty,
            detalle,
            ahora.ToUniversalTime(),
            ahora));
    }

    private static string ResolverSid(string cuenta)
    {
        try
        {
            return ((SecurityIdentifier)new NTAccount(cuenta).Translate(
                typeof(SecurityIdentifier))).Value;
        }
        catch (IdentityNotMappedException)
        {
            return string.Empty;
        }
    }

    private static T Deserializar<T>(JsonElement datos)
    {
        return datos.Deserialize<T>(TransporteProtocolo.OpcionesJson)
            ?? throw new InvalidDataException("La solicitud no contiene los datos esperados.");
    }

    private static RespuestaServidor Correcta<T>(
        Guid solicitudId,
        T datos,
        string mensaje = "")
    {
        return new RespuestaServidor(
            TransporteProtocolo.VersionActual,
            solicitudId,
            true,
            "ok",
            mensaje,
            TransporteProtocolo.CrearDatos(datos));
    }

    private static RespuestaServidor Error(Guid solicitudId, string codigo, string mensaje)
    {
        return new RespuestaServidor(
            TransporteProtocolo.VersionActual,
            solicitudId,
            false,
            codigo,
            mensaje,
            TransporteProtocolo.CrearDatos(new { }));
    }

    private static RespuestaServidor AccesoDenegado(Guid solicitudId)
    {
        return Error(
            solicitudId,
            "acceso_denegado",
            "La cuenta de Windows no tiene permisos para esta operacion.");
    }

    private static string LimitarMensaje(string mensaje)
    {
        var valor = mensaje.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return valor.Length <= 500 ? valor : valor[..500];
    }
}
