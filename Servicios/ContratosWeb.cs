// (Autor: Alex Roman)
// Descripcion: Contratos internos usados por la API web local.

namespace LanzadorScripts.Servicios;

public sealed record UsuarioCliente(string NombreUsuario, string Rol, int MaxScriptsSimultaneos, bool EstaAutorizado, string MotivoBloqueo = "");

public sealed record ScriptInterno(string Id, string Nombre, string Tipo, string RutaCompleta);

public sealed record EventoCliente(string Tipo, string Mensaje, string? Color = null, bool Finalizado = false);
