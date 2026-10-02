// (Autor: Alex Roman)
// Descripcion: Renderiza la salida del proceso sin afectar el foco del campo de respuesta.

using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LanzadorScripts.ModelosVista;
using LanzadorScripts.Servicios;

namespace LanzadorScripts.Vistas;

public partial class ConsolaNativa : UserControl
{
    private ConsolaNativaModelo? _modelo;
    private Paragraph _parrafo = new() { Margin = new Thickness(0) };
    private bool _entradaProtegida;
    public event RoutedEventHandler? Enviar;
    public event RoutedEventHandler? Cerrar;
    public ConsolaNativa() { InitializeComponent(); Salida.Document.Blocks.Add(_parrafo); }

    private void Modelo_Cambiado(object sender, DependencyPropertyChangedEventArgs e) => Conectar();
    private void Vista_Cargada(object sender, RoutedEventArgs e) => Conectar();
    private void Vista_Descargada(object sender, RoutedEventArgs e) => Desconectar();

    private void Conectar()
    {
        Desconectar();
        _modelo = DataContext as ConsolaNativaModelo;
        Salida.Document.Blocks.Clear();
        _parrafo = new Paragraph { Margin = new Thickness(0) };
        Salida.Document.Blocks.Add(_parrafo);
        if (_modelo is null) return;
        foreach (var evento in _modelo.Eventos) AgregarSalida(evento);
        _modelo.Eventos.CollectionChanged += Salida_Cambiada;
        _modelo.PropertyChanged += Estado_Cambiado;
        AplicarEntradaProtegida(_modelo.EntradaProtegida);
        if (_modelo.Activa) { if (_entradaProtegida) EntradaOculta.Focus(); else Entrada.Focus(); }
    }

    private void Desconectar()
    {
        if (_modelo is null) return;
        _modelo.Eventos.CollectionChanged -= Salida_Cambiada;
        _modelo.PropertyChanged -= Estado_Cambiado;
        _modelo = null;
    }

    private void Salida_Cambiada(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Solo desplaza la salida si el usuario no esta leyendo un punto anterior.
        var seguir = Salida.VerticalOffset + Salida.ViewportHeight >= Salida.ExtentHeight - 24;
        if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Reset)
        {
            _parrafo.Inlines.Clear();
            if (_modelo is not null) foreach (var evento in _modelo.Eventos) AgregarSalida(evento);
        }
        else if (e.NewItems is not null) foreach (EventoCliente evento in e.NewItems) AgregarSalida(evento);
        if (seguir) Salida.ScrollToEnd();
    }

    private void AgregarSalida(EventoCliente evento)
    {
        var color = evento.Tipo == "error" ? Color.FromRgb(244, 71, 71) : evento.Tipo == "exito" ? Color.FromRgb(181, 206, 168) : Color.FromRgb(209, 213, 219);
        if (evento.Mensaje.StartsWith(">", StringComparison.Ordinal)) _parrafo.Inlines.Add(new LineBreak());
        _parrafo.Inlines.Add(new Run(evento.Mensaje) { Foreground = new SolidColorBrush(color) });
        if (evento.Mensaje.StartsWith(">", StringComparison.Ordinal)) _parrafo.Inlines.Add(new LineBreak());
    }

    private void Estado_Cambiado(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConsolaNativaModelo.EntradaProtegida)) AplicarEntradaProtegida(_modelo?.EntradaProtegida == true);
        if (e.PropertyName == nameof(ConsolaNativaModelo.Activa) && _modelo?.Activa == true) Entrada.Focus();
        if (e.PropertyName == nameof(ConsolaNativaModelo.Entrada) && EntradaOculta.Password != _modelo?.Entrada)
            EntradaOculta.Password = _modelo?.Entrada ?? "";
    }

    private void Ocultar_Click(object sender, RoutedEventArgs e)
    {
        if (_modelo is not null) _modelo.EntradaProtegida = !_modelo.EntradaProtegida;
    }

    private void AplicarEntradaProtegida(bool protegida)
    {
        var reenfocar = IsKeyboardFocusWithin;
        _entradaProtegida = protegida;
        EntradaOculta.Password = _modelo?.Entrada ?? "";
        Entrada.Visibility = _entradaProtegida ? Visibility.Collapsed : Visibility.Visible;
        EntradaOculta.Visibility = _entradaProtegida ? Visibility.Visible : Visibility.Collapsed;
        if (reenfocar) { if (_entradaProtegida) EntradaOculta.Focus(); else Entrada.Focus(); }
    }

    private void EntradaOculta_Cambiada(object sender, RoutedEventArgs e)
    {
        // La respuesta protegida solo permanece en memoria hasta enviarla o finalizar.
        if (_modelo is not null && _entradaProtegida) _modelo.Entrada = EntradaOculta.Password;
    }

    private void Entrada_Tecla(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Enviar?.Invoke(this, new RoutedEventArgs());
    }

    private void Enviar_Click(object sender, RoutedEventArgs e) => Enviar?.Invoke(this, e);
    private void Cerrar_Click(object sender, RoutedEventArgs e) => Cerrar?.Invoke(this, e);
}
