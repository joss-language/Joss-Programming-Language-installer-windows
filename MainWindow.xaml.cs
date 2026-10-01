using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using System.IO;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Joss_Programming_Language_installer.Services;

namespace Joss_Programming_Language_installer
{
    public sealed partial class MainWindow : Window
    {
        private readonly InstallationService _installService = new();
        private bool _isBusy = false;

        public MainWindow()
        {
            this.InitializeComponent();

            this.AppWindow.Resize(new SizeInt32(660, 580));
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);

            string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Joss");
            TxtInstallPath.Text = defaultPath;

            TrySetMicaBackdrop();

            // Cargar el estado inicial de instalación en segundo plano
            _ = CheckSystemStatusAsync();
        }

        private void TrySetMicaBackdrop()
        {
            if (MicaController.IsSupported())
            {
                this.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
            }
        }

        private async System.Threading.Tasks.Task CheckSystemStatusAsync()
        {
            string targetDir = TxtInstallPath.Text.Trim();
            string currentVer = await _installService.GetInstalledVersionAsync(targetDir);

            if (!string.IsNullOrEmpty(currentVer))
            {
                TxtCurrentInstalledTitle.Text = "Joss detectado en el equipo";
                TxtCurrentInstalledVer.Text = $"Versión instalada: {currentVer} | Ubicación: {targetDir}";
                IconEnvStatus.Glyph = "\uE73E"; // Check
                IconEnvStatus.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
            }
            else
            {
                TxtCurrentInstalledTitle.Text = "Joss no está instalado";
                TxtCurrentInstalledVer.Text = "El compilador no se encuentra en la ruta seleccionada.";
                IconEnvStatus.Glyph = "\uE783"; // Warning / Info
                IconEnvStatus.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            }
        }

        private async void BtnRefreshStatus_Click(object sender, RoutedEventArgs e)
        {
            await CheckSystemStatusAsync();
        }

