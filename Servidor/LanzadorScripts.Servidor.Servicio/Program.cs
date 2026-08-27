// (Autor: Alex Roman)
// Descripcion: Configura el proceso como servicio Windows autocontenido.

using LanzadorScripts.Servidor.Core;
using LanzadorScripts.Servidor.Servicio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Principal;

const string ArgumentoAdministradorInicial = "--preparar-administrador-inicial";
if (args.Length > 0)
{
    if (args.Length != 2
        || !string.Equals(args[0], ArgumentoAdministradorInicial, StringComparison.Ordinal))
    {
        Environment.ExitCode = 2;
        return;
    }

    try
    {
        using var identidad = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identidad).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException(
                "El aprovisionamiento inicial requiere una sesion administrativa elevada.");
        }

        new AlmacenAdministradorInicialServidor(new RutasServidor()).Preparar(args[1]);
        return;
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException
        or InvalidDataException
        or IOException
        or System.Security.Cryptography.CryptographicException)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
        return;
    }
}

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(opciones =>
    {
        opciones.ServiceName = ServicioCentralAlojado.NombreServicio;
    })
    .ConfigureServices(servicios =>
    {
        servicios.AddSingleton<RutasServidor>();
        servicios.AddSingleton<AlmacenConfiguracionServidor>();
        servicios.AddSingleton<RegistroServidor>();
        servicios.AddSingleton<IRegistroSpnServidor, RegistroSpnServidor>();
        servicios.AddHostedService<ServicioCentralAlojado>();
    })
    .Build();

await host.RunAsync();
