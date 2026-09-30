// (Autor: Alex Roman)
// Descripcion: Verifica dibujo WPF, entrada nativa y conservacion del foco con salida continua.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using LanzadorScripts.ModelosVista;
using LanzadorScripts.Servicios;
using LanzadorScripts.Vistas;
using Xunit;

namespace LanzadorScripts.Pruebas;

public sealed class PruebasVistaNativa
{
    private static readonly Task<Dispatcher> HiloVisual = CrearHiloVisual();

    [Theory]
    [InlineData(1280, 820, 96)]
    [InlineData(1000, 680, 96)]
    [InlineData(1280, 820, 144)]
    public async Task VentanaRealConservaBarraIconoYControlesNativos(int ancho, int alto, int dpi)
    {
        var dispatcher = await HiloVisual;
        await dispatcher.InvokeAsync(async () =>
        {
            using var cliente = new ClienteNativoSimulado();
            var ventana = new VentanaPrincipal(cliente) { Width = ancho, Height = alto, Left = -5000, Top = -5000, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                ventana.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var vista = (ClienteNativo)ventana.FindName("VistaCliente");
                Assert.NotNull(vista.Modelo);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)ventana.FindName("PanelArranque")).Visibility);
                Assert.Equal(WindowStyle.None, ventana.WindowStyle);
                Assert.NotNull(ventana.Icon);
                var icono = (Image)ventana.FindName("IconoBarraTitulo");
                Assert.True(icono.Source.Width > 0);
                foreach (var nombre in new[] { "BotonCerrar", "BotonMinimizar", "BotonMaximizar" })
                    Assert.True(((Button)ventana.FindName(nombre)).IsVisible);
                var detener = (Button)vista.FindName("BotonDetenerTodas");
                var limpiar = (Button)vista.FindName("BotonLimpiarFinalizadas");
                Assert.False(detener.IsEnabled);
                Assert.True(((Button)vista.FindName("BotonAuditoria")).IsVisible);
                await vista.Modelo!.EjecutarAsync(ClienteNativoSimulado.Script);
                await vista.Modelo.EjecutarAsync(ClienteNativoSimulado.Script);
                Assert.True(detener.IsEnabled);
                Assert.False(limpiar.IsEnabled);
                foreach (var consola in vista.Modelo.Consolas)
                    for (var i = 0; i < 60; i++) consola.Agregar(new EventoCliente("info", "Salida de la consola " + i + "\n", null, false));
                await Dispatcher.Yield(DispatcherPriority.Background);
                Dibujar(ventana, ancho, alto, dpi, "ventana-completa");
                var barra = (FrameworkElement)ventana.FindName("BarraTitulo");
                Assert.InRange(barra.ActualHeight, 39, 41);
                var saludo = (TextBlock)vista.FindName("SaludoUsuario");
                Assert.True(saludo.ActualWidth > 150);
                Assert.Equal(ancho >= 1280 ? 2 : 1, Descendientes<UniformGrid>(vista).Single().Columns);
                vista.Modelo.Consolas[0].Finalizar();
                await Dispatcher.Yield(DispatcherPriority.DataBind);
                Assert.True(limpiar.IsEnabled);
                Assert.True(detener.IsEnabled);
            }
            finally { ventana.Close(); }
        }).Task.Unwrap();
    }

    [Theory]
    [InlineData(1280, 720, 96)]
    [InlineData(960, 600, 96)]
    [InlineData(1280, 720, 144)]
    public async Task VistaDibujaConsolasYAjustesSinNavegador(int ancho, int alto, int dpi)
    {
        var dispatcher = await HiloVisual;
        await dispatcher.InvokeAsync(async () =>
        {
            using var cliente = new ClienteNativoSimulado();
            using var vista = new ClienteNativo();
            await vista.InicializarAsync(cliente);
            await vista.Modelo!.EjecutarAsync(ClienteNativoSimulado.Script);
            vista.Modelo.Consolas[0].Agregar(new EventoCliente("info", "Respuesta: \n", null, false));
            Dibujar(vista, ancho, alto, dpi, "consola");
            var consola = Descendientes<ConsolaNativa>(vista).Single();
            var entrada = (TextBox)consola.FindName("Entrada");
            var salida = (RichTextBox)consola.FindName("Salida");
            Assert.True(entrada.ActualWidth > 160);
            Assert.True(salida.ActualHeight > 120);
            Assert.True(entrada.TransformToAncestor(consola).Transform(new Point()).Y > salida.TransformToAncestor(consola).Transform(new Point()).Y);
            await vista.Modelo.AbrirAjustesAsync();
            var pestanas = (TabControl)vista.FindName("PestanasAjustes");
            for (var i = 0; i < pestanas.Items.Count; i++)
            {
                pestanas.SelectedIndex = i;
                Dibujar(vista, ancho, alto, dpi, "ajustes-" + i);
                if (i is 0 or 2) Assert.NotEmpty(Descendientes<DataGrid>(vista));
            }
        }).Task.Unwrap();
    }

    [Fact]
    public async Task SalidaNoBorraRespuestaNiMueveFocoYEnterEnvia()
    {
        var dispatcher = await HiloVisual;
        await dispatcher.InvokeAsync(async () =>
        {
            var modelo = new ConsolaNativaModelo(1);
            modelo.Iniciar(ClienteNativoSimulado.Script, Guid.NewGuid());
            var vista = new ConsolaNativa { DataContext = modelo };
            var ventana = new Window { Content = vista, Width = 700, Height = 400, Left = -5000, Top = -5000,
                ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
            try
            {
                ventana.Show();
                ventana.UpdateLayout();
                var entrada = (TextBox)vista.FindName("Entrada");
                var salida = (RichTextBox)vista.FindName("Salida");
                FocusManager.SetFocusedElement(ventana, entrada);
                entrada.Text = "respuesta en curso";
                for (var i = 0; i < 100; i++) modelo.Agregar(new EventoCliente("info", "salida " + i + "\n", null, false));
                await Dispatcher.Yield(DispatcherPriority.Background);
                Assert.Equal("respuesta en curso", modelo.Entrada);
                Assert.Same(entrada, FocusManager.GetFocusedElement(ventana));
                Assert.Contains("salida 99", new TextRange(salida.Document.ContentStart, salida.Document.ContentEnd).Text);
                var enviado = false;
                vista.Enviar += (_, _) => enviado = true;
                var tecla = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(ventana), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                entrada.RaiseEvent(tecla);
                Assert.True(enviado);
                Assert.True(tecla.Handled);
                modelo.Finalizar();
                Assert.False(entrada.IsEnabled);
            }
            finally { ventana.Close(); }
        }).Task.Unwrap();
    }

    [Theory]
    [InlineData(1280, 720, 1, 96)]
    [InlineData(960, 600, 1, 96)]
    [InlineData(1280, 720, 2, 96)]
    [InlineData(960, 600, 5, 96)]
    [InlineData(1280, 720, 1, 144)]
    public async Task SalidaAbundanteNoDesplazaEntradaFueraDelAreaVisible(int ancho, int alto, int cantidad, int dpi)
    {
        var dispatcher = await HiloVisual;
        await dispatcher.InvokeAsync(async () =>
        {
            using var cliente = new ClienteNativoSimulado();
            using var vista = new ClienteNativo();
            await vista.InicializarAsync(cliente);
            for (var i = 0; i < cantidad; i++) await vista.Modelo!.EjecutarAsync(ClienteNativoSimulado.Script);
            Dibujar(vista, ancho, alto, dpi, "entrada-antes-" + cantidad);
            var desplazamiento = (ScrollViewer)vista.FindName("DesplazamientoConsolas");
            var alturaInicial = Descendientes<ConsolaNativa>(vista).First().ActualHeight;
            foreach (var modelo in vista.Modelo!.Consolas)
                for (var i = 0; i < 200; i++) modelo.Agregar(new EventoCliente("info", "salida continua " + i + "\n", null, false));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Dibujar(vista, ancho, alto, dpi, "entrada-despues-" + cantidad);
            var consolas = Descendientes<ConsolaNativa>(vista).ToArray();
            Assert.Equal(cantidad, consolas.Length);
            Assert.InRange(consolas[0].ActualHeight, alturaInicial - 1, alturaInicial + 1);
            foreach (var consola in consolas)
            {
                var entrada = (TextBox)consola.FindName("Entrada");
                var salida = (RichTextBox)consola.FindName("Salida");
                // Cada consola conserva una salida acotada y su entrada en el mismo bloque.
                var posicion = entrada.TransformToAncestor(consola).Transform(new Point());
                Assert.True(posicion.Y + entrada.ActualHeight <= consola.ActualHeight);
                Assert.InRange(consola.ActualHeight, 280, desplazamiento.ViewportHeight);
                Assert.True(salida.ExtentHeight > salida.ViewportHeight);
                desplazamiento.ScrollToVerticalOffset(consola.TransformToAncestor(desplazamiento).Transform(new Point()).Y + desplazamiento.VerticalOffset);
                vista.UpdateLayout();
                var visible = entrada.TransformToAncestor(desplazamiento).Transform(new Point());
                Assert.InRange(visible.Y, 0, desplazamiento.ViewportHeight - entrada.ActualHeight);
            }
            Dibujar(vista, 960, 600, dpi, "entrada-redimensionada-" + cantidad);
            Assert.InRange(consolas[0].ActualHeight, 280, desplazamiento.ViewportHeight);
        }).Task.Unwrap();
    }

    private static void Dibujar(FrameworkElement vista, int ancho, int alto, int dpi, string nombre)
    {
        vista.Width = ancho;
        vista.Height = alto;
        vista.Measure(new Size(ancho, alto));
        vista.Arrange(new Rect(0, 0, ancho, alto));
        vista.UpdateLayout();
        var imagen = new RenderTargetBitmap(ancho * dpi / 96, alto * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
        imagen.Render(vista);
        var pixels = new byte[imagen.PixelWidth * imagen.PixelHeight * 4];
        imagen.CopyPixels(pixels, imagen.PixelWidth * 4, 0);
        Assert.Contains(pixels.Where((_, i) => i % 4 == 3), p => p != 0);
        Assert.True(pixels.Where((_, i) => i % 4 != 3).Distinct().Count() > 20);
        var carpeta = Path.Combine(AppContext.BaseDirectory, "VerificacionVisual");
        Directory.CreateDirectory(carpeta);
        var codificador = new PngBitmapEncoder();
        codificador.Frames.Add(BitmapFrame.Create(imagen));
        using var archivo = File.Create(Path.Combine(carpeta, $"{nombre}-{ancho}x{alto}-{dpi}.png"));
        codificador.Save(archivo);
    }

    private static IEnumerable<T> Descendientes<T>(DependencyObject padre) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(padre); i++)
        {
            var hijo = VisualTreeHelper.GetChild(padre, i);
            if (hijo is T elemento) yield return elemento;
            foreach (var descendiente in Descendientes<T>(hijo)) yield return descendiente;
        }
    }

    private static Task<Dispatcher> CrearHiloVisual()
    {
        // Mantiene un unico Application y dispatcher STA para todas las pruebas visuales.
        var listo = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hilo = new Thread(() =>
        {
            try
            {
                // Carga solo los recursos, sin iniciar la aplicacion ni conectar al servidor real.
                var raiz = new DirectoryInfo(AppContext.BaseDirectory);
                while (raiz is not null && !File.Exists(Path.Combine(raiz.FullName, "Aplicacion.xaml"))) raiz = raiz.Parent;
                if (raiz is null) throw new DirectoryNotFoundException("No se encontraron los estilos WPF.");
                var fuente = XDocument.Load(Path.Combine(raiz.FullName, "Aplicacion.xaml"));
                XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var recursos = new XElement(wpf + "ResourceDictionary",
                    new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                    fuente.Root!.Element(wpf + "Application.Resources")!.Elements());
                var aplicacion = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    Resources = (ResourceDictionary)XamlReader.Parse(recursos.ToString()) };
                listo.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex) { listo.TrySetException(ex); }
        }) { IsBackground = true, Name = "Pruebas WPF nativas" };
        hilo.SetApartmentState(ApartmentState.STA);
        hilo.Start();
        return listo.Task;
    }
}
