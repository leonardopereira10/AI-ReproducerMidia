using System.Diagnostics;

namespace CATRA.Services.Casting;

/// <summary>
/// Best-effort Windows Firewall inbound rule for the embedded HTTP server
/// (ST-08). <c>netsh</c> requires elevation: without admin rights the call
/// fails silently and the caller should surface the manual instruction
/// ("allow CATRA through the firewall"). Runs at most once per machine
/// (marker file under <c>%AppData%/CATRA</c>). Never throws.
/// </summary>
public static class FirewallHelper
{
    /// <summary>Firewall rule name.</summary>
    public const string RuleName = "CATRA DLNA Media Server";

    /// <summary>
    /// Tries to add the inbound TCP rule once. Returns a human-readable
    /// outcome (success, already-done, or the manual instruction).
    /// </summary>
    public static string TryEnsureFirewallRule()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                return ManualInstruction;
            }

            var markerDir = Path.Combine(appData, "CATRA");
            Directory.CreateDirectory(markerDir);
            var marker = Path.Combine(markerDir, "firewall-rule-attempted");
            if (File.Exists(marker))
            {
                return "Regra de firewall já solicitada anteriormente.";
            }

            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments =
                    $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP program=\"{Environment.ProcessPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return ManualInstruction;
            }

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Best effort.
                }

                return ManualInstruction;
            }

            return process.ExitCode == 0
                ? "Exceção de firewall adicionada."
                : ManualInstruction;
        }
        catch
        {
            return ManualInstruction;
        }
    }

    /// <summary>Manual instruction shown when elevation is unavailable.</summary>
    public const string ManualInstruction =
        "Sem privilégios de administrador: permita manualmente o CATRA no Firewall do Windows " +
        "(rede privada) para habilitar a transmissão DLNA.";
}
