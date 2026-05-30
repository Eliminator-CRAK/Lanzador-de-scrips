// (Autor: Alex Roman)
// Descripcion: Resultado tipado de la validacion de scripts.

namespace LanzadorScripts.Servicios;

public enum CodigoValidacionScript
{
    Correcto,
    RutaScriptsNoConfigurada,
    RutaScriptsNoDisponible,
    IdentificadorVacio,
    IdentificadorNoRelativo,
    IdentificadorNoPermitido,
    CarpetaExcluida,
    ExtensionNoPermitida,
    ScriptFueraDeRaiz,
    ScriptNoEncontrado,
    EnlaceNoPermitido,
    MetacaracterPeligroso
}

public sealed record ResultadoValidacionScript(
    CodigoValidacionScript Codigo,
    string Mensaje,
    ScriptInterno? Script = null)
{
    public bool EsValido => Codigo == CodigoValidacionScript.Correcto && Script is not null;

    public static ResultadoValidacionScript Correcto(ScriptInterno script)
    {
        return new ResultadoValidacionScript(CodigoValidacionScript.Correcto, string.Empty, script);
    }

    public static ResultadoValidacionScript Error(CodigoValidacionScript codigo, string mensaje)
    {
        return new ResultadoValidacionScript(codigo, mensaje);
    }
}

public sealed record ResultadoValidacionConfiguracion(bool EsValida, string Mensaje)
{
    public static ResultadoValidacionConfiguracion Correcta()
    {
        return new ResultadoValidacionConfiguracion(true, string.Empty);
    }

    public static ResultadoValidacionConfiguracion Error(string mensaje)
    {
        return new ResultadoValidacionConfiguracion(false, mensaje);
    }
}
