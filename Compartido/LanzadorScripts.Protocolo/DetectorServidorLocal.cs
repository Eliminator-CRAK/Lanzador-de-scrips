// (Autor: Alex Roman)
// Descripcion: Identifica el nombre corto y el FQDN del equipo Windows local.

using System.Net.NetworkInformation;

namespace LanzadorScripts.Protocolo;

public static class DetectorServidorLocal
{
    public static bool EsEquipoActual(string servidor)
    {
        var nombreServidor = AutenticacionServidorCentral.NormalizarServidor(servidor);
        return CoincideConNombreLocal(nombreServidor, ObtenerNombresLocales());
    }

    internal static bool CoincideConNombreLocal(
        string servidor,
        IEnumerable<string> nombresLocales)
    {
        // Compara nombres del sistema sin resolver direcciones ni alias remotos.
        var nombreServidor = AutenticacionServidorCentral.NormalizarServidor(servidor);
        return nombresLocales
            .Where(nombre => !string.IsNullOrWhiteSpace(nombre))
            .Select(IntentarNormalizar)
            .Where(nombre => nombre is not null)
            .Any(nombre => string.Equals(
                nombreServidor,
                nombre,
                StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyCollection<string> ObtenerNombresLocales()
    {
        var nombres = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Environment.MachineName
        };

        try
        {
            var propiedades = IPGlobalProperties.GetIPGlobalProperties();
            if (!string.IsNullOrWhiteSpace(propiedades.HostName))
            {
                nombres.Add(propiedades.HostName);
                if (!string.IsNullOrWhiteSpace(propiedades.DomainName))
                {
                    nombres.Add($"{propiedades.HostName}.{propiedades.DomainName}");
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Conserva el nombre corto proporcionado por Windows.
        }

        return nombres;
    }

    private static string? IntentarNormalizar(string nombre)
    {
        try
        {
            return AutenticacionServidorCentral.NormalizarServidor(nombre);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
