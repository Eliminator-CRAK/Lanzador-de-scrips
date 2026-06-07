// (Autor: Alex Roman)
// Descripcion: Modelos versionados de permisos y paquetes firmados.

namespace LanzadorScripts.Modelos;

public sealed record PermisosLanzador(
    int Version,
    bool InicioAutomaticoWindows,
    IReadOnlyList<UsuarioPermisos> Usuarios,
    PoliticaSeguridadScriptsConfig SeguridadScripts);

public sealed record UsuarioPermisos(
    string NombreUsuario,
    string Rol,
    int MaxScriptsSimultaneos,
    IReadOnlyList<string> CarpetasPermitidas);

public sealed record PoliticaSeguridadScriptsConfig(
    IReadOnlyList<string> CertificadosPowerShellPermitidos,
    IReadOnlyList<HashBatchPermitido> HashesBatchPermitidos,
    IReadOnlyList<string> ScriptsElevadosPermitidos,
    bool PermitirExecutionPolicyBypass);

public sealed record HashBatchPermitido(string ScriptId, string Sha256);

public sealed record PaqueteConfiguracionFirmado(
    int Version,
    DateTimeOffset CreadoUtc,
    string Emisor,
    ConfiguracionLanzador Configuracion,
    PermisosLanzador Permisos);
