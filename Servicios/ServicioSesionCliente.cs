// (Autor: Alex Roman)
// Descripcion: Mantiene un bloqueo por sesion y recupera sus datos efimeros sin seguir enlaces.

using System.IO;

namespace LanzadorScripts.Servicios;

internal sealed class ServicioSesionCliente : IDisposable
{
    private static readonly string NombreSesion = "Sesion-" + Guid.NewGuid().ToString("N");
    internal static string RaizSesionesInstaladas => Path.Combine(RutasAplicacion.RaizLocalAppData, "SesionesCliente");
    internal static string RutaActual => RutasAplicacion.Distribucion.EsPortable
        ? RutasAplicacion.Distribucion.RaizPortable!
        : Path.Combine(RaizSesionesInstaladas, NombreSesion);
    private readonly FileStream _bloqueo;
    private readonly string _ruta;
    private readonly string _marcador;
    private readonly bool _portable;

    public ServicioSesionCliente() : this(RutaActual, RutasAplicacion.Distribucion.EsPortable) { }

    internal ServicioSesionCliente(string ruta, bool portable)
    {
        _ruta = ruta;
        _portable = portable;
        var raiz = Path.GetDirectoryName(_ruta)!;
        ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(raiz);
        Directory.CreateDirectory(raiz);
        if (!_portable) LimpiarAbandonadas(raiz, _ruta);
        ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(_ruta);
        Directory.CreateDirectory(_ruta);
        _marcador = _portable ? Path.Combine(_ruta, "cliente.lock") : _ruta + ".lock";
        ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(_marcador);
        _bloqueo = new FileStream(_marcador, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal static void LimpiarAbandonadas(string raiz, string actual)
    {
        ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(raiz);
        foreach (var carpeta in Directory.EnumerateDirectories(raiz, "Sesion-*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(carpeta, actual, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(carpeta)[7..], "N", out _)
                || Directory.GetCreationTimeUtc(carpeta) > DateTime.UtcNow.AddMinutes(-1)) continue;
            try
            {
                ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(carpeta);
                // Solo las sesiones gestionadas por este cliente tienen este marcador.
                var marcador = carpeta + ".lock";
                if (!File.Exists(marcador)) continue;
                ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(marcador);
                using (var bloqueo = new FileStream(marcador, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(raiz, carpeta);
                }
                File.Delete(marcador);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        // Recupera el marcador si Windows termino el proceso despues de borrar la carpeta.
        foreach (var marcador in Directory.EnumerateFiles(raiz, "Sesion-*.lock", SearchOption.TopDirectoryOnly))
        {
            var nombre = Path.GetFileNameWithoutExtension(marcador);
            var carpeta = Path.Combine(raiz, nombre);
            if (!Guid.TryParseExact(nombre[7..], "N", out _)
                || Directory.Exists(carpeta)
                || File.GetCreationTimeUtc(marcador) > DateTime.UtcNow.AddMinutes(-1)) continue;
            try
            {
                ServicioDirectoriosAplicacion.RechazarPuntosReanalisis(marcador);
                // DeleteOnClose mantiene el bloqueo hasta eliminar el marcador.
                using var bloqueo = new FileStream(marcador, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        if (_portable)
        {
            // El lanzador nativo conserva el bloqueo y limpia despues de terminar este proceso.
            _bloqueo.Dispose();
            return;
        }
        try
        {
            ServicioDirectoriosAplicacion.EliminarArbolSinAtravesarReanalisis(Path.GetDirectoryName(_ruta)!, _ruta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El marcador permite reintentar en el siguiente arranque tras liberar procesos de Windows.
        }
        finally { _bloqueo.Dispose(); }
        if (!Directory.Exists(_ruta))
        {
            try
            {
                File.Delete(_marcador);
                Directory.Delete(Path.GetDirectoryName(_ruta)!, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
