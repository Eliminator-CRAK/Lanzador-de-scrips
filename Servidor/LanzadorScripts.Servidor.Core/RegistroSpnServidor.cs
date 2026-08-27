// (Autor: Alex Roman)
// Descripcion: Registra el SPN Kerberos del servicio en la cuenta de equipo.

using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LanzadorScripts.Protocolo;

namespace LanzadorScripts.Servidor.Core;

public interface IRegistroSpnServidor
{
    ResultadoRegistroSpn Registrar();

    ResultadoRegistroSpn Eliminar();
}

public sealed record ResultadoRegistroSpn(
    bool Exito,
    uint CodigoWin32,
    string SpnPrincipal,
    string Mensaje);

public sealed record EstadoAutenticacionServidor(
    bool Preparada,
    string SpnPrincipal,
    string Mensaje)
{
    public static EstadoAutenticacionServidor Pendiente { get; } = new(
        false,
        CrearSpnPrincipal(),
        "El registro Kerberos esta pendiente.");

    private static string CrearSpnPrincipal()
    {
        try
        {
            return $"{AutenticacionServidorCentral.ClaseSpn}/{Dns.GetHostEntry(Environment.MachineName).HostName}";
        }
        catch (SocketException)
        {
            return $"{AutenticacionServidorCentral.ClaseSpn}/{Environment.MachineName}";
        }
    }
}

public sealed class RegistroSpnServidor : IRegistroSpnServidor
{
    private const uint ErrorCorrecto = 0;
    private const uint ErrorAccesoDenegado = 5;

    private readonly Func<bool> _esSistemaLocal;
    private readonly Func<OperacionRegistroSpn, uint> _ejecutar;

    public RegistroSpnServidor()
        : this(EsSistemaLocal, EjecutarNativo)
    {
    }

    internal RegistroSpnServidor(
        Func<bool> esSistemaLocal,
        Func<OperacionRegistroSpn, uint> ejecutar)
    {
        _esSistemaLocal = esSistemaLocal;
        _ejecutar = ejecutar;
    }

    public ResultadoRegistroSpn Registrar()
    {
        return Ejecutar(OperacionRegistroSpn.Agregar, "registrado");
    }

    public ResultadoRegistroSpn Eliminar()
    {
        return Ejecutar(OperacionRegistroSpn.Eliminar, "eliminado");
    }

    private ResultadoRegistroSpn Ejecutar(OperacionRegistroSpn operacion, string accion)
    {
        var spn = EstadoAutenticacionServidor.Pendiente.SpnPrincipal;
        if (!_esSistemaLocal())
        {
            return new ResultadoRegistroSpn(
                false,
                ErrorAccesoDenegado,
                spn,
                "El SPN solo se puede administrar desde el servicio LocalSystem.");
        }

        try
        {
            var codigo = _ejecutar(operacion);
            return codigo == ErrorCorrecto
                ? new ResultadoRegistroSpn(
                    true,
                    codigo,
                    spn,
                    $"SPN Kerberos {accion} en la cuenta de equipo.")
                : new ResultadoRegistroSpn(
                    false,
                    codigo,
                    spn,
                    $"Windows no pudo administrar el SPN: {new Win32Exception((int)codigo).Message}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new ResultadoRegistroSpn(
                false,
                uint.MaxValue,
                spn,
                $"La API de Active Directory no esta disponible: {ex.GetType().Name}.");
        }
    }

    private static bool EsSistemaLocal()
    {
        using var identidad = WindowsIdentity.GetCurrent();
        var sistema = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        return identidad.User?.Equals(sistema) == true;
    }

    private static uint EjecutarNativo(OperacionRegistroSpn operacion)
    {
        return DsServerRegisterSpnW(
            operacion,
            AutenticacionServidorCentral.ClaseSpn,
            null);
    }

    [DllImport("ntdsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint DsServerRegisterSpnW(
        OperacionRegistroSpn operacion,
        string claseServicio,
        string? usuarioDn);
}

internal enum OperacionRegistroSpn : uint
{
    Agregar = 0,
    Eliminar = 2
}
