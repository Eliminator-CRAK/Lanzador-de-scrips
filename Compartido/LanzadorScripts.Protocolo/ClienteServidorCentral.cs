// (Autor: Alex Roman)
// Descripcion: Cliente autenticado para acceder al servicio central.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text.Json;

namespace LanzadorScripts.Protocolo;

public sealed class ClienteServidorCentral
{
    private static readonly TimeSpan TiempoPredeterminado = TimeSpan.FromSeconds(8);

    private readonly string _servidor;
    private readonly int _puerto;
    private readonly TimeSpan _tiempoMaximo;
    private readonly bool _exigirAutenticacionMutua;
    private readonly ClienteAdministracionLocal? _clienteLocal;

    public ClienteServidorCentral(
        string servidor,
        int puerto,
        TimeSpan? tiempoMaximo = null,
        bool exigirAutenticacionMutua = true)
    {
        _servidor = AutenticacionServidorCentral.NormalizarServidor(servidor);
        _puerto = puerto is >= 1024 and <= 65535
            ? puerto
            : throw new ArgumentOutOfRangeException(nameof(puerto));
        _tiempoMaximo = tiempoMaximo ?? TiempoPredeterminado;
        _exigirAutenticacionMutua = exigirAutenticacionMutua;
        if (DetectorServidorLocal.EsEquipoActual(_servidor))
        {
            _clienteLocal = new ClienteAdministracionLocal(_tiempoMaximo);
        }
    }

    internal bool UsaCanalLocal => _clienteLocal is not null;

    public RespuestaTipada<TRespuesta> Enviar<TSolicitud, TRespuesta>(
        string operacion,
        TSolicitud datos)
    {
        return EnviarAsync<TSolicitud, TRespuesta>(operacion, datos, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
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

        if (_clienteLocal is not null)
        {
            // El bucle local usa el pipe autenticado y nunca recurre a TCP o NTLM.
            return await _clienteLocal.EnviarAsync<TSolicitud, TRespuesta>(
                operacion,
                datos,
                cancelacion);
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
            AuthenticationException? ultimoErrorAutenticacion = null;
            foreach (var spn in AutenticacionServidorCentral.CrearSpnCandidatos(_servidor))
            {
                try
                {
                    return await EnviarConSpnAsync<TRespuesta>(
                        solicitud,
                        solicitudId,
                        spn,
                        limite.Token);
                }
                catch (AuthenticationException ex)
                {
                    ultimoErrorAutenticacion = ex;
                }
            }

            var detalle = ultimoErrorAutenticacion is null
                ? string.Empty
                : $" Detalle: {LimitarMensaje(ultimoErrorAutenticacion.Message)}";
            return RespuestaTipada<TRespuesta>.Error(
                "autenticacion_windows",
                $"Windows no pudo autenticar mutuamente el servidor con Kerberos. "
                + $"Compruebe el SPN '{AutenticacionServidorCentral.ClaseSpn}/{_servidor}'.{detalle}");
        }
        catch (OperationCanceledException) when (!cancelacion.IsCancellationRequested)
        {
            return RespuestaTipada<TRespuesta>.Error(
                "tiempo_agotado",
                "El servidor central no respondio dentro del tiempo permitido.");
        }
        catch (Exception ex) when (ex is SocketException
            or IOException
            or JsonException)
        {
            return RespuestaTipada<TRespuesta>.Error(
                "servidor_no_disponible",
                $"No se pudo establecer una conexion segura con el servidor central: {ex.GetType().Name}.");
        }
    }

    private async Task<RespuestaTipada<TRespuesta>> EnviarConSpnAsync<TRespuesta>(
        SolicitudServidor solicitud,
        Guid solicitudId,
        string spn,
        CancellationToken cancelacion)
    {
        using var tcp = new TcpClient
        {
            NoDelay = true
        };
        await tcp.ConnectAsync(_servidor, _puerto, cancelacion);
        await using var seguro = new NegotiateStream(
            tcp.GetStream(),
            leaveInnerStreamOpen: false);
        await seguro.AuthenticateAsClientAsync(
            CredentialCache.DefaultNetworkCredentials,
            spn,
            ProtectionLevel.EncryptAndSign,
            TokenImpersonationLevel.Identification)
            .WaitAsync(cancelacion);
        ValidarCanal(seguro);

        await TransporteProtocolo.EscribirAsync(seguro, solicitud, cancelacion);
        var respuesta = await TransporteProtocolo.LeerAsync<RespuestaServidor>(seguro, cancelacion);
        if (respuesta.Version != TransporteProtocolo.VersionActual
            || respuesta.SolicitudId != solicitudId)
        {
            return RespuestaTipada<TRespuesta>.Error(
                "protocolo_invalido",
                "El servidor devolvio una respuesta que no corresponde con la solicitud.");
        }

        if (!respuesta.Exito)
        {
            return RespuestaTipada<TRespuesta>.Error(respuesta.Codigo, respuesta.Mensaje);
        }

        var resultado = respuesta.Datos.Deserialize<TRespuesta>(TransporteProtocolo.OpcionesJson);
        return resultado is null
            ? RespuestaTipada<TRespuesta>.Error(
                "respuesta_vacia",
                "El servidor no devolvio los datos esperados.")
            : RespuestaTipada<TRespuesta>.Correcta(resultado, respuesta.Mensaje);
    }

    private static string LimitarMensaje(string mensaje)
    {
        // Devuelve un diagnostico breve sin saltos de linea.
        var valor = mensaje.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return valor.Length <= 300 ? valor : valor[..300];
    }

    private void ValidarCanal(NegotiateStream seguro)
    {
        if (!seguro.IsAuthenticated || !seguro.IsEncrypted || !seguro.IsSigned)
        {
            throw new AuthenticationException("El canal no quedo autenticado, cifrado y firmado.");
        }

        if (_exigirAutenticacionMutua && !seguro.IsMutuallyAuthenticated)
        {
            throw new AuthenticationException("El servidor no pudo autenticarse mutuamente.");
        }
    }

}
