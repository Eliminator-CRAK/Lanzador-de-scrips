// (Autor: Alex Roman)
// Descripcion: Rutas usadas por la aplicacion.

using System.IO;

namespace LanzadorScripts.Servicios;

public static class RutasAplicacion
{
    public static string RaizProgramData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LanzadorScripts");

    public static string RutaConfiguracionLocal => Path.Combine(AppContext.BaseDirectory, "configuracion.json");

    public static string RutaConfiguracionProgramData => Path.Combine(RaizProgramData, "configuracion.json");
}
