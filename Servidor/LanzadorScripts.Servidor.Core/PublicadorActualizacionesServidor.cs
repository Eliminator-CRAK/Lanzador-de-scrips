// (Autor: Alex Roman)
// Descripcion: Prepara paquetes en una carpeta privada y los publica mediante un movimiento atomico.

using System.Security.Cryptography;
using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servidor.Core;

public sealed class PublicadorActualizacionesServidor
{
    private readonly string _staging;
    private readonly string _destino;
    private readonly Func<string, ResultadoValidacionPaqueteActualizacion> _validar;
    private readonly bool _protegerPublicacion;

    public PublicadorActualizacionesServidor(RutasServidor rutas)
        : this(Path.Combine(rutas.RutaDatos, "PublicacionMSI"), rutas.RutaActualizaciones,
            ValidadorPaqueteActualizacion.Validar) { _protegerPublicacion = true; }

    internal PublicadorActualizacionesServidor(string staging, string destino,
        Func<string, ResultadoValidacionPaqueteActualizacion> validar)
    {
        _staging = Path.GetFullPath(staging);
        _destino = Path.GetFullPath(destino);
        _validar = validar;
    }

    public (PublicarActualizacionServidor Solicitud, ResultadoValidacionPaqueteActualizacion Paquete) Preparar(string origen)
    {
        if (!Path.IsPathFullyQualified(origen))
            throw new InvalidDataException("Seleccione una ruta absoluta al MSI.");
        RutasServidor.RechazarPuntoReanalisis(origen);
        var id = Guid.NewGuid();
        var nombre = Path.GetFileName(origen);
        var ruta = RutaPreparada(id, nombre);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        try
        {
            using (var entrada = new FileStream(origen, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var salida = new FileStream(ruta, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (entrada.Length is <= 0 or > ValidadorPaqueteActualizacion.LongitudMaxima)
                    throw new InvalidDataException("El MSI tiene un tamano no permitido.");
                entrada.CopyTo(salida);
                salida.Flush(flushToDisk: true);
            }
            var paquete = ExigirValido(ruta);
            return (new(id, nombre, paquete.Sha256), paquete);
        }
        catch
        {
            Descartar(new(id, nombre, string.Empty));
            throw;
        }
    }

    public ResultadoValidacionPaqueteActualizacion Publicar(PublicarActualizacionServidor solicitud)
    {
        var origen = RutaPreparada(solicitud.PreparacionId, solicitud.NombreArchivo);
        RutasServidor.RechazarPuntoReanalisis(_destino);
        Directory.CreateDirectory(_destino);
        var destino = Path.Combine(_destino, solicitud.NombreArchivo);
        var paquete = ExigirValido(origen);
        if (!string.Equals(paquete.Sha256, solicitud.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("El MSI cambio desde la confirmacion.");
        if (_protegerPublicacion) RutasServidor.PrepararArchivoActualizacion(origen);

        // Bloquea escrituras mientras se comprueba el contenido y se cambia el nombre.
        using var bloqueo = new FileStream(origen, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bloqueo)), paquete.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("El MSI cambio durante la publicacion.");
        if (File.Exists(destino))
        {
            var existente = ExigirValido(destino);
            if (!string.Equals(existente.Sha256, paquete.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Ya existe otro MSI con esa version. Incremente la version del producto.");
            return existente;
        }
        File.Move(origen, destino, overwrite: false);
        return paquete;
    }

    public void Descartar(PublicarActualizacionServidor solicitud)
    {
        var ruta = RutaPreparada(solicitud.PreparacionId, solicitud.NombreArchivo);
        if (File.Exists(ruta)) File.Delete(ruta);
        var carpeta = Path.GetDirectoryName(ruta)!;
        if (Directory.Exists(carpeta)) Directory.Delete(carpeta, recursive: false);
    }

    private string RutaPreparada(Guid id, string nombre)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(nombre) || nombre.Length > 120
            || nombre != Path.GetFileName(nombre) || nombre.Contains(':') || nombre.Contains('/')
            || !nombre.StartsWith("LanzadorScripts-", StringComparison.Ordinal)
            || !nombre.EndsWith("-x64.msi", StringComparison.Ordinal) || nombre.Contains(".."))
            throw new InvalidDataException("La identificacion del paquete no es valida.");
        var ruta = Path.Combine(_staging, id.ToString("N"), nombre);
        RutasServidor.RechazarPuntoReanalisis(ruta);
        return ruta;
    }

    private ResultadoValidacionPaqueteActualizacion ExigirValido(string ruta)
    {
        var resultado = _validar(ruta);
        if (!resultado.Valido) throw new InvalidDataException(resultado.Mensaje);
        return resultado;
    }
}
