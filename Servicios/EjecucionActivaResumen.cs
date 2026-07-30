// (Autor: Alex Roman)
// Descripcion: Resume una ejecucion que sigue activa al cerrar la aplicacion.

namespace LanzadorScripts.Servicios;

public sealed record EjecucionActivaResumen(Guid Id, string NombreScript);