        private void CmbAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TxtHeaderTitle == null || BtnAction == null || CardPath == null) return;
            if (CmbAction.SelectedItem is not ComboBoxItem item) return;

            string action = item.Tag?.ToString() ?? "Install";

            switch (action)
            {
                case "Install":
                    TxtHeaderTitle.Text = "Instalar Joss SDK";
                    TxtHeaderDesc.Text = "Instala el compilador y añade las herramientas a tu sistema.";
                    BtnAction.Content = "Instalar";
                    BtnAction.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                    CardPath.Visibility = Visibility.Visible;
                    TxtPathSub.Text = "Permite ejecutar 'joss' directamente desde cualquier terminal.";
                    TxtVsCodeTitle.Text = "Instalar extensión para Visual Studio Code";
                    break;

                case "Update":
                    TxtHeaderTitle.Text = "Actualizar Joss SDK";
                    TxtHeaderDesc.Text = "Comprueba si existe una versión más reciente en GitHub y la aplica.";
                    BtnAction.Content = "Actualizar";
                    BtnAction.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                    CardPath.Visibility = Visibility.Visible;
                    TxtPathSub.Text = "Mantiene o actualiza la entrada en el PATH.";
                    TxtVsCodeTitle.Text = "Actualizar extensión de VS Code si hay cambios";
                    break;

                case "Reinstall":
                    TxtHeaderTitle.Text = "Reinstalar Joss SDK";
                    TxtHeaderDesc.Text = "Vuelve a descargar y sobrescribir los binarios y configuración.";
                    BtnAction.Content = "Reinstalar";
                    BtnAction.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                    CardPath.Visibility = Visibility.Visible;
                    TxtPathSub.Text = "Reescribe la entrada en el PATH.";
                    TxtVsCodeTitle.Text = "Reinstalar extensión para Visual Studio Code";
                    break;

                case "Uninstall":
                    TxtHeaderTitle.Text = "Desinstalar Joss SDK";
                    TxtHeaderDesc.Text = "Elimina los archivos locales y limpia las configuraciones registradas.";
                    BtnAction.Content = "Desinstalar";
                    BtnAction.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
                    CardPath.Visibility = Visibility.Collapsed;
                    TxtPathSub.Text = "Remueve la ruta de Joss del PATH de usuario.";
                    TxtVsCodeTitle.Text = "Desinstalar extensión de Visual Studio Code";
                    break;
            }
        }

        private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var folderPicker = new FolderPicker();
            folderPicker.FileTypeFilter.Add("*");

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(folderPicker, hwnd);

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                TxtInstallPath.Text = Path.Combine(folder.Path, "Joss");
                await CheckSystemStatusAsync();
            }
        }

        private async void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            // Iniciar fase de progreso
            _isBusy = true;
            OptionsPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            QuickActionsPanel.Visibility = Visibility.Collapsed;
            BtnBackToMenu.Visibility = Visibility.Collapsed;
            BtnAction.IsEnabled = false;
            BtnCancel.IsEnabled = false;
            TxtConsoleLog.Text = "";

            string action = ((ComboBoxItem)CmbAction.SelectedItem).Tag?.ToString() ?? "Install";
            string targetDir = TxtInstallPath.Text.Trim();
            bool addToPath = ChkAddToPath.IsChecked ?? true;
            bool handleVsCode = ChkInstallVSCode.IsChecked ?? true;

            var progress = new Progress<InstallProgressReport>(report =>
            {
                TxtStatus.Text = report.StatusMessage;
                TxtDetail.Text = report.DetailMessage;
                ProgressBarInstall.IsIndeterminate = report.IsIndeterminate;

                if (!report.IsIndeterminate)
                {
                    ProgressBarInstall.Value = report.Percentage;
                }

                TxtConsoleLog.Text += $"[{DateTime.Now:HH:mm:ss}] {report.DetailMessage}\n";
                LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
            });

            bool success = false;
            try
            {
                if (action == "Uninstall")
                {
                    await _installService.ExecuteUninstallAsync(targetDir, addToPath, handleVsCode, progress);
                }
                else
                {
                    bool onlyIfNewer = (action == "Update");
                    await _installService.ExecuteInstallOrUpdateAsync(targetDir, addToPath, handleVsCode, onlyIfNewer, progress);
                }

                IconStatus.Glyph = "\uE73E";
                IconStatus.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                success = true;
            }
            catch (Exception ex)
            {
                TxtConsoleLog.Text += $"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}\n";
                IconStatus.Glyph = "\uE711";
                IconStatus.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                TxtStatus.Text = "Error en la operación";
                TxtDetail.Text = "Ocurrió un problema durante el proceso.";
                ProgressBarInstall.IsIndeterminate = false;
                ProgressBarInstall.Value = 0;
            }
            finally
            {
                _isBusy = false;
                BtnCancel.IsEnabled = true;
                BtnBackToMenu.Visibility = Visibility.Visible; // Permitir regresar al menú
                BtnAction.Visibility = Visibility.Collapsed;

                if (success && action != "Uninstall")
                {
                    QuickActionsPanel.Visibility = Visibility.Visible;
                }

                // Actualizar tarjeta de estado tras la operación
                _ = CheckSystemStatusAsync();
            }
        }

        // Volver a la pantalla principal sin cerrar la aplicación
        private void BtnBackToMenu_Click(object sender, RoutedEventArgs e)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            OptionsPanel.Visibility = Visibility.Visible;

            BtnBackToMenu.Visibility = Visibility.Collapsed;
            BtnAction.Visibility = Visibility.Visible;
            BtnAction.IsEnabled = true;
        }

        // Acciones rápidas tras instalar
        private void BtnOpenTerminal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Inicia wt.exe (Windows Terminal) o powershell.exe en el directorio de usuario
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoExit -Command Write-Host 'Bienvenido a Joss' -ForegroundColor Cyan; joss version",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string targetDir = TxtInstallPath.Text.Trim();
            if (Directory.Exists(targetDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = targetDir,
                    UseShellExecute = true
                });
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => this.Close();
    }
}