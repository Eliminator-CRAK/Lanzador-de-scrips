// (Autor: Alex Roman)
// Descripcion: Modelos versionados de permisos y paquetes.

namespace LanzadorScripts.Modelos;

public sealed record PermisosLanzador(
    int Version,
    IReadOnlyList<UsuarioPermisos> Usuarios,
    PoliticaSeguridadScriptsConfig SeguridadScripts);

public sealed record UsuarioPermisos(
    string NombreUsuario,
    string Rol,
    int MaxScriptsSimultaneos,
    IReadOnlyList<string> CarpetasPermitidas);

public sealed record PoliticaSeguridadScriptsConfig(
    IReadOnlyList<string> ScriptsElevadosPermitidos,
    bool PermitirExecutionPolicyBypass);

public sealed record PaqueteConfiguracionFirmado(
    int Version,
    DateTimeOffset CreadoUtc,
    string Emisor,
    ConfiguracionLanzador Configuracion,
    PermisosLanzador Permisos);
