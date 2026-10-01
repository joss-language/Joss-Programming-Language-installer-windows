namespace Joss_Programming_Language_installer.Services
{
    public class InstallProgressReport
    {
        public double Percentage { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public string DetailMessage { get; set; } = string.Empty;
        public bool IsIndeterminate { get; set; } = true;
    }
}