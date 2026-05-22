// (Autor: Alex Roman)
// Descripcion: Usuarios autorizados para ejecutar scripts en subcarpetas.

namespace LanzadorScripts.Modelos;

public sealed class ConfiguracionPermisos
{
    public List<string> EjecutoresSubcarpetas { get; set; } = [];
}
