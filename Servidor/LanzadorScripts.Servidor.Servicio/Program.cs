// (Autor: Alex Roman)
// Descripcion: Configura el proceso como servicio Windows autocontenido.

using LanzadorScripts.Servidor.Core;
using LanzadorScripts.Servidor.Servicio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        servicios.AddHostedService<ServicioCentralAlojado>();
    })
    .Build();

await host.RunAsync();
