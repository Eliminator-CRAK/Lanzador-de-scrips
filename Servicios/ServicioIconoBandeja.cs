// (Autor: Alex Roman)
// Descripcion: Mantiene el icono y los comandos de la aplicacion en la bandeja.

using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace LanzadorScripts.Servicios;

public sealed class ServicioIconoBandeja : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Icon _icono;
    private readonly Forms.NotifyIcon _notificador;
    private readonly Forms.ContextMenuStrip _menu;
    private bool _liberado;

    public ServicioIconoBandeja(
        Dispatcher dispatcher,
        Action restaurar,
        Action maximizar,
        Action minimizar,
        Action cerrar)
    {
        _dispatcher = dispatcher;
        _icono = CargarIcono();
        _menu = CrearMenu(restaurar, maximizar, minimizar, cerrar);
        _notificador = new Forms.NotifyIcon
        {
            Icon = _icono,
            Text = "Lanzador de Scripts",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notificador.DoubleClick += (_, _) => Ejecutar(restaurar);
    }

    public void Dispose()
    {
        // Retira el icono antes de liberar sus recursos nativos.
        if (_liberado)
        {
            return;
        }

        _liberado = true;
        _notificador.Visible = false;
        _notificador.Dispose();
        _menu.Dispose();
        _icono.Dispose();
    }

    private Forms.ContextMenuStrip CrearMenu(
        Action restaurar,
        Action maximizar,
        Action minimizar,
        Action cerrar)
    {
        // Crea los comandos visibles al pulsar con el boton derecho.
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(CrearOpcion("Abrir / Restaurar", restaurar, negrita: true));
        menu.Items.Add(CrearOpcion("Maximizar", maximizar));
        menu.Items.Add(CrearOpcion("Minimizar", minimizar));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(CrearOpcion("Cerrar", cerrar));
        return menu;
    }

    private Forms.ToolStripMenuItem CrearOpcion(string texto, Action accion, bool negrita = false)
    {
        // Envia cada comando al dispatcher principal de WPF.
        var opcion = new Forms.ToolStripMenuItem(texto);
        if (negrita)
        {
            opcion.Font = new Font(opcion.Font, FontStyle.Bold);
        }

        opcion.Click += (_, _) => Ejecutar(accion);
        return opcion;
    }

    private void Ejecutar(Action accion)
    {
        // Evita ejecutar operaciones WPF desde otro hilo.
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(accion);
    }

    private static Icon CargarIcono()
    {
        // Carga una copia independiente del icono incluido.
        var recurso = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Recursos/IconoLanzador.ico"))
            ?? throw new InvalidOperationException("No se encontro el icono de la aplicacion.");
        using var iconoRecurso = new Icon(recurso.Stream);
        return (Icon)iconoRecurso.Clone();
    }
}
