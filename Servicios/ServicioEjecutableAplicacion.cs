// (Autor: Alex Roman)
// Descripcion: Resuelve el ejecutable distribuido usado para relanzar la aplicacion.

using System.IO;

namespace LanzadorScripts.Servicios;

public static class ServicioEjecutableAplicacion
{
    internal const string VariableEjecutableDistribuido = "LANZADOR_DISTRIBUTION_EXE";

    public static string? ResolverRutaRelanzable()
    {
        return SeleccionarRutaEjecutable(
            Environment.GetEnvironmentVariable(VariableEjecutableDistribuido),
            Environment.ProcessPath,
            File.Exists);
    }

    internal static string? SeleccionarRutaEjecutable(
        string? rutaDistribuida,
        string? rutaProceso,
        Func<string, bool> existeArchivo)
    {
        // Prioriza el EXE unico que recibio el usuario.
        if (!string.IsNullOrWhiteSpace(rutaDistribuida) &&
            !rutaDistribuida.Contains('/') &&
            Path.IsPathFullyQualified(rutaDistribuida) &&
            !ServicioRutasSeguras.ContieneSegmentosNavegacion(rutaDistribuida) &&
            existeArchivo(rutaDistribuida!))
        {
            return Path.GetFullPath(rutaDistribuida!);
        }

        return rutaProceso;
    }
}
