// (Autor: Alex Roman)
// Descripcion: Consulta, descarga y prepara actualizaciones MSI del cliente instalado.

using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using LanzadorScripts.Modelos;
using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servicios;

public sealed record ResultadoConsultaActualizacion(
    bool Disponible,
    ActualizacionClienteServidor? Actualizacion,
    string Mensaje);

public sealed record ProgresoActualizacionCliente(
    string Fase,
    long BytesCompletados,
    long BytesTotales);

public sealed record PaqueteActualizacionPreparado(
    string Version,
    string RutaMsi,
    string RutaActualizador,
    string Sha256);

public sealed class ServicioActualizacionesCliente
{
    public const string NombreActualizador = "LanzadorScripts.Actualizador.exe";

    private static readonly TimeSpan TiempoConsulta = TimeSpan.FromSeconds(5);
    private readonly Func<ConfiguracionLanzador> _obtenerConfiguracion;
    private readonly ContextoDistribucion _distribucion;

    public ServicioActualizacionesCliente(
        Func<ConfiguracionLanzador>? obtenerConfiguracion = null,
        ContextoDistribucion? distribucion = null)
    {
        _obtenerConfiguracion = obtenerConfiguracion ?? (() => new ServicioConfiguracion().Cargar());
        _distribucion = distribucion ?? RutasAplicacion.Distribucion;
    }

