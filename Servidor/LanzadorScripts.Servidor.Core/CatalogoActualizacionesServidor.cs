// (Autor: Alex Roman)
// Descripcion: Descubre y valida los MSI disponibles para los clientes instalados.

using LanzadorScripts.Protocolo;
using System.Text.Json;

namespace LanzadorScripts.Servidor.Core;

public sealed class CatalogoActualizacionesServidor
{
    public const string NombreRecursoCompartido = "LanzadorScriptsActualizaciones$";

    private readonly string _carpeta;
    private readonly PublicadorActualizacionesServidor? _publicador;
    private readonly Func<string, ResultadoValidacionPaqueteActualizacion> _validar;
    private readonly object _bloqueo = new();
    private readonly Dictionary<string, EntradaCache> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public CatalogoActualizacionesServidor(RutasServidor rutas)
        : this(rutas.RutaActualizaciones, ValidadorPaqueteActualizacion.Validar)
    {
        _publicador = new PublicadorActualizacionesServidor(rutas);
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
            var desactivados = LeerDesactivados();
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
                    resultado.Mensaje,
                    resultado.Valido && !desactivados.Contains(resultado.Sha256)))
                .ToList();
            var activa = resultados
                .Where(resultado => resultado.Valido && resultado.Version is not null && !desactivados.Contains(resultado.Sha256))
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

    public ResultadoValidacionPaqueteActualizacion Publicar(PublicarActualizacionServidor solicitud)
    {
        lock (_bloqueo)
        {
            var resultado = (_publicador ?? throw new InvalidOperationException("Publicacion no configurada.")).Publicar(solicitud);
            _cache.Clear();
            return resultado;
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
            .Where(paquete => paquete.Valido && paquete.Activo
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

    public EstadoActualizacionesServidorCentral Gestionar(GestionarActualizacionServidor solicitud)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        lock (_bloqueo)
        {
            if (string.IsNullOrWhiteSpace(solicitud.NombreArchivo) || solicitud.NombreArchivo != Path.GetFileName(solicitud.NombreArchivo)
                || solicitud.NombreArchivo.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !solicitud.NombreArchivo.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                || solicitud.Sha256 is null || solicitud.Sha256.Length != 64 || solicitud.Sha256.Any(c => !Uri.IsHexDigit(c))
                || solicitud.Eliminar == (solicitud.Activo is not null))
                throw new InvalidDataException("La seleccion de actualizacion no es valida.");
            RutasServidor.RechazarPuntoReanalisis(_carpeta);
            var ruta = Path.Combine(_carpeta, solicitud.NombreArchivo);
            RutasServidor.RechazarPuntoReanalisis(ruta);
            var paquete = _validar(ruta);
            if (!string.Equals(paquete.Sha256, solicitud.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("El paquete ha cambiado. Actualice la lista antes de continuar.");
            var desactivados = LeerDesactivados();
            if (solicitud.Eliminar)
            {
                if (paquete.Valido && !desactivados.Contains(paquete.Sha256) && ObtenerEstado().VersionActiva == paquete.Version?.ToString(3))
                    throw new InvalidOperationException("Desactive la version actual antes de eliminarla.");
                File.Delete(ruta);
            }
            else
            {
                if (solicitud.Activo == true && !paquete.Valido) throw new InvalidDataException("No se puede activar un paquete que no supera la validacion.");
                if (solicitud.Activo == true) desactivados.Remove(paquete.Sha256); else desactivados.Add(paquete.Sha256);
                GuardarDesactivados(desactivados);
            }
            _cache.Clear();
            return ObtenerEstado(true);
        }
    }

    private HashSet<string> LeerDesactivados()
    {
        var ruta = Path.Combine(_carpeta, "estado-actualizaciones.json");
        if (!File.Exists(ruta)) return new(StringComparer.OrdinalIgnoreCase);
        RutasServidor.RechazarPuntoReanalisis(ruta);
        using var archivo = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (archivo.Length > 1_048_576) throw new InvalidDataException("Estado de actualizaciones demasiado grande.");
        var hashes = JsonSerializer.Deserialize<string[]>(archivo) ?? throw new InvalidDataException("Estado de actualizaciones no valido.");
        if (hashes.Any(h => h is null || h.Length != 64 || h.Any(c => !Uri.IsHexDigit(c)))) throw new InvalidDataException("Hash de estado no valido.");
        return new(hashes, StringComparer.OrdinalIgnoreCase);
    }

    private void GuardarDesactivados(HashSet<string> hashes)
    {
        var ruta = Path.Combine(_carpeta, "estado-actualizaciones.json");
        RutasServidor.RechazarPuntoReanalisis(ruta);
        var temporal = ruta + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archivo = new FileStream(temporal, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(archivo, hashes.Order(StringComparer.OrdinalIgnoreCase).ToArray()); archivo.Flush(true); }
            File.Move(temporal, ruta, true);
        }
        finally { if (File.Exists(temporal)) File.Delete(temporal); }
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
