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

    public ClienteServidorCentral(
        string servidor,
        int puerto,
        TimeSpan? tiempoMaximo = null,
        bool exigirAutenticacionMutua = true)
    {
        _servidor = ValidarServidor(servidor);
        _puerto = puerto is >= 1024 and <= 65535
            ? puerto
            : throw new ArgumentOutOfRangeException(nameof(puerto));
        _tiempoMaximo = tiempoMaximo ?? TiempoPredeterminado;
        _exigirAutenticacionMutua = exigirAutenticacionMutua;
    }

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
            using var tcp = new TcpClient
            {
                NoDelay = true
            };
            await tcp.ConnectAsync(_servidor, _puerto, limite.Token);
            await using var seguro = new NegotiateStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false);
            await seguro.AuthenticateAsClientAsync(
                CredentialCache.DefaultNetworkCredentials,
                $"HOST/{_servidor}",
                ProtectionLevel.EncryptAndSign,
                TokenImpersonationLevel.Identification)
                .WaitAsync(limite.Token);
            ValidarCanal(seguro);

            await TransporteProtocolo.EscribirAsync(seguro, solicitud, limite.Token);
            var respuesta = await TransporteProtocolo.LeerAsync<RespuestaServidor>(seguro, limite.Token);
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
        catch (OperationCanceledException) when (!cancelacion.IsCancellationRequested)
        {
            return RespuestaTipada<TRespuesta>.Error(
                "tiempo_agotado",
                "El servidor central no respondio dentro del tiempo permitido.");
        }
        catch (Exception ex) when (ex is SocketException
            or AuthenticationException
            or IOException
            or JsonException)
        {
            return RespuestaTipada<TRespuesta>.Error(
                "servidor_no_disponible",
                $"No se pudo establecer una conexion segura con el servidor central: {ex.GetType().Name}.");
        }
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

    private static string ValidarServidor(string servidor)
    {
        var valor = servidor?.Trim().TrimEnd('.')
            ?? throw new ArgumentNullException(nameof(servidor));
        if (valor.Length is <= 0 or > 253
            || valor.Contains('\\', StringComparison.Ordinal)
            || valor.Contains('/', StringComparison.Ordinal)
            || valor.Contains(':', StringComparison.Ordinal)
            || valor.Split('.').Any(segmento => segmento.Length is <= 0 or > 63
                || segmento[0] == '-'
                || segmento[^1] == '-'
                || segmento.Any(caracter => !char.IsLetterOrDigit(caracter) && caracter != '-')))
        {
            throw new ArgumentException("El nombre del servidor central no es valido.", nameof(servidor));
        }

        return valor;
    }
}