    public async Task<ResultadoConsultaActualizacion> ConsultarAsync(
        CancellationToken cancelacion)
    {
        if (_distribucion.EsPortable)
        {
            return new ResultadoConsultaActualizacion(
                false,
                null,
                "La distribucion portable no admite actualizaciones MSI.");
        }

        try
        {
            var configuracion = _obtenerConfiguracion();
            var version = ObtenerVersionActual();
            var cliente = new ClienteServidorCentral(
                configuracion.ServidorCentral,
                configuracion.PuertoServidorCentral,
                TiempoConsulta);
            var respuesta = await cliente.EnviarAsync<
                ConsultaActualizacionCliente,
                ActualizacionClienteServidor>(
                OperacionesServidor.ObtenerActualizacion,
                new ConsultaActualizacionCliente(version, "x64", "Instalada"),
                cancelacion);
            if (!respuesta.Exito || respuesta.Datos is null)
            {
                return new ResultadoConsultaActualizacion(false, null, respuesta.Mensaje);
            }

            if (!respuesta.Datos.Disponible)
            {
                return new ResultadoConsultaActualizacion(false, null, string.Empty);
            }

            ValidarMetadatos(respuesta.Datos, version);
            return new ResultadoConsultaActualizacion(true, respuesta.Datos, string.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException
            or AuthenticationException
            or CryptographicException
            or FormatException
            or InvalidDataException
            or InvalidOperationException
            or IOException
            or JsonException
            or SocketException
            or TimeoutException
            or UnauthorizedAccessException)
        {
            return new ResultadoConsultaActualizacion(false, null, LimitarMensaje(ex.Message));
        }
    }

    public async Task<PaqueteActualizacionPreparado> DescargarYPrepararAsync(
        ActualizacionClienteServidor actualizacion,
        IProgress<ProgresoActualizacionCliente>? progreso,
        CancellationToken cancelacion)
    {
        ArgumentNullException.ThrowIfNull(actualizacion);
        if (_distribucion.EsPortable)
        {
            throw new InvalidOperationException("La portable no puede instalar paquetes MSI.");
        }

        var versionActual = ObtenerVersionActual();
        ValidarMetadatos(actualizacion, versionActual);
        var configuracion = _obtenerConfiguracion();
        var origen = ConstruirRutaRemota(configuracion.ServidorCentral, actualizacion);
        var carpeta = PrepararCarpetaStaging();
        var rutaMsi = Path.Combine(carpeta, actualizacion.NombreArchivo);
        var rutaActualizador = Path.Combine(carpeta, NombreActualizador);
        try
        {
            progreso?.Report(new ProgresoActualizacionCliente(
                "Descargando",
                0,
                actualizacion.Longitud));
            await CopiarMsiAsync(
                origen,
                rutaMsi,
                actualizacion.Longitud,
                progreso,
                cancelacion);

            progreso?.Report(new ProgresoActualizacionCliente(
                "Verificando",
                actualizacion.Longitud,
                actualizacion.Longitud));
            var validacion = await Task.Run(
                () => ValidadorPaqueteActualizacion.Validar(rutaMsi),
                cancelacion);
            if (!validacion.Valido
                || validacion.Version?.ToString(3) != actualizacion.Version
                || validacion.Longitud != actualizacion.Longitud
                || !CompararHexadecimal(validacion.Sha256, actualizacion.Sha256))
            {
                throw new InvalidDataException(
                    string.IsNullOrWhiteSpace(validacion.Mensaje)
                        ? "El MSI descargado no coincide con los metadatos del servidor."
                        : validacion.Mensaje);
            }

            var origenActualizador = Path.Combine(AppContext.BaseDirectory, NombreActualizador);
            _ = ValidadorPaqueteActualizacion.ValidarFirmaAuthenticode(origenActualizador);
            File.Copy(origenActualizador, rutaActualizador, overwrite: false);
            _ = ValidadorPaqueteActualizacion.ValidarFirmaAuthenticode(rutaActualizador);
            return new PaqueteActualizacionPreparado(
                actualizacion.Version,
                rutaMsi,
                rutaActualizador,
                actualizacion.Sha256);
        }
        catch
        {
            IntentarEliminarStaging(carpeta);
            throw;
        }
    }

    public static Process IniciarActualizador(PaqueteActualizacionPreparado paquete)
    {
        ArgumentNullException.ThrowIfNull(paquete);
        var rutaActualizador = Path.GetFullPath(paquete.RutaActualizador);
        var carpeta = Path.GetDirectoryName(rutaActualizador)
            ?? throw new InvalidOperationException("El actualizador no tiene una carpeta valida.");
        if (!ServicioRutasSeguras.EstaDentroDeCarpeta(
                RutasAplicacion.RutaStagingActualizaciones,
                carpeta)
            || !string.Equals(
                Path.GetFileName(rutaActualizador),
                NombreActualizador,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("La ruta preparada del actualizador no es valida.");
        }

        var inicio = new ProcessStartInfo
        {
            FileName = rutaActualizador,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = carpeta
        };
        inicio.ArgumentList.Add("--instalar");
        inicio.ArgumentList.Add(Path.GetFileName(paquete.RutaMsi));
        inicio.ArgumentList.Add(paquete.Sha256);
        inicio.ArgumentList.Add(paquete.Version);
        inicio.ArgumentList.Add(Environment.ProcessId.ToString());
        return Process.Start(inicio)
            ?? throw new InvalidOperationException("No se pudo iniciar el actualizador firmado.");
    }

    public static void LimpiarStagingAbandonado()
    {
        if (RutasAplicacion.Distribucion.EsPortable
            || !Directory.Exists(RutasAplicacion.RutaStagingActualizaciones))
        {
            return;
        }

        foreach (var carpeta in Directory.EnumerateDirectories(
                     RutasAplicacion.RutaStagingActualizaciones,
                     "Sesion-*",
                     SearchOption.TopDirectoryOnly))
        {
            IntentarEliminarStaging(carpeta);
        }
    }

    public static async Task ReintentarLimpiezaStagingAbandonadoAsync()
    {
        // Reintenta cuando el actualizador anterior ya ha liberado su ejecutable.
        await Task.Delay(TimeSpan.FromSeconds(5));
        try
        {
            LimpiarStagingAbandonado();
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
        }
    }

    internal static string ConstruirRutaRemota(
        string servidor,
        ActualizacionClienteServidor actualizacion)
    {
        ArgumentNullException.ThrowIfNull(actualizacion);
        ValidarNombreSimple(actualizacion.NombreArchivo, "nombre del MSI");
        if (!string.Equals(
                actualizacion.RecursoCompartido,
                "LanzadorScriptsActualizaciones$",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("El recurso de actualizaciones no es el autorizado.");
        }

        if (string.IsNullOrWhiteSpace(servidor)
            || servidor.Contains('\\')
            || servidor.Contains('/')
            || servidor.Contains(':'))
        {
            throw new InvalidDataException("El servidor de actualizaciones no es valido.");
        }

        return $@"\\{servidor}\{actualizacion.RecursoCompartido}\{actualizacion.NombreArchivo}";
    }

    private static async Task CopiarMsiAsync(
        string origen,
        string destino,
        long longitudEsperada,
        IProgress<ProgresoActualizacionCliente>? progreso,
        CancellationToken cancelacion)
    {
        using var entrada = new FileStream(
            origen,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (entrada.Length != longitudEsperada)
        {
            throw new InvalidDataException("El tamano del MSI remoto cambio antes de la descarga.");
        }

        await using var salida = new FileStream(
            destino,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int leidos;
        while ((leidos = await entrada.ReadAsync(buffer, cancelacion)) > 0)
        {
            await salida.WriteAsync(buffer.AsMemory(0, leidos), cancelacion);
            total += leidos;
            if (total > longitudEsperada)
            {
                throw new InvalidDataException("El MSI remoto supera el tamano anunciado.");
            }

            progreso?.Report(new ProgresoActualizacionCliente(
                "Descargando",
                total,
                longitudEsperada));
        }

        await salida.FlushAsync(cancelacion);
        if (total != longitudEsperada)
        {
            throw new EndOfStreamException("La descarga del MSI termino antes de completarse.");
        }
    }

    private static string PrepararCarpetaStaging()
    {
        ServicioDirectoriosAplicacion.PrepararDirectorioBase(
            RutasAplicacion.RutaActualizacionesCliente);
        Directory.CreateDirectory(RutasAplicacion.RutaStagingActualizaciones);
        ServicioDirectoriosAplicacion.PrepararDirectorioBase(
            RutasAplicacion.RutaStagingActualizaciones);
        var carpeta = Path.Combine(
            RutasAplicacion.RutaStagingActualizaciones,
            $"Sesion-{Guid.NewGuid():N}");
        ServicioDirectoriosAplicacion.PrepararDirectorioPrivado(carpeta);
        return carpeta;
    }

    private static void ValidarMetadatos(
        ActualizacionClienteServidor actualizacion,
        string versionActual)
    {
        if (!actualizacion.Disponible)
        {
            throw new InvalidDataException("El servidor no ofrece una actualizacion.");
        }

        ValidarNombreSimple(actualizacion.NombreArchivo, "nombre del MSI");
        if (string.IsNullOrWhiteSpace(actualizacion.Version)
            || string.IsNullOrWhiteSpace(actualizacion.Sha256)
            || !ValidadorPaqueteActualizacion.EsVersionPosterior(
                actualizacion.Version,
                versionActual)
            || actualizacion.Longitud is <= 0 or > ValidadorPaqueteActualizacion.LongitudMaxima
            || actualizacion.Sha256.Length != 64
            || actualizacion.Sha256.Any(caracter => !Uri.IsHexDigit(caracter)))
        {
            throw new InvalidDataException("Los metadatos de actualizacion no son validos.");
        }

        _ = ConstruirRutaRemota("servidor", actualizacion);
    }

    private static void ValidarNombreSimple(string valor, string descripcion)
    {
        if (string.IsNullOrWhiteSpace(valor)
            || valor.Length > 180
            || valor.Contains("..", StringComparison.Ordinal)
            || valor.Contains('\\')
            || valor.Contains('/')
            || valor.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(valor, Path.GetFileName(valor), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"El {descripcion} no es valido.");
        }
    }

    private static string ObtenerVersionActual()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version
            ?? throw new InvalidOperationException("No se pudo determinar la version instalada.");
        return version.ToString(3);
    }

    private static bool CompararHexadecimal(string primero, string segundo)
    {
        if (primero.Length != segundo.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(primero),
            Convert.FromHexString(segundo));
    }

    private static void IntentarEliminarStaging(string carpeta)
    {
        try
        {
            ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(
                RutasAplicacion.RutaStagingActualizaciones,
                carpeta);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
        }
    }

    private static string LimitarMensaje(string mensaje)
    {
        var valor = mensaje.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return valor.Length <= 300 ? valor : valor[..300];
    }
}
