// (Autor: Alex Roman)
// Descripcion: Gestiona el inicio automatico elevado de la aplicacion en Windows.

using System.Diagnostics;
using System.IO;

namespace LanzadorScripts.Servicios;

public static class ServicioInicioAutomatico
{
    private const string NombreTarea = "LanzadorScripts";

    public static void Aplicar(bool habilitado)
    {
        if (habilitado)
        {
            CrearTarea();
            return;
        }

        EliminarTarea();
    }

    private static void CrearTarea()
    {
        var rutaEjecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(rutaEjecutable))
        {
            return;
        }

        EjecutarSchtasks($"/Create /F /TN \"{NombreTarea}\" /SC ONLOGON /RL HIGHEST /TR \"\\\"{rutaEjecutable}\\\"\"");
    }

    private static void EliminarTarea()
    {
        EjecutarSchtasks($"/Delete /F /TN \"{NombreTarea}\"");
    }

    private static void EjecutarSchtasks(string argumentos)
    {
        var inicio = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            Arguments = argumentos,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proceso = Process.Start(inicio);
        proceso?.WaitForExit(5000);
    }
}
