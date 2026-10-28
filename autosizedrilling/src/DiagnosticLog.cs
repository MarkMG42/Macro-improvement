using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace AutoSizeDrilling
{
    // Local-only diagnostics: no document paths and no automatic network upload.
    internal sealed class DiagnosticLog : IDisposable
    {
        private StreamWriter writer;
        public string FilePath { get; private set; }
        public DiagnosticLog()
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AutoSizeDrilling", "Logs");
                Directory.CreateDirectory(directory);
                FilePath = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N") + ".log");
                writer = new StreamWriter(FilePath, false) { AutoFlush = true };
                Write("AutoSizeDrilling " + typeof(SolidWorksMacro).Assembly.GetName().Version +
                    "; coordinates in sheet metres; measured values in model millimetres");
            }
            catch (Exception ex) { Disable(ex); }
        }
        public void Write(string text)
        {
            Debug.WriteLine(text);
            try { writer?.WriteLine(text); }
            catch (Exception ex) { Disable(ex); }
        }
        public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        public static string Point(double[] point) => string.Join(", ", Array.ConvertAll(point, Number));
        private void Disable(Exception ex)
        {
            Debug.WriteLine("Diagnostic log unavailable: " + ex.Message);
            Dispose();
            FilePath = null;
        }
        public void Dispose()
        {
            try { writer?.Dispose(); } catch (Exception ex) { Debug.WriteLine(ex.Message); }
            writer = null;
        }
    }
}
