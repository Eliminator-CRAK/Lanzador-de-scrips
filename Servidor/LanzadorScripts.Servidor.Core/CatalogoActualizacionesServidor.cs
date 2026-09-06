// (Autor: Alex Roman)
// Descripcion: Descubre y valida los MSI disponibles para los clientes instalados.

using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servidor.Core;

public sealed class CatalogoActualizacionesServidor
{
    public const string NombreRecursoCompartido = "LanzadorScriptsActualizaciones$";

    private readonly string _carpeta;
    private readonly Func<string, ResultadoValidacionPaqueteActualizacion> _validar;
    private readonly object _bloqueo = new();
    private readonly Dictionary<string, EntradaCache> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public CatalogoActualizacionesServidor(RutasServidor rutas)
        : this(rutas.RutaActualizaciones, ValidadorPaqueteActualizacion.Validar)
    {
    }

    internal CatalogoActualizacionesServidor(
        string carpeta,
        Func<string, ResultadoValidacionPaqueteActualizacion> validar)
    {
        _carpeta = Path.GetFullPath(carpeta);
        _validar = validar ?? throw new ArgumentNullException(nameof(validar));
    }

    public EstadoActualizacionesServidorCentral ObtenerEstado(bool forzarValidacion = false)
    {
        lock (_bloqueo)
        {
            Directory.CreateDirectory(_carpeta);
            RutasServidor.RechazarPuntoReanalisis(_carpeta);
            if (forzarValidacion)
            {
                _cache.Clear();
            }

            var rutas = Directory
                .EnumerateFiles(_carpeta, "*.msi", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .OrderBy(ruta => ruta, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var vigentes = new HashSet<string>(rutas, StringComparer.OrdinalIgnoreCase);
            foreach (var retirada in _cache.Keys.Where(ruta => !vigentes.Contains(ruta)).ToList())
            {
                _cache.Remove(retirada);
            }

            var resultados = rutas.Select(ObtenerResultado).ToList();
            var paquetes = resultados
                .OrderByDescending(resultado => resultado.Version)
                .ThenBy(resultado => resultado.NombreArchivo, StringComparer.OrdinalIgnoreCase)
                .Select(resultado => new PaqueteActualizacionServidorCentral(
                    resultado.NombreArchivo,
                    resultado.Version?.ToString(3) ?? string.Empty,
                    resultado.Longitud,
                    resultado.Sha256,
                    resultado.FechaUtc,
                    resultado.Valido,
                    resultado.EstadoFirma,
                    resultado.Mensaje))
                .ToList();
            var activa = resultados
                .Where(resultado => resultado.Valido && resultado.Version is not null)
                .OrderByDescending(resultado => resultado.Version)
                .FirstOrDefault();
            var mensaje = activa is null
                ? "No hay ningun MSI valido publicado."
                : $"Version activa: {activa.Version!.ToString(3)}.";
            return new EstadoActualizacionesServidorCentral(
                _carpeta,
                $@"\\{Environment.MachineName}\{NombreRecursoCompartido}",
                activa?.Version?.ToString(3) ?? string.Empty,
                paquetes,
                DateTimeOffset.UtcNow,
                mensaje);
        }
    }

    public ActualizacionClienteServidor ObtenerActualizacion(
        ConsultaActualizacionCliente consulta)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        if (!string.Equals(consulta.Arquitectura, "x64", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(consulta.TipoDistribucion, "Instalada", StringComparison.OrdinalIgnoreCase)
            || !Version.TryParse(consulta.VersionCliente, out var versionCliente))
        {
            throw new InvalidDataException("Los datos de la version cliente no son validos.");
        }

        var estado = ObtenerEstado();
        var activa = estado.Paquetes
            .Where(paquete => paquete.Valido
                && Version.TryParse(paquete.Version, out var version)
                && version > versionCliente)
            .OrderByDescending(paquete => Version.Parse(paquete.Version))
            .FirstOrDefault();
        return activa is null
            ? new ActualizacionClienteServidor(
                false,
                string.Empty,
                string.Empty,
                NombreRecursoCompartido,
                0,
                string.Empty,
                DateTimeOffset.MinValue)
            : new ActualizacionClienteServidor(
                true,
                activa.Version,
                activa.NombreArchivo,
                NombreRecursoCompartido,
                activa.Longitud,
                activa.Sha256,
                activa.FechaUtc);
    }

    private ResultadoValidacionPaqueteActualizacion ObtenerResultado(string ruta)
    {
        try
        {
            RutasServidor.RechazarPuntoReanalisis(ruta);
            var archivo = new FileInfo(ruta);
            var clave = new ClaveCache(archivo.Length, archivo.LastWriteTimeUtc);
            if (_cache.TryGetValue(ruta, out var entrada) && entrada.Clave == clave)
            {
                return entrada.Resultado;
            }

            var resultado = _validar(ruta);
            _cache[ruta] = new EntradaCache(clave, resultado);
            return resultado;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var archivo = new FileInfo(ruta);
            return ResultadoValidacionPaqueteActualizacion.Rechazado(
                archivo.Name,
                archivo.Exists ? archivo.Length : 0,
                archivo.Exists
                    ? new DateTimeOffset(archivo.LastWriteTimeUtc, TimeSpan.Zero)
                    : DateTimeOffset.MinValue,
                ex.Message);
        }
    }

    private readonly record struct ClaveCache(long Longitud, DateTime UltimaEscrituraUtc);

    private sealed record EntradaCache(
        ClaveCache Clave,
        ResultadoValidacionPaqueteActualizacion Resultado);
}
