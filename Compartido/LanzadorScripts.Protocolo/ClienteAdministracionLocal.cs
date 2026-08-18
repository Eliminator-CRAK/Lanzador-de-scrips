// (Autor: Alex Roman)
// Descripcion: Conecta la consola administrativa con el servicio mediante un canal local protegido.

using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace LanzadorScripts.Protocolo;

public static class CanalAdministracionLocal
{
    public const string Nombre = "LanzadorScriptsServidor.Administracion.v1";
}

public sealed class ClienteAdministracionLocal
{
    private static readonly TimeSpan TiempoPredeterminado = TimeSpan.FromSeconds(8);
    private readonly TimeSpan _tiempoMaximo;

    public ClienteAdministracionLocal(TimeSpan? tiempoMaximo = null)
    {
        _tiempoMaximo = tiempoMaximo ?? TiempoPredeterminado;
    }

    public async Task<RespuestaTipada<TRespuesta>> EnviarAsync<TSolicitud, TRespuesta>(
        string operacion,
        TSolicitud datos,
        CancellationToken cancelacion)
    {
        if (string.IsNullOrWhiteSpace(operacion) || operacion.Length > 100)
        {
            throw new ArgumentException("La operacion del servidor no es valida.", nameof(operacion));
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancelacion);
        limite.CancelAfter(_tiempoMaximo);
        var solicitudId = Guid.NewGuid();
        var solicitud = new SolicitudServidor(
            TransporteProtocolo.VersionActual,
            solicitudId,
            operacion,
            TransporteProtocolo.CrearDatos(datos));
        try
        {
            await using var canal = new NamedPipeClientStream(
                ".",
                CanalAdministracionLocal.Nombre,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                TokenImpersonationLevel.Impersonation,
                HandleInheritability.None);
            await canal.ConnectAsync(_tiempoMaximo, limite.Token);
            await TransporteProtocolo.EscribirAsync(canal, solicitud, limite.Token);
            var respuesta = await TransporteProtocolo.LeerAsync<RespuestaServidor>(canal, limite.Token);
            if (respuesta.Version != TransporteProtocolo.VersionActual
                || respuesta.SolicitudId != solicitudId)
            {
                return RespuestaTipada<TRespuesta>.Error(
                    "protocolo_invalido",
                    "El servicio local devolvio una respuesta que no corresponde con la solicitud.");
            }

            if (!respuesta.Exito)
            {
                return RespuestaTipada<TRespuesta>.Error(respuesta.Codigo, respuesta.Mensaje);
            }

            var resultado = respuesta.Datos.Deserialize<TRespuesta>(TransporteProtocolo.OpcionesJson);
            return resultado is null
                ? RespuestaTipada<TRespuesta>.Error(
                    "respuesta_vacia",
                    "El servicio local no devolvio los datos esperados.")
                : RespuestaTipada<TRespuesta>.Correcta(resultado, respuesta.Mensaje);
        }
        catch (OperationCanceledException) when (!cancelacion.IsCancellationRequested)
        {
            return RespuestaTipada<TRespuesta>.Error(
                "tiempo_agotado",
                "El servicio local no respondio dentro del tiempo permitido.");
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return RespuestaTipada<TRespuesta>.Error(
                "canal_local_no_disponible",
                "No se pudo abrir el canal administrativo local. Compruebe que el servicio esta iniciado y que la consola se ejecuto como administrador.");
        }
    }
}
