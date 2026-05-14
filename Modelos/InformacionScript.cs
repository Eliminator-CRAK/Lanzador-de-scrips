// (Autor: Alex Roman)
// Descripcion: Informacion de un script disponible.

namespace LanzadorScripts.Modelos;

public sealed class InformacionScript
{
    public required string Nombre { get; init; }

    public required string RutaCompleta { get; init; }

    public required string RutaRelativa { get; init; }

    public required string Carpeta { get; init; }

    public bool RequierePermisoExtra { get; init; }

    public bool EstaAutorizado { get; init; }

    public string TextoPermiso => RequierePermisoExtra
        ? EstaAutorizado ? "Subcarpeta autorizada" : "Subcarpeta bloqueada"
        : "Raiz";
}
