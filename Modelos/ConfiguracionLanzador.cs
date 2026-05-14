// (Autor: Alex Roman)
// Descripcion: Configuracion persistente de la aplicacion.

using System.IO;

namespace LanzadorScripts.Modelos;

public sealed class ConfiguracionLanzador
{
    public string RutaScripts { get; set; } = @"\\MAD002MICROPRU\REPO";

    public string RutaLogs { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LanzadorScripts",
        "Logs");

    public int MaximoEjecucionesParalelas { get; set; } = 5;

    public void Normalizar()
    {
        if (string.IsNullOrWhiteSpace(RutaScripts))
        {
            RutaScripts = @"\\MAD002MICROPRU\REPO";
        }

        if (string.IsNullOrWhiteSpace(RutaLogs))
        {
            RutaLogs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "LanzadorScripts",
                "Logs");
        }

        MaximoEjecucionesParalelas = Math.Clamp(MaximoEjecucionesParalelas, 1, 50);
    }
}
