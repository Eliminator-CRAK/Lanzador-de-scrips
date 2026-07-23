// (Autor: Alex Roman)
// Descripcion: Representa una ruta de artefacto validada antes de acceder al disco.

using System.IO;

namespace LanzadorScripts.Servicios;

internal sealed class RutaArchivoProtegidoValidada
{
    internal RutaArchivoProtegidoValidada(string rutaCompleta)
    {
        RutaCompleta = rutaCompleta;
    }

    public string RutaCompleta { get; }

    public FileStream AbrirLectura()
    {
        // Abre unicamente la ruta aprobada por el servicio de rutas.
        return new FileStream(
            RutaCompleta,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
    }
}
