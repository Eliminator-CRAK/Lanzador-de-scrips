// (Autor: Alex Roman)
// Descripcion: Valida la entrada enmascarada de la clave compartida de artefactos.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;

namespace LanzadorScripts;

public partial class DialogoClaveArtefactos : Window
{
    private byte[]? _clave;

    public DialogoClaveArtefactos()
    {
        InitializeComponent();
        Loaded += (_, _) => EntradaClave.Focus();
    }

    public byte[] TomarClave()
    {
        // Transfiere la clave al proceso de aprovisionamiento.
        var clave = _clave
            ?? throw new InvalidOperationException("No se ha introducido una clave valida.");
        _clave = null;
        return clave;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_clave is not null)
        {
            CryptographicOperations.ZeroMemory(_clave);
            _clave = null;
        }

        EntradaClave.Clear();
        base.OnClosed(e);
    }

    private void Instalar_Click(object sender, RoutedEventArgs e)
    {
        IntentarAceptar();
    }

    private void Cancelar_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void EntradaClave_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        IntentarAceptar();
    }

    private void IntentarAceptar()
    {
        TextoError.Text = string.Empty;
        if (EntradaClave.SecurePassword.Length == 0)
        {
            TextoError.Text = "Introduce la clave compartida.";
            return;
        }

        var puntero = IntPtr.Zero;
        string? texto = null;
        byte[]? clave = null;
        try
        {
            using var entradaSegura = EntradaClave.SecurePassword;
            puntero = Marshal.SecureStringToBSTR(entradaSegura);
            texto = Marshal.PtrToStringBSTR(puntero);
            if (string.IsNullOrWhiteSpace(texto))
            {
                TextoError.Text = "Introduce la clave compartida.";
                return;
            }

            clave = Convert.FromBase64String(texto.Trim());
            if (clave.Length != 32)
            {
                TextoError.Text = "La clave debe contener exactamente 32 bytes.";
                return;
            }

            _clave = clave;
            clave = null;
            EntradaClave.Clear();
            DialogResult = true;
        }
        catch (FormatException)
        {
            TextoError.Text = "La clave no contiene Base64 válido.";
        }
        finally
        {
            texto = null;
            if (clave is not null)
            {
                CryptographicOperations.ZeroMemory(clave);
            }

            if (puntero != IntPtr.Zero)
            {
                Marshal.ZeroFreeBSTR(puntero);
            }
        }
    }
}
