// (Autor: Alex Roman)
// Descripcion: Inicializa el cliente web y su backend local.

using System.Windows;
using LanzadorScripts.Servicios;

namespace LanzadorScripts;

public partial class VentanaPrincipal : Window
{
    private readonly ServidorLocalWeb _servidor;

    public VentanaPrincipal()
    {
        InitializeComponent();
        _servidor = ServidorLocalWeb.Iniciar();
        CargarClienteAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _servidor.Dispose();
        base.OnClosed(e);
    }

    private async void CargarClienteAsync()
    {
        await VistaCliente.EnsureCoreWebView2Async();
        VistaCliente.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        VistaCliente.CoreWebView2.Settings.AreDevToolsEnabled = false;
        VistaCliente.Source = _servidor.UrlBase;
    }
}
