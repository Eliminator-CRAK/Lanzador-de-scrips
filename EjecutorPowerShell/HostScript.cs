// (Autor: Alex Roman)
// Descripcion: Conserva salida, preguntas, credenciales y progreso de Windows PowerShell.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;

namespace LanzadorScripts.EjecutorPowerShell
{
    internal sealed class HostScript : PSHost
    {
        private readonly Guid _id = Guid.NewGuid();
        private readonly InterfazScript _ui;
        internal HostScript(CanalProgreso canal) { _ui = new InterfazScript(canal); }
        internal bool SolicitoSalida { get; private set; }
        internal int CodigoSalida { get; private set; }
        public override Guid InstanceId { get { return _id; } }
        public override string Name { get { return "LanzadorScripts"; } }
        public override Version Version { get { return new Version(5, 1); } }
        public override CultureInfo CurrentCulture { get { return CultureInfo.CurrentCulture; } }
        public override CultureInfo CurrentUICulture { get { return CultureInfo.CurrentUICulture; } }
        public override PSHostUserInterface UI { get { return _ui; } }
        public override void SetShouldExit(int exitCode) { CodigoSalida = exitCode; SolicitoSalida = true; }
        public override void EnterNestedPrompt() { throw new NotSupportedException("No se admiten sesiones anidadas."); }
        public override void ExitNestedPrompt() { }
        public override void NotifyBeginApplication() { }
        public override void NotifyEndApplication() { }
    }

    internal sealed class InterfazScript : PSHostUserInterface
    {
        private readonly CanalProgreso _canal;
        private readonly InterfazBruta _raw = new InterfazBruta();
        internal InterfazScript(CanalProgreso canal) { _canal = canal; }
        public override PSHostRawUserInterface RawUI { get { return _raw; } }
        public override void Write(string value) { Console.Write(value); }
        public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) { Write(value); }
        public override void WriteLine(string value) { Console.WriteLine(value); }
        public override void WriteErrorLine(string value) { Console.Error.WriteLine(value); }
        public override void WriteDebugLine(string message) { Console.WriteLine(message); }
        public override void WriteVerboseLine(string message) { Console.WriteLine(message); }
        public override void WriteWarningLine(string message) { Console.WriteLine(message); }
        public override void WriteProgress(long sourceId, ProgressRecord record) { _canal.Progreso(sourceId, record); }
        public override string ReadLine() { _canal.Entrada(false); return Console.ReadLine() ?? ""; }
        public override SecureString ReadLineAsSecureString()
        {
            _canal.Entrada(true);
            var segura = new SecureString();
            var valor = Console.ReadLine() ?? "";
            foreach (var caracter in valor) segura.AppendChar(caracter);
            segura.MakeReadOnly();
            return segura;
        }

        public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions)
        {
            if (!string.IsNullOrEmpty(caption)) WriteLine(caption);
            if (!string.IsNullOrEmpty(message)) WriteLine(message);
            var resultado = new Dictionary<string, PSObject>();
            foreach (var campo in descriptions)
            {
                Write(campo.Name + ": ");
                // PowerShell entrega el nombre de tipo ya cualificado en este campo.
                var tipo = Type.GetType(campo.ParameterAssemblyFullName, false)
                    ?? Type.GetType(campo.ParameterTypeFullName, false) ?? typeof(string);
                object valor;
                if (tipo == typeof(SecureString)) valor = ReadLineAsSecureString();
                else if (tipo == typeof(PSCredential)) valor = PromptForCredential("", "", "", "");
                else valor = LanguagePrimitives.ConvertTo(ReadLine(), tipo, CultureInfo.CurrentCulture);
                resultado[campo.Name] = PSObject.AsPSObject(valor);
            }
            return resultado;
        }

        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName)
        { return PromptForCredential(caption, message, userName, targetName, PSCredentialTypes.Default, PSCredentialUIOptions.Default); }
        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
        {
            if (!string.IsNullOrEmpty(caption)) WriteLine(caption);
            if (!string.IsNullOrEmpty(message)) WriteLine(message);
            if (string.IsNullOrEmpty(userName)) { Write("Usuario: "); userName = ReadLine(); }
            Write("Password: ");
            return new PSCredential(userName, ReadLineAsSecureString());
        }

        public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
        {
            WriteLine(caption); WriteLine(message);
            for (var i = 0; i < choices.Count; i++) WriteLine(i + ": " + choices[i].Label.Replace("&", ""));
            while (true)
            {
                Write("Opcion: ");
                var valor = ReadLine();
                if (string.IsNullOrWhiteSpace(valor) && defaultChoice >= 0) return defaultChoice;
                int numero;
                if (int.TryParse(valor, out numero) && numero >= 0 && numero < choices.Count) return numero;
                for (var i = 0; i < choices.Count; i++)
                {
                    var marca = choices[i].Label.IndexOf('&');
                    if (marca >= 0 && marca + 1 < choices[i].Label.Length && string.Equals(valor, choices[i].Label.Substring(marca + 1, 1), StringComparison.OrdinalIgnoreCase)) return i;
                }
            }
        }
    }

    internal sealed class InterfazBruta : PSHostRawUserInterface
    {
        public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;
        public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;
        public override Size BufferSize { get; set; } = new Size(160, 3000);
        public override Coordinates CursorPosition { get; set; }
        public override int CursorSize { get; set; } = 25;
        public override bool KeyAvailable { get { return false; } }
        public override Size MaxPhysicalWindowSize { get { return new Size(160, 80); } }
        public override Size MaxWindowSize { get { return MaxPhysicalWindowSize; } }
        public override Coordinates WindowPosition { get; set; }
        public override Size WindowSize { get; set; } = new Size(160, 40);
        public override string WindowTitle { get; set; } = "LanzadorScripts";
        public override void FlushInputBuffer() { }
        public override BufferCell[,] GetBufferContents(Rectangle rectangle) { throw new NotSupportedException(); }
        public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) { }
        public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
        public override void SetBufferContents(Rectangle rectangle, BufferCell fill) { }
        public override KeyInfo ReadKey(ReadKeyOptions options)
        {
            // Pause recibe Enter por la entrada redirigida sin necesitar una consola de Windows.
            Console.ReadLine();
            return new KeyInfo(13, '\r', 0, true);
        }
    }
}
