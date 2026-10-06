using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace Joss_Programming_Language_installer.Services
{
    public class InstallationService
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
            uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        private const int HWND_BROADCAST = 0xffff;
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        private readonly HttpClient _httpClient;
        private const string RepoOwner = "joss-language";
        private const string RepoName = "Joss-Programming-Language";

        public InstallationService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Joss-Installer", "1.0"));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        }

        public async Task<string> GetInstalledVersionAsync(string targetDir)
        {
            string exe = Path.Combine(targetDir, "joss.exe");
            if (!File.Exists(exe)) return string.Empty;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "version",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return string.Empty;
                string output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                return output.Trim();
            }
            catch
            {
                return "Instalado";
            }
        }

        public async Task ExecuteInstallOrUpdateAsync(
            string targetDir,
            bool addToPath,
            bool installVsCode,
            bool onlyIfNewer,
            IProgress<InstallProgressReport> progress)
        {
            progress.Report(new InstallProgressReport
            {
                StatusMessage = "Consultando versión...",
                DetailMessage = "Buscando última versión en GitHub...",
                IsIndeterminate = true
            });

            string apiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            string releaseJson = await _httpClient.GetStringAsync(apiUrl);

            using var doc = JsonDocument.Parse(releaseJson);
            var root = doc.RootElement;
            string latestTag = root.GetProperty("tag_name").GetString() ?? "";
            var assets = root.GetProperty("assets").EnumerateArray().ToList();

            if (onlyIfNewer)
            {
                string currentVer = await GetInstalledVersionAsync(targetDir);
                if (!string.IsNullOrEmpty(currentVer) && currentVer.Contains(latestTag.TrimStart('v'), StringComparison.OrdinalIgnoreCase))
                {
                    progress.Report(new InstallProgressReport
                    {
                        Percentage = 100,
                        IsIndeterminate = false,
                        StatusMessage = "Ya tienes la última versión",
                        DetailMessage = $"La versión local coincide con {latestTag}. No requiere actualización."
                    });
                    return;
                }
            }

            string? windowsAssetUrl = assets
                .FirstOrDefault(a => a.GetProperty("name").GetString() == "jossecurity-windows.zip")
                .GetProperty("browser_download_url").GetString();

            if (string.IsNullOrEmpty(windowsAssetUrl))
                throw new Exception("No se encontró 'jossecurity-windows.zip' en la release.");

            Directory.CreateDirectory(targetDir);
            string tempZip = Path.Combine(Path.GetTempPath(), "jossecurity-windows.zip");

            progress.Report(new InstallProgressReport
            {
                StatusMessage = $"Descargando Joss {latestTag}...",
                DetailMessage = "Descargando jossecurity-windows.zip...",
                IsIndeterminate = true
            });

            await DownloadFileAsync(windowsAssetUrl, tempZip);

            progress.Report(new InstallProgressReport
            {
                StatusMessage = "Instalando archivos...",
                DetailMessage = $"Extrayendo componentes en {targetDir}...",
                IsIndeterminate = true
            });

            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(tempZip);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string destPath = Path.Combine(targetDir, entry.Name);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            });

            if (File.Exists(tempZip)) File.Delete(tempZip);

            if (addToPath)
            {
                progress.Report(new InstallProgressReport
                {
                    StatusMessage = "Configurando PATH...",
                    DetailMessage = "Registrando ruta de Joss en variables de entorno...",
                    IsIndeterminate = true
                });
                await Task.Run(() => ConfigureUserPath(targetDir, remove: false));
            }

            if (installVsCode)
            {
                string? vscodeAssetUrl = assets
                    .FirstOrDefault(a => a.GetProperty("name").GetString() == "jossecurity-vscode.zip")
                    .GetProperty("browser_download_url").GetString();

                if (!string.IsNullOrEmpty(vscodeAssetUrl))
                {
                    progress.Report(new InstallProgressReport
                    {
                        StatusMessage = "Configurando VS Code...",
                        DetailMessage = "Instalando extensión oficial...",
                        IsIndeterminate = true
                    });
                    await InstallVsCodeExtensionAsync(vscodeAssetUrl);
                }
            }

            progress.Report(new InstallProgressReport
            {
                Percentage = 100,
                IsIndeterminate = false,
                StatusMessage = "¡Operación completada!",
                DetailMessage = $"Joss {latestTag} ha sido configurado correctamente."
            });
        }

        public async Task ExecuteUninstallAsync(
            string targetDir,
            bool removeFromPath,
            bool removeVsCode,
            IProgress<InstallProgressReport> progress)
        {
            progress.Report(new InstallProgressReport
            {
                StatusMessage = "Desinstalando Joss...",
                DetailMessage = "Eliminando directorio y binarios...",
                IsIndeterminate = true
            });

            await Task.Run(() =>
            {
                if (Directory.Exists(targetDir))
                {
                    Directory.Delete(targetDir, recursive: true);
                }
            });

            if (removeFromPath)
            {
                progress.Report(new InstallProgressReport
                {
                    StatusMessage = "Limpiando variables de entorno...",
                    DetailMessage = "Removiendo Joss del PATH de usuario...",
                    IsIndeterminate = true
                });
                await Task.Run(() => ConfigureUserPath(targetDir, remove: true));
            }

            if (removeVsCode)
            {
                progress.Report(new InstallProgressReport
                {
                    StatusMessage = "Editor...",
                    DetailMessage = "Desinstalando extensión de Visual Studio Code...",
                    IsIndeterminate = true
                });
                await Task.Run(() => UninstallVsCodeExtension());
            }

            progress.Report(new InstallProgressReport
            {
                Percentage = 100,
                IsIndeterminate = false,
                StatusMessage = "Desinstalación completa",
                DetailMessage = "Joss ha sido eliminado de tu sistema."
            });
        }

        private async Task DownloadFileAsync(string url, string destinationPath)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream);
        }

        private static void ConfigureUserPath(string pathTarget, bool remove)
        {
            using var envKey = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            if (envKey == null) return;

            string currentPath = (envKey.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string) ?? "";
            var parts = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

            if (remove)
            {
                parts.RemoveAll(p => p.Trim().Equals(pathTarget.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (!parts.Any(p => p.Trim().Equals(pathTarget.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    parts.Add(pathTarget);
                }
            }

            string updatedPath = string.Join(';', parts);
            envKey.SetValue("Path", updatedPath, RegistryValueKind.ExpandString);

            SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 2000, out _);
        }

        private async Task InstallVsCodeExtensionAsync(string vsixZipUrl, IProgress<InstallProgressReport>? progress = null)
        {
            string tempVsixZip = Path.Combine(Path.GetTempPath(), "jossecurity-vscode.zip");
            await DownloadFileAsync(vsixZipUrl, tempVsixZip);

            string vsixExtractedPath = Path.Combine(Path.GetTempPath(), "joss-vscode-extracted");
            if (Directory.Exists(vsixExtractedPath)) Directory.Delete(vsixExtractedPath, true);
            Directory.CreateDirectory(vsixExtractedPath);

            ZipFile.ExtractToDirectory(tempVsixZip, vsixExtractedPath);
            var vsixFiles = Directory.GetFiles(vsixExtractedPath, "*.vsix", SearchOption.AllDirectories);

            if (vsixFiles.Length > 0)
            {
                string targetVsix = vsixFiles[0];
                await Task.Run(() =>
                {
                    bool success = RunVsCodeCli($"--install-extension \"{targetVsix}\" --force", out string output);
                    if (!success)
                    {
                        throw new Exception($"No se pudo instalar la extensión de VS Code: {output}");
                    }
                });
            }
            else
            {
                throw new Exception("El archivo jossecurity-vscode.zip no contiene ningún archivo de extensión .vsix.");
            }

            if (File.Exists(tempVsixZip)) File.Delete(tempVsixZip);
            if (Directory.Exists(vsixExtractedPath)) Directory.Delete(vsixExtractedPath, true);
        }

        private static void UninstallVsCodeExtension()
        {
            // El ID del paquete según el archivo vsix es jossecurity.joss-language
            RunVsCodeCli("--uninstall-extension jossecurity.joss-language", out _);
            // Compatibilidad hacia atrás si se usó joss-language.joss-language
            RunVsCodeCli("--uninstall-extension joss-language.joss-language", out _);
        }

        private static string? ResolveVsCodeCliPath()
        {
            // 1. Revisar ubicaciones típicas de instalación en Windows
            string[] possiblePaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "bin", "code.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "bin", "code.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "bin", "code.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code Insiders", "bin", "code-insiders.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code Insiders", "bin", "code-insiders.cmd"),
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path)) return path;
            }

            // 2. Revisar si está disponible en el PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "code.cmd");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            return null;
        }

        private static bool RunVsCodeCli(string arguments, out string output)
        {
            output = string.Empty;
            string? codePath = ResolveVsCodeCliPath();

            ProcessStartInfo psi;
            if (!string.IsNullOrEmpty(codePath))
            {
                // En Windows, los archivos .cmd requieren cmd.exe o UseShellExecute si se llaman sin comspec directo
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{codePath}\" {arguments}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                // Intento fallback si code.cmd está directamente en PATH
                psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"code.cmd {arguments}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            try
            {
                using var p = Process.Start(psi);
                if (p == null)
                {
                    output = "No se pudo iniciar el proceso de VS Code.";
                    return false;
                }

                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit(45000);

                output = $"{stdout} {stderr}".Trim();
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }
    }
}