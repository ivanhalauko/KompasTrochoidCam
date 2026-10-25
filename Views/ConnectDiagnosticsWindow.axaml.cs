using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace KompasTrochoidCam.Views
{
    /// <summary>Окно с подробным отчётом о попытке подключения к КОМПАС-3D.</summary>
    public partial class ConnectDiagnosticsWindow : Window
    {
        public ConnectDiagnosticsWindow()
        {
            InitializeComponent();
        }

        public ConnectDiagnosticsWindow(string report) : this()
        {
            var box = this.FindControl<TextBox>("ReportBox");
            if (box != null) box.Text = report;
        }

        private async void CopyButton_Click(object? sender, RoutedEventArgs e)
        {
            // В Avalonia 12 SetTextAsync — метод расширения ClipboardExtensions
            // из пространства имён Avalonia.Input.Platform, а не член IClipboard.
            var clipboard = Clipboard;
            if (clipboard == null) return;

            string report = this.FindControl<TextBox>("ReportBox")?.Text ?? string.Empty;

            try
            {
                await clipboard.SetTextAsync(report);
                if (sender is Button button) button.Content = "Скопировано";
            }
            catch
            {
                // Буфер обмена может быть занят другим приложением — не роняем окно.
            }
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
    }
}
