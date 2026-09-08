// (Autor: Alex Roman)
// Descripcion: Restringe el destino y los parametros de la telemetria tecnica.

namespace LanzadorScripts.Monitorizacion;

public enum ComponenteMonitorizado { ClienteInstalado, ClientePortable, ConsolaServidor, ServicioServidor }

internal sealed record ConfiguracionMonitorizacion(bool Habilitada, Uri Destino, string Entorno)
{
    internal const string DestinoGitLab = "https://138431379.otel.gitlab-o11y.com:14318/";

    internal static ConfiguracionMonitorizacion Leer(Func<string, string?> leer)
    {
        var activacion = leer("LANZADOR_MONITORIZACION_HABILITADA");
        // Un valor no reconocido desactiva el envio, sin impedir el arranque.
        var habilitada = activacion is null || activacion == "1"
            || string.Equals(activacion, "true", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(leer("OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY"), "disk", StringComparison.OrdinalIgnoreCase))
            habilitada = false;
        var destino = leer("LANZADOR_MONITORIZACION_ENDPOINT") ?? DestinoGitLab;
        if (!Uri.TryCreate(destino, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || uri.HostNameType != UriHostNameType.Dns)
        {
            return new(false, new Uri(DestinoGitLab), "production");
        }

        var entorno = leer("LANZADOR_MONITORIZACION_ENTORNO") switch
        {
            "test" => "test",
            "development" => "development",
            "staging" => "staging",
            _ => "production"
        };
        return new(habilitada, uri, entorno);
    }
}
