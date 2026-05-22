// (Autor: Alex Roman)
// Descripcion: Registra la extension de paquetes de configuracion para doble clic.

using Microsoft.Win32;

namespace LanzadorScripts.Servicios;

public static class ServicioAsociacionArchivos
{
    private const string TipoArchivo = "LanzadorScripts.ConfigPackage";

    public static void Registrar()
    {
        var rutaEjecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(rutaEjecutable))
        {
            return;
        }

        using var extension = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ServicioPaquetesConfiguracion.ExtensionPaquete}");
        extension?.SetValue(string.Empty, TipoArchivo);

        using var tipo = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{TipoArchivo}");
        tipo?.SetValue(string.Empty, "Configuracion de LanzadorScripts");

        using var comando = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{TipoArchivo}\shell\open\command");
        comando?.SetValue(string.Empty, $"\"{rutaEjecutable}\" \"%1\"");
    }
}
