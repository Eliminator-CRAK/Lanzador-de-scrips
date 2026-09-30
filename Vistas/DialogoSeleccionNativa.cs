// (Autor: Alex Roman)
// Descripcion: Selecciona carpetas o scripts mediante controles WPF sin perder entradas anteriores.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LanzadorScripts.Vistas;

public sealed class DialogoSeleccionNativa : Window
{
    private readonly List<CheckBox> _opciones = [];
    public IReadOnlyList<string> Seleccionados => _opciones.Where(o => o.IsChecked == true).Select(o => (string)o.Tag).ToArray();

    public DialogoSeleccionNativa(string titulo, IReadOnlyList<string> disponibles, IReadOnlyList<string> seleccionados)
    {
        Title = titulo; Width = 560; Height = 540; MinWidth = 380; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("SurfaceBrush");
        Foreground = (Brush)FindResource("TextBrush");
        var raiz = new DockPanel { Margin = new Thickness(20) };
        var acciones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancelar = new Button { Content = "Cancelar", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var aceptar = new Button { Content = "Aceptar", IsDefault = true };
        aceptar.Click += (_, _) => DialogResult = true;
        acciones.Children.Add(cancelar); acciones.Children.Add(aceptar);
        DockPanel.SetDock(acciones, Dock.Bottom); raiz.Children.Add(acciones);
        var buscador = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        buscador.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Buscar opciones");
        DockPanel.SetDock(buscador, Dock.Top); raiz.Children.Add(buscador);
        var lista = new StackPanel();
        foreach (var id in disponibles.Concat(seleccionados).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            var opcion = new CheckBox { Content = new TextBlock { Text = id, TextWrapping = TextWrapping.Wrap }, Tag = id,
                IsChecked = seleccionados.Contains(id, StringComparer.OrdinalIgnoreCase), Foreground = Foreground, Margin = new Thickness(0, 6, 0, 6) };
            _opciones.Add(opcion); lista.Children.Add(opcion);
        }
        buscador.TextChanged += (_, _) => { foreach (var opcion in _opciones) opcion.Visibility = ((string)opcion.Tag).Contains(buscador.Text, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed; };
        raiz.Children.Add(new ScrollViewer { Content = lista, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = raiz;
    }
}
