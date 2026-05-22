// (Autor: Alex Roman)
// Descripcion: Busca scripts PowerShell disponibles en la ruta configurada.

using System.IO;
using LanzadorScripts.Modelos;

namespace LanzadorScripts.Servicios;

public sealed class ServicioDescubrimientoScripts
{
    private static readonly HashSet<string> CarpetasExcluidas = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "PERMISOS"
    };

    public IReadOnlyList<InformacionScript> Descubrir(string rutaScripts, ServicioPermisos servicioPermisos)
    {
        if (string.IsNullOrWhiteSpace(rutaScripts) || !Directory.Exists(rutaScripts))
        {
            return [];
        }

        var raiz = Path.GetFullPath(rutaScripts);
        var scripts = new List<InformacionScript>();

        foreach (var ruta in EnumerarScriptsPowerShell(raiz))
        {
            var rutaRelativa = Path.GetRelativePath(raiz, ruta);
            var carpeta = Path.GetDirectoryName(rutaRelativa);
            var requierePermisoExtra = servicioPermisos.RequierePermisoExtra(raiz, ruta);

            scripts.Add(new InformacionScript
            {
                Nombre = Path.GetFileName(ruta),
                RutaCompleta = ruta,
                RutaRelativa = rutaRelativa,
                Carpeta = string.IsNullOrWhiteSpace(carpeta) ? "." : carpeta,
                RequierePermisoExtra = requierePermisoExtra,
                EstaAutorizado = !requierePermisoExtra || servicioPermisos.PuedeEjecutar(raiz, ruta)
            });
        }

        return scripts
            .OrderBy(script => script.Carpeta, StringComparer.OrdinalIgnoreCase)
            .ThenBy(script => script.Nombre, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerarScriptsPowerShell(string raiz)
    {
        var pendientes = new Stack<string>();
        pendientes.Push(raiz);

        while (pendientes.Count > 0)
        {
            var actual = pendientes.Pop();

            IEnumerable<string> archivos;
            try
            {
                archivos = Directory.EnumerateFiles(actual, "*.ps1");
            }
            catch
            {
                continue;
            }

            foreach (var archivo in archivos)
            {
                yield return archivo;
            }

            IEnumerable<string> carpetas;
            try
            {
                carpetas = Directory.EnumerateDirectories(actual);
            }
            catch
            {
                continue;
            }

            foreach (var carpeta in carpetas)
            {
                if (!CarpetasExcluidas.Contains(Path.GetFileName(carpeta)))
                {
                    pendientes.Push(carpeta);
                }
            }
        }
    }
}
