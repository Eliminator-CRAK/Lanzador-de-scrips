// (Autor: Alex Roman)
// Descripcion: Estados posibles de una ejecucion de script.

namespace LanzadorScripts.Modelos;

public enum EstadoEjecucion
{
    Pendiente,
    Ejecutando,
    Finalizada,
    Error,
    Cancelada
}
