using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RobloxImageFix;

public partial class MainWindow : Window
{
    private Button? _currentNav;
    private RadioButton? _selectedDnsRadio;
    private bool _isFixing;
    private Storyboard? _startupAnim;
    private Dictionary<string, List<string>> _dnsBackup = new();
    private readonly ObservableCollection<string> _operationLog = new();
    private readonly List<int> _pingHistory = new();
    private DispatcherTimer? _dnsMonitorTimer;
    private string _lastSetPrimary = "";
    private string _lastSetSecondary = "";
    private static readonly string[] AllDnsIps = ["95.182.120.241", "45.155.204.190", "37.230.192.51", "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112", "208.67.222.222", "208.67.220.220"];

    private static readonly Dictionary<string, (string primary, string secondary, string doh)> DnsProfiles = new()
    {
        ["dnsGeohide"] = ("95.182.120.241", "45.155.204.190", "https://dns.geohide.ru:8443/"),
        ["dnsGeohideAlt"] = ("95.182.120.241", "45.155.204.190", "https://dns.geohide.ru:444/dns-query"),
        ["dnsGeohide1"] = ("95.182.120.241", "37.230.192.51", "https://dns.geohide.ru:8443/"),
        ["dnsCloudflare"] = ("1.1.1.1", "1.0.0.1", "https://cloudflare-dns.com/dns-query"),
        ["dnsGoogle"] = ("8.8.8.8", "8.8.4.4", "https://dns.google/dns-query"),
        ["dnsQuad9"] = ("9.9.9.9", "149.112.112.112", "https://dns.quad9.net/dns-query"),
        ["dnsOpenDns"] = ("208.67.222.222", "208.67.220.220", ""),
        ["dnsCustom"] = ("", "", ""),
    };

    private (string primary, string secondary, string doh) SelectedDnsProfile
    {
        get
        {
            if (_selectedDnsRadio == null) return DnsProfiles["dnsGeohide"];
            if (_selectedDnsRadio.Name == "dnsCustom")
            {
                var ip = customDnsInput.Text.Trim();
                if (string.IsNullOrWhiteSpace(ip)) ip = "95.182.120.241";
                return (ip, "", "");
            }
            return DnsProfiles.GetValueOrDefault(_selectedDnsRadio.Name, DnsProfiles["dnsGeohide"]);
        }
    }

    private static readonly string[] HostsEntries =
    [
        "95.182.120.241 roblox.com",
        "95.182.120.241 www.roblox.com",
        "95.182.120.241 api.roblox.com",
        "95.182.120.241 images.rbxcdn.com",
        "95.182.120.241 t6.rbxcdn.com",
        "95.182.120.241 t7.rbxcdn.com",
        "95.182.120.241 t8.rbxcdn.com",
        "95.182.120.241 cdn.arkoselabs.com",
    ];

    private static readonly string[] TrRbxcdnFallback =
    [
        "3.171.117.54 tr.rbxcdn.com",
        "3.171.117.73 tr.rbxcdn.com",
        "3.164.195.121 tr.rbxcdn.com",
        "18.65.39.105 tr.rbxcdn.com",
        "2.20.245.170 tr.rbxcdn.com",
    ];

    private static async Task<(List<string> entries, bool usedFallback)> GetFreshTrRbxcdnEntriesAsync()
    {
        var ips = new List<string>();

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var json = await client.GetStringAsync("https://dns.google/resolve?name=tr.rbxcdn.com&type=A");
            var root = JsonNode.Parse(json);
            if (root?["Answer"] is JsonArray answers)
            {
                foreach (var answer in answers)
                {
                    if (answer?["type"]?.GetValue<int>() == 1 && answer["data"] is JsonValue data)
                    {
                        var ip = data.GetValue<string>();
                        if (!ips.Contains(ip)) ips.Add(ip);
                    }
                }
            }
        }
        catch { }

        if (ips.Count == 0)
        {
            try
            {
                foreach (var ip in await Dns.GetHostAddressesAsync("tr.rbxcdn.com"))
                {
                    var s = ip.ToString();
                    if (!ips.Contains(s)) ips.Add(s);
                }
            }
            catch { }
        }

        if (ips.Count > 0)
            return (ips.Select(ip => $"{ip} tr.rbxcdn.com").ToList(), false);

        return (TrRbxcdnFallback.ToList(), true);
    }

    public MainWindow()
    {
        InitializeComponent();
        CheckAdmin();
        if (RunUninstallIfRequested())
        {
            Close();
            return;
        }
        _currentNav = navDashboard;
        foreach (var rb in new[] { dnsGeohide, dnsGeohideAlt, dnsGeohide1, dnsCloudflare, dnsGoogle, dnsQuad9, dnsOpenDns, dnsCustom })
        {
            if (rb.Name == "dnsGeohide") { rb.IsChecked = true; break; }
        }
        UpdateDnsDisplay("dnsGeohide");
        RefreshAdaptersDisplay();
        autorunCheck.IsChecked = IsAutorunEnabled();
        autoHostsCheck.IsChecked = IsAutoHostsEnabled();
        Loaded += OnMainWindowLoaded;
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (autoHostsCheck.IsChecked != true) return;
        try
        {
            var (added, _) = await ApplyHostsAsync();
            LogOperation(added > 0
                ? $"Auto-added {added} hosts entries on startup"
                : "Auto-hosts: all entries already present");
        }
        catch (Exception ex)
        {
            LogOperation($"Auto-hosts error: {ex.Message}");
        }
    }

    private static void CheckAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(id);
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            var result = MessageBox.Show(
                "This app needs administrator privileges to change DNS and hosts.\n\nRestart as administrator?",
                "Roblox Image Fix", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);

            if (result == MessageBoxResult.Yes)
            {
                var args = Environment.GetCommandLineArgs().Skip(1);
                var psi = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    Verb = "runas",
                    UseShellExecute = true
                };
                if (args.Any()) psi.Arguments = string.Join(" ", args);
                try { Process.Start(psi); } catch { }
            }
            Environment.Exit(0);
        }
    }

    private static bool RunUninstallIfRequested()
    {
        if (!Environment.GetCommandLineArgs().Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
            return false;
        return RunUninstallFlow();
    }

    private static bool RunUninstallFlow()
    {
        var confirm = MessageBox.Show(
            "Uninstall Roblox Image Fix?\n\nThis will remove the app files, shortcuts, autorun entry and the registry uninstall entry.\nDNS and hosts changes will not be touched.",
            "Roblox Image Fix — Uninstall",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return false;

        try
        {
            var shortcuts = new List<string>();
            foreach (var folder in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.Desktop })
            {
                var dir = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(dir)) continue;
                shortcuts.Add(Path.Combine(dir, "Roblox Image Fix.lnk"));
                shortcuts.Add(Path.Combine(Path.Combine(dir, "Programs"), "Roblox Image Fix.lnk"));
            }
            foreach (var lnk in shortcuts.Distinct())
            {
                if (File.Exists(lnk)) File.Delete(lnk);
            }

            using (var uninstallKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true))
                uninstallKey?.DeleteSubKey("RobloxImageFix", false);

            RemoveAutorunIfPresent();

            var installDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            if (Directory.Exists(installDir) && installDir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase))
            {
                var bat = Path.Combine(Path.GetTempPath(), $"rif-uninstall-{Guid.NewGuid():N}.bat");
                File.WriteAllText(bat,
                    $"@echo off\r\ntimeout /t 2 /nobreak >nul\r\nrd /s /q \"{installDir}\"\r\ndel \"%~f0\"\r\n");
                Process.Start(new ProcessStartInfo(bat) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            }

            MessageBox.Show("Roblox Image Fix has been uninstalled.",
                "Roblox Image Fix", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Uninstall error: {ex.Message}",
                "Roblox Image Fix", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static void RemoveAutorunIfPresent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("RobloxImageFix", false);
        }
        catch { }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (RunUninstallFlow())
        {
            Close();
        }
    }

    private void LogOperation(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _operationLog.Add(entry);
        if (_operationLog.Count > 200) _operationLog.RemoveAt(0);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        if (_currentNav != null)
        {
            _currentNav.Tag = null;
            _currentNav.Foreground = FindResource("TextSecondaryBrush") as Brush;
        }
        btn.Tag = "Active";
        btn.Foreground = FindResource("TextPrimaryBrush") as Brush;
        _currentNav = btn;

        dashboardView.Visibility = btn == navDashboard ? Visibility.Visible : Visibility.Collapsed;
        settingsView.Visibility = btn == navSettings ? Visibility.Visible : Visibility.Collapsed;
        aboutView.Visibility = btn == navAbout ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DnsOption_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || !rb.IsChecked == true) return;
        _selectedDnsRadio = rb;
        UpdateDnsDisplay(rb.Name);
    }

    private void UpdateDnsDisplay(string name)
    {
        if (name == "dnsCustom")
        {
            currentDnsText.Text = "Custom DNS";
            currentDnsIp.Text = customDnsInput.Text.Trim();
            return;
        }

        (string primary, string secondary, string doh) profile = DnsProfiles.GetValueOrDefault(name, ("", "", ""));
        var label = name switch
        {
            "dnsGeohide" => "Geohide",
            "dnsGeohideAlt" => "Geohide Win11",
            "dnsGeohide1" => "Geohide 1",
            "dnsCloudflare" => "Cloudflare",
            "dnsGoogle" => "Google",
            "dnsQuad9" => "Quad9",
            _ => "Unknown"
        };
        currentDnsText.Text = $"{label} DNS";
        currentDnsIp.Text = profile.primary;
    }

    private async void FixImages_Click(object sender, RoutedEventArgs e)
    {
        if (_isFixing) return;
        _isFixing = true;
        LogOperation("Fix started");

        SetStatus("Fixing", "#F59E0B", "Applying DNS changes...");

        fixButton.IsEnabled = false;
        fixButtonText.Text = "FIXING...";
        progressArea.Visibility = Visibility.Visible;
        successMessage.Visibility = Visibility.Collapsed;
        hostsMessage.Visibility = Visibility.Collapsed;
        AnimateProgressBar();

        try
        {
            var (p, s, d) = SelectedDnsProfile;
            if (string.IsNullOrWhiteSpace(p)) { p = "95.182.120.241"; s = "45.155.204.190"; d = "https://dns.geohide.ru:8443/"; }
            var errors = new StringBuilder();

            progressText.Text = "Scanning adapters...";
            await Task.Delay(100);

            var readyAdapters = GetAllAdapters().Select(a => a.name).ToList();
            if (readyAdapters.Count == 0)
                errors.AppendLine("No network adapters found.");

            BackupCurrentDns();

            var totalSteps = readyAdapters.Count + (IsWindows11() ? readyAdapters.Count : 0) + 1;
            var currentStep = 0;

            foreach (var adapter in readyAdapters)
            {
                currentStep++;
                progressText.Text = $"Setting DNS on {adapter}...";
                UpdateProgressBar((double)currentStep / totalSteps);
                await Task.Delay(50);

                var result = await SetDnsOnAdapter(adapter, p, s);
                if (!result.success)
                    errors.AppendLine($"[{adapter}] {result.error}");
            }

            if (IsWindows11())
            {
                foreach (var adapter in readyAdapters)
                {
                    currentStep++;
                    if (!string.IsNullOrWhiteSpace(d))
                    {
                        progressText.Text = $"Configuring DoH on {adapter}...";
                        UpdateProgressBar((double)currentStep / totalSteps);
                        await Task.Delay(50);

                        var result = await SetDohOnAdapter(adapter, p, d);
                        if (!result.success)
                            errors.AppendLine($"[{adapter}] DoH: {result.error}");
                    }
                }
            }

            currentStep++;
            progressText.Text = "Flushing DNS cache...";
            UpdateProgressBar((double)currentStep / totalSteps);
            await FlushDns();

            UpdateProgressBar(1.0);
            progressText.Text = "Done!";
            await Task.Delay(300);

            if (errors.Length == 0)
            {
                SetStatus("Connected", "#22C55E", "DNS configured. CDN images restored.");
                _lastSetPrimary = p;
                _lastSetSecondary = s;
                LogOperation($"DNS set to {p} / {s}");
                ShowSuccess();
            }
            else
            {
                SetStatus("Error", "#EF4444", errors.ToString().TrimEnd());
                LogOperation($"Fix failed: {errors}");
                ShowError(errors.ToString());
            }
        }
        catch (Exception ex)
        {
            SetStatus("Error", "#EF4444", ex.Message);
            ShowError(ex.Message);
        }
        finally
        {
            fixButton.IsEnabled = true;
            fixButtonText.Text = "FIX IMAGES";
            _isFixing = false;
        }
    }

    private void BackupCurrentDns()
    {
        _dnsBackup.Clear();
        foreach (var name in GetAllAdapters().Select(a => a.name))
        {
            var servers = new List<string>();
            try
            {
                var psi = new ProcessStartInfo("netsh", $"""interface ip show dns name="{name}" """)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                var output = proc.StandardOutput.ReadToEnd();
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("Сервер DNS", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("DNS Server", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = t.Split(':');
                        if (parts.Length > 1) servers.Add(parts[^1].Trim());
                    }
                }
                _dnsBackup[name] = servers;
            }
            catch { }
        }
        restoreDnsBtn.IsEnabled = _dnsBackup.Count > 0;
    }

    private async void BackupDns_Click(object sender, RoutedEventArgs e)
    {
        BackupCurrentDns();
        var count = _dnsBackup.Sum(kv => kv.Value.Count);
        backupStatus.Text = count > 0
            ? $"✔ Backed up {count} DNS entries"
            : "✖ No DNS servers found";
        backupStatus.Foreground = count > 0
            ? (Brush)FindResource("SuccessBrush")
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
        if (count > 0) LogOperation($"Backed up {count} DNS entries");
    }

    private async void RestoreDns_Click(object sender, RoutedEventArgs e)
    {
        restoreDnsBtn.IsEnabled = false;
        backupStatus.Text = "Restoring...";
        backupStatus.Foreground = (Brush)FindResource("TextPrimaryBrush");
        LogOperation("Restoring DNS to DHCP...");

        var adapters = GetAllAdapters().Select(a => a.name).ToList();
        if (adapters.Count == 0)
        {
            backupStatus.Text = "✖ No adapters found";
            backupStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            restoreDnsBtn.IsEnabled = true;
            return;
        }

        var errorLog = new StringBuilder();
        foreach (var adapter in adapters)
        {
            backupStatus.Text = $"Restoring {adapter}...";
            try
            {
                var psi = new ProcessStartInfo("netsh", $"""interface ip set dns name="{adapter}" source=dhcp""")
                {
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync();
                    if (proc.ExitCode != 0)
                    {
                        var err = await proc.StandardError.ReadToEndAsync();
                        errorLog.AppendLine($"[{adapter}] exit:{proc.ExitCode} {err.Trim()}");
                    }
                }
            }
            catch (Exception ex)
            {
                errorLog.AppendLine($"[{adapter}] {ex.Message}");
            }
        }

        await FlushDns();

        if (errorLog.Length > 0)
        {
            SetStatus("Warning", "#F59E0B", "Some adapters had errors");
            backupStatus.Text = $"⚠ {errorLog}";
            backupStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            LogOperation($"Restore had errors: {errorLog}");
        }
        else
        {
            SetStatus("Connected", "#22C55E", "All adapters restored to DHCP");
            backupStatus.Text = $"✔ DHCP restored on {adapters.Count} adapters";
            backupStatus.Foreground = (Brush)FindResource("SuccessBrush");
            LogOperation($"DHCP restored on {adapters.Count} adapters");
        }
        restoreDnsBtn.IsEnabled = true;
    }

    private static async Task SetDhcpOnAdapter(string adapter)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"""interface ip set dns name="{adapter}" source=dhcp""")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch { }
    }

    private async void FlushDns_Click(object sender, RoutedEventArgs e)
    {
        flushDnsBtn.IsEnabled = false;
        LogOperation("Flushing DNS cache...");
        await FlushDns();
        flushDnsBtn.IsEnabled = true;
        LogOperation("DNS cache flushed");
        SetStatus("Connected", "#22C55E", "DNS cache flushed");
    }

    private async void PingTest_Click(object sender, RoutedEventArgs e)
    {
        pingBtn.IsEnabled = false;
        pingResult.Text = "Pinging...";

        var targets = new[] { "roblox.com", "images.rbxcdn.com" };
        var results = new StringBuilder();

        using var ping = new Ping();
        foreach (var target in targets)
        {
            try
            {
                var reply = await ping.SendPingAsync(target, 3000);
                if (reply.Status == IPStatus.Success)
                {
                    results.AppendLine($"✅ {target}: {reply.RoundtripTime}ms");
                    RecordPing(target, (int)reply.RoundtripTime);
                }
                else
                    results.AppendLine($"❌ {target}: {reply.Status}");
            }
            catch (Exception ex)
            {
                results.AppendLine($"❌ {target}: {ex.Message}");
            }
        }

        pingResult.Text = results.ToString().TrimEnd();
        pingBtn.IsEnabled = true;
        LogOperation("Ping test completed");
    }

    private async void ClearHosts_Click(object sender, RoutedEventArgs e)
    {
        clearHostsBtn.IsEnabled = false;

        try
        {
            var hostsPath = @"C:\Windows\System32\drivers\etc\hosts";
            var existing = await File.ReadAllLinesAsync(hostsPath);
            var filtered = existing.Where(l =>
            {
                var t = l.Trim();
                if (string.IsNullOrEmpty(t) || t.StartsWith("#", StringComparison.Ordinal)) return true;
                if (HostsEntries.Any(he => t.Equals(he, StringComparison.OrdinalIgnoreCase))) return false;
                if (t.Contains("tr.rbxcdn.com", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }).ToArray();
            var removed = existing.Length - filtered.Length;

            if (removed > 0)
            {
                await File.WriteAllLinesAsync(hostsPath, filtered);
                LogOperation($"Removed {removed} hosts entries");
            }

            backupStatus.Text = removed > 0 ? $"✔ Removed {removed} hosts entries" : "ℹ No entries to remove";
            backupStatus.Foreground = (Brush)FindResource("SuccessBrush");
        }
        catch (Exception ex)
        {
            backupStatus.Text = $"✖ Error: {ex.Message}";
            backupStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            LogOperation($"Clear hosts error: {ex.Message}");
        }
        finally
        {
            clearHostsBtn.IsEnabled = true;
        }
    }

    private void ShowCurrentDns()
    {
        currentDnsPanel.Visibility = Visibility.Visible;
        var info = new StringBuilder();

        foreach (var name in GetAllAdapters().Select(a => a.name))
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", $"""interface ip show dns name="{name}" """)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) continue;
                var output = proc.StandardOutput.ReadToEnd();

                var servers = new List<string>();
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("Сервер DNS", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("DNS Server", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = t.Split(':');
                        if (parts.Length > 1) servers.Add(parts[^1].Trim());
                    }
                }
                info.AppendLine($"📡 {name}: {(servers.Count > 0 ? string.Join(", ", servers) : "DHCP")}");
            }
            catch { }
        }

        currentDnsInfo.Text = info.ToString();
    }

    private async void AddHosts_Click(object sender, RoutedEventArgs e)
    {
        hostsMessage.Visibility = Visibility.Collapsed;
        hostsButton.IsEnabled = false;

        try
        {
            var (addedCount, usedFallback) = await ApplyHostsAsync();
            hostsMessage.Text = $"✔ Added {addedCount} hosts entries ({(usedFallback ? "fallback IPs" : "fresh IPs")})";
            hostsMessage.Foreground = FindResource("SuccessBrush") as Brush;
        }
        catch (Exception ex)
        {
            hostsMessage.Text = $"✖ Error: {ex.Message}";
            hostsMessage.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            LogOperation($"Add hosts error: {ex.Message}");
        }
        finally
        {
            hostsMessage.Visibility = Visibility.Visible;
            hostsButton.IsEnabled = true;
        }
    }

    private async Task<(int added, bool usedFallback)> ApplyHostsAsync()
    {
        var hostsPath = @"C:\Windows\System32\drivers\etc\hosts";

        var backupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxImageFix");
        Directory.CreateDirectory(backupDir);
        File.Copy(hostsPath, Path.Combine(backupDir, "hosts.backup"), true);

        var (fresh, usedFallback) = await GetFreshTrRbxcdnEntriesAsync();
        LogOperation(usedFallback
            ? "tr.rbxcdn.com: fresh IPs unavailable, using fallback list"
            : $"tr.rbxcdn.com: got {fresh.Count} fresh IPs from dns.google");

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var target = new List<string>();
        foreach (var entry in HostsEntries.Concat(fresh))
        {
            var trimmed = entry.Trim();
            if (unique.Add(trimmed)) target.Add(trimmed);
        }

        var existing = File.ReadAllLines(hostsPath);
        var existingSet = new HashSet<string>(existing.Select(l => l.Trim()), StringComparer.OrdinalIgnoreCase);
        var addedCount = 0;
        foreach (var entry in target)
        {
            if (!existingSet.Contains(entry))
            {
                existing = [..existing, entry];
                existingSet.Add(entry);
                addedCount++;
            }
        }

        await File.WriteAllLinesAsync(hostsPath, existing);
        LogOperation($"Added {addedCount} hosts entries ({(usedFallback ? "fallback IPs" : "fresh IPs")})");
        return (addedCount, usedFallback);
    }

    private void OpenTelegram_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://t.me/bROtaniKaIFofie") { UseShellExecute = true }); }
        catch { }
    }

    private void Autorun_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (autorunCheck.IsChecked == true)
                key.SetValue("RobloxImageFix", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("RobloxImageFix", false);
        }
        catch { }
    }

    private static bool IsAutorunEnabled()
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("RobloxImageFix") != null;
        }
        catch { return false; }
    }

    private void AutoHosts_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\RobloxImageFix");
            key.SetValue("AutoHosts", autoHostsCheck.IsChecked == true ? "1" : "0");
        }
        catch { }
    }

    private static bool IsAutoHostsEnabled()
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\RobloxImageFix");
            return key?.GetValue("AutoHosts") as string != "0";
        }
        catch { return true; }
    }

    private void AnimateProgressBar()
    {
        progressFill.Width = 0;
        progressFill.Background = FindResource("PrimaryBrush") as Brush;
    }

    private void UpdateProgressBar(double fraction)
    {
        var parentWidth = ((Border)progressFill.Parent).ActualWidth;
        if (parentWidth <= 0) parentWidth = 340;
        var targetWidth = parentWidth * fraction;

        var anim = new DoubleAnimation
        {
            To = targetWidth,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        progressFill.BeginAnimation(Border.WidthProperty, anim);
    }

    private void ShowSuccess()
    {
        progressArea.Visibility = Visibility.Collapsed;
        successMessage.Text = "✔ Images Fixed";
        successMessage.Foreground = FindResource("SuccessBrush") as Brush;
        successMessage.Visibility = Visibility.Visible;

        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        successMessage.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void ShowError(string message)
    {
        progressArea.Visibility = Visibility.Collapsed;
        successMessage.Text = $"✖ {message}";
        successMessage.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
        successMessage.Visibility = Visibility.Visible;

        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        successMessage.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void SetStatus(string text, string color, string desc)
    {
        var colorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        statusDot.Background = colorBrush;
        statusText.Text = text;
        statusText.Foreground = colorBrush;
        statusDesc.Text = desc;
        ShowCurrentDns();
    }

    private static List<(string name, string status, bool hasIpv4)> GetAllAdapters()
    {
        var adapters = new List<(string, string, bool)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                var ipProps = ni.GetIPProperties();
                var hasIpv4 = ipProps.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                var status = ni.OperationalStatus.ToString();
                adapters.Add((ni.Name, status, hasIpv4));
            }
        }
        catch { }
        return adapters;
    }

    private void RefreshAdaptersDisplay()
    {
        adaptersPanel.Children.Clear();
        var adapters = GetAllAdapters();

        if (adapters.Count == 0)
        {
            noAdaptersText.Visibility = Visibility.Visible;
            currentAdapterText.Text = "⚪ None";
            adapterCountText.Text = "No adapters detected";
            return;
        }
        noAdaptersText.Visibility = Visibility.Collapsed;

        var hasWiFi = adapters.Any(a => a.name.Contains("Wi-Fi") || a.name.Contains("Wireless") || a.name.Contains("беспроводн") || a.name.Contains("WLAN"));
        var hasEth = adapters.Any(a => a.name.Contains("Ethernet") || a.name.Contains("eth") || a.name.Contains("Local"));
        var icon = hasWiFi ? "📡" : hasEth ? "🖥" : "🔗";

        var readyCount = adapters.Count(a => a.hasIpv4);
        currentAdapterText.Text = $"{icon}  {readyCount} active";
        adapterCountText.Text = $"{adapters.Count} adapter{(adapters.Count > 1 ? "s" : "")} found";

        foreach (var (name, status, hasIpv4) in adapters)
        {
            var isReady = hasIpv4;
            var adapterIcon = name.Contains("Wi-Fi") || name.Contains("Wireless") || name.Contains("беспроводн") || name.Contains("WLAN")
                ? "📡 " : name.Contains("VPN") || name.Contains("Radmin")
                ? "🔒 " : "🖥 ";

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0x15, 0x2A, 0x2D, 0x44)),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2D44")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 8, 8),
                Opacity = isReady ? 1.0 : 0.45
            };

            var stack = new StackPanel { Orientation = Orientation.Horizontal };

            stack.Children.Add(new Border
            {
                Width = 8, Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = isReady
                    ? (Brush)FindResource("SuccessBrush")
                    : new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2D, 0x44)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            stack.Children.Add(new TextBlock
            {
                Text = adapterIcon,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });

            stack.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });

            border.Child = stack;
            adaptersPanel.Children.Add(border);
        }
    }

    private static async Task<(bool success, string error)> SetDnsOnAdapter(string adapter, string primary, string secondary)
    {
        try
        {
            var setPrimary = $"""interface ip set dns name="{adapter}" source=static addr={primary} register=primary validate=no""";
            var psi = new ProcessStartInfo("netsh", setPrimary)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start netsh");
            await proc.WaitForExitAsync();
            var err = await proc.StandardError.ReadToEndAsync();
            if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                return (false, err.Trim());

            if (!string.IsNullOrWhiteSpace(secondary))
            {
                var addSecondary = $"""interface ip add dns name="{adapter}" addr={secondary} index=2 validate=no""";
                var psi2 = new ProcessStartInfo("netsh", addSecondary)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc2 = Process.Start(psi2);
                if (proc2 == null) return (false, "Failed to start netsh");
                await proc2.WaitForExitAsync();
                var err2 = await proc2.StandardError.ReadToEndAsync();
                if (proc2.ExitCode != 0 && !string.IsNullOrWhiteSpace(err2))
                    return (false, err2.Trim());
            }

            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(bool success, string error)> SetDohOnAdapter(string adapter, string address, string template)
    {
        try
        {
            var cmd = $"""dns add encryption "{adapter}" {address} {template}""";
            var psi = new ProcessStartInfo("netsh", cmd)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start netsh");
            await proc.WaitForExitAsync();
            var err = await proc.StandardError.ReadToEndAsync();
            if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                return (false, err.Trim());
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task FlushDns()
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig", "/flushdns")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch { }
    }

    private static bool IsWindows11()
    {
        try { return Environment.OSVersion.Version.Build >= 22000; }
        catch { return false; }
    }

    private async void SpeedTest_Click(object sender, RoutedEventArgs e)
    {
        speedTestBtn.IsEnabled = false;
        speedTestResult.Text = "Testing...";
        LogOperation("Speed test started");

        var results = new StringBuilder();
        using var ping = new Ping();

        foreach (var ip in AllDnsIps)
        {
            try
            {
                var reply = await ping.SendPingAsync(ip, 3000);
                var ms = reply.Status == IPStatus.Success ? $"{reply.RoundtripTime}ms" : reply.Status.ToString();
                results.AppendLine($"  {ip,-18} → {ms}");
            }
            catch (Exception)
            {
                results.AppendLine($"  {ip,-18} → error");
            }
            await Task.Delay(50);
        }

        speedTestResult.Text = results.ToString().TrimEnd();
        speedTestBtn.IsEnabled = true;
        LogOperation("Speed test completed");
    }

    private async void SpeedTestAll_Click(object sender, RoutedEventArgs e)
    {
        speedTestAllBtn.IsEnabled = false;
        speedTestAllResult.Text = "Pinging all providers...";
        LogOperation("Comparing all DNS providers");

        var results = new StringBuilder();
        using var ping = new Ping();

        foreach (var kv in DnsProfiles)
        {
            if (kv.Key == "dnsCustom" || string.IsNullOrWhiteSpace(kv.Value.primary)) continue;
            var label = kv.Key switch
            {
                "dnsGeohide" => "Geohide",
                "dnsGeohideAlt" => "Geohide Alt",
                "dnsGeohide1" => "Geohide 1",
                "dnsCloudflare" => "Cloudflare",
                "dnsGoogle" => "Google",
                "dnsQuad9" => "Quad9",
                "dnsOpenDns" => "OpenDNS",
                _ => kv.Key
            };
            try
            {
                var reply = await ping.SendPingAsync(kv.Value.primary, 3000);
                var ms = reply.Status == IPStatus.Success ? $"{reply.RoundtripTime}ms" : "✗";
                results.AppendLine($"  {label,-16} {kv.Value.primary,-16} → {ms}");
            }
            catch
            {
                results.AppendLine($"  {label,-16} {kv.Value.primary,-16} → ✗");
            }
        }

        speedTestAllResult.Text = results.ToString().TrimEnd();
        speedTestAllBtn.IsEnabled = true;
        LogOperation("Provider comparison done");
    }

    private async void MonitorDns_Toggled(object sender, RoutedEventArgs e)
    {
        if (monitorDnsCheck.IsChecked == true)
        {
            _dnsMonitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _dnsMonitorTimer.Tick += async (_, _) => await CheckAndRestoreDns();
            _dnsMonitorTimer.Start();
            LogOperation("DNS monitoring enabled (30s interval)");
        }
        else
        {
            _dnsMonitorTimer?.Stop();
            _dnsMonitorTimer = null;
            LogOperation("DNS monitoring disabled");
        }
    }

    private async Task CheckAndRestoreDns()
    {
        if (_isFixing || string.IsNullOrWhiteSpace(_lastSetPrimary)) return;

        try
        {
            var psi = new ProcessStartInfo("netsh", $"""interface ip show dns name="!" """)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            var output = await proc.StandardOutput.ReadToEndAsync();

            var found = false;
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if ((t.StartsWith("Сервер DNS", StringComparison.OrdinalIgnoreCase) ||
                     t.StartsWith("DNS Server", StringComparison.OrdinalIgnoreCase)) &&
                    t.Contains(_lastSetPrimary))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                LogOperation($"⚠ DNS was reset! Restoring {_lastSetPrimary}...");
                var adapters = GetAllAdapters().Select(a => a.name).ToList();
                foreach (var adapter in adapters)
                {
                    await SetDnsOnAdapter(adapter, _lastSetPrimary, _lastSetSecondary);
                }
                await FlushDns();
                LogOperation($"✔ DNS restored to {_lastSetPrimary}");
            }
        }
        catch { }
    }

    private void RecordPing(string host, int ms)
    {
        _pingHistory.Add(ms);
        if (_pingHistory.Count > 60) _pingHistory.RemoveAt(0);
        DrawPingGraph();
    }

    private void DrawPingGraph()
    {
        if (pingGraphCanvas == null) return;
        pingGraphCanvas.Children.Clear();

        if (_pingHistory.Count == 0) return;

        var w = pingGraphCanvas.ActualWidth;
        var h = pingGraphCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var maxMs = Math.Max(_pingHistory.Max(), 10);
        var stepX = w / Math.Max(_pingHistory.Count - 1, 1);
        var points = new PointCollection();

        for (int i = 0; i < _pingHistory.Count; i++)
        {
            var x = i * stepX;
            var y = h - (_pingHistory[i] / (double)maxMs) * (h - 8) - 4;
            points.Add(new Point(x, y));
        }

        if (points.Count < 2) return;

        var polyline = new System.Windows.Shapes.Polyline
        {
            Points = points,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#7C6BFF")),
            StrokeThickness = 2,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        polyline.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 8, ShadowDepth = 0, Color = (Color)ColorConverter.ConvertFromString("#7C6BFF"), Opacity = 0.3
        };
        pingGraphCanvas.Children.Add(polyline);

        var avg = _pingHistory.Average();
        var avgLine = new System.Windows.Shapes.Line
        {
            X1 = 0, Y1 = h - (avg / maxMs) * (h - 8) - 4,
            X2 = w, Y2 = h - (avg / maxMs) * (h - 8) - 4,
            Stroke = new SolidColorBrush(Color.FromArgb(60, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection([4, 4]),
        };
        pingGraphCanvas.Children.Add(avgLine);

        pingGraphInfo.Text = $"Avg: {avg:F0}ms  |  Last: {_pingHistory[^1]}ms  |  Samples: {_pingHistory.Count}";
    }

    private async void QuickPing_Click(object sender, RoutedEventArgs e)
    {
        quickPingBtn.IsEnabled = false;
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync("roblox.com", 5000);
            if (reply.Status == IPStatus.Success)
            {
                RecordPing("roblox.com", (int)reply.RoundtripTime);
                quickPingResult.Text = $"✔ {reply.RoundtripTime}ms";
                quickPingResult.Foreground = (Brush)FindResource("SuccessBrush");
            }
            else
            {
                quickPingResult.Text = $"✗ {reply.Status}";
                quickPingResult.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            }
            LogOperation($"Ping roblox.com: {reply.Status} ({reply.RoundtripTime}ms)");
        }
        catch (Exception ex)
        {
            quickPingResult.Text = $"✗ {ex.Message}";
            quickPingResult.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
        }
        quickPingBtn.IsEnabled = true;
    }

    private async void GenerateDiagLog_Click(object sender, RoutedEventArgs e)
    {
        diagGenerateBtn.IsEnabled = false;
        diagStatus.Text = "Generating diagnostic log...";
        diagLogOutput.Text = "";

        var log = new StringBuilder();

        try
        {
            // [1] Basic Info
            log.AppendLine("=== DNS Diagnostic Log ===");
            log.AppendLine($"Date: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            log.AppendLine($"Computer: {Environment.MachineName}");
            log.AppendLine($"User: {Environment.UserName}");
            log.AppendLine($"OS: {Environment.OSVersion}");
            log.AppendLine($"App: {Process.GetCurrentProcess().MainModule?.FileVersionInfo?.FileVersion ?? "1.0.0"}");
            log.AppendLine();

            // [2] Network Adapters
            log.AppendLine("[1] Network Adapters");
            var adaptersRaw = await RunPsCommand(
                "Get-NetAdapter | Format-Table -AutoSize -Property Name, Status, InterfaceDescription, MacAddress | Out-String -Width 4096");
            log.AppendLine(adaptersRaw);
            log.AppendLine();

            // [3] IP Addresses
            log.AppendLine("[2] IP Addresses");
            var ipRaw = await RunPsCommand(
                "Get-NetIPAddress -AddressFamily IPv4 | Format-Table -AutoSize -Property InterfaceAlias, IPAddress, PrefixLength, PrefixOrigin | Out-String -Width 4096");
            log.AppendLine(ipRaw);
            log.AppendLine();

            // [4] DNS Servers
            log.AppendLine("[3] DNS Servers");
            var dnsRaw = await RunPsCommand(
                "Get-DnsClientServerAddress | Where-Object { $_.ServerAddresses } | Format-Table -AutoSize -Property InterfaceAlias, ServerAddresses | Out-String -Width 4096");
            log.AppendLine(dnsRaw);
            log.AppendLine();

            // [5] Ping DNS
            var ips = new[] { "95.182.120.241", "45.155.204.190", "8.8.8.8" };
            foreach (var ip in ips)
            {
                log.AppendLine($"[4] Ping {ip}");
                var pingRaw = await RunPsCommand(
                    $"""ping -n 3 {ip} | Select-String -Pattern "(Ответ|Reply|Превышен|Request|ms|TTL|статистика|Statistics|Потерян|Lost)" """);
                log.AppendLine(string.IsNullOrWhiteSpace(pingRaw) ? "(no ping reply)" : pingRaw.TrimEnd());
                log.AppendLine();
            }

            // [6] nslookup via DNS
            if (DnsProfiles.TryGetValue(_selectedDnsRadio?.Name ?? "dnsGeohide", out var prof) && !string.IsNullOrWhiteSpace(prof.primary))
            {
                log.AppendLine($"[5] nslookup via {prof.primary}");
                var nsRaw = await RunPsCommand(
                    $"""nslookup roblox.com {prof.primary} 2>&1 | Select-String -Pattern "(Name|Address|Server|Aliases|DNS|request|timed|fail)" """);
                log.AppendLine(string.IsNullOrWhiteSpace(nsRaw) ? "(no response)" : nsRaw.TrimEnd());
                log.AppendLine();
            }

            // Default nslookup
            log.AppendLine("[6] nslookup default");
            var nsDef = await RunPsCommand(
                """nslookup roblox.com 2>&1 | Select-String -Pattern "(Name|Address|Server|Aliases|DNS|request|timed|fail)" """);
            log.AppendLine(string.IsNullOrWhiteSpace(nsDef) ? "(no response)" : nsDef.TrimEnd());
            log.AppendLine();

            // [7] hosts file
            log.AppendLine("[7] Hosts File");
            var hostsRaw = await RunPsCommand(
                """Get-Content "$env:SystemRoot\System32\drivers\etc\hosts" -ErrorAction SilentlyContinue | Where-Object { $_ -notmatch '^\s*(#|$)' }""");
            log.AppendLine(string.IsNullOrWhiteSpace(hostsRaw) ? "(no entries)" : hostsRaw.TrimEnd());
            log.AppendLine();

            // [8] Route Table
            log.AppendLine("[8] Route Table");
            var routeRaw = await RunPsCommand("route print -4 2>&1 | Out-String -Width 4096");
            log.AppendLine(routeRaw);
            log.AppendLine();

            // [9] Firewall Rules
            log.AppendLine("[9] Firewall Rules (DNS)");
            var fwRaw = await RunPsCommand(
                """netsh advfirewall firewall show rule name=all dir=out verbose | Select-String -Pattern "(5353|5355|53|DNS|dns)" -Context 2,0 | Out-String -Width 4096""");
            log.AppendLine(string.IsNullOrWhiteSpace(fwRaw) ? "(no DNS firewall rules)" : fwRaw.TrimEnd());
            log.AppendLine();

            // [10] ipconfig /all
            log.AppendLine("[10] IP Config All");
            var ipconfigRaw = await RunPsCommand("ipconfig /all 2>&1 | Out-String -Width 4096");
            log.AppendLine(ipconfigRaw);
            log.AppendLine();

            // [11] Test Port 53
            log.AppendLine("[11] Test Port 53");
            foreach (var ip in ips)
            {
                var portRaw = await RunPsCommand(
                    $"""Test-NetConnection -ComputerName {ip} -Port 53 -WarningAction SilentlyContinue | Select-Object ComputerName, TcpTestSucceeded | Format-Table -AutoSize | Out-String -Width 4096""");
                log.AppendLine(string.IsNullOrWhiteSpace(portRaw) ? $"  {ip}:53 — (no result)" : portRaw.TrimEnd());
            }
            log.AppendLine();

            log.AppendLine("=== END ===");
        }
        catch (Exception ex)
        {
            log.AppendLine($"ERROR: {ex.Message}");
        }

        diagLogOutput.Text = log.ToString().TrimEnd();
        diagCopyBtn.IsEnabled = true;
        diagSaveBtn.IsEnabled = true;
        diagStatus.Text = $"✔ Log generated — {log.Length} chars";
        diagStatus.Foreground = (Brush)FindResource("SuccessBrush");
        diagGenerateBtn.IsEnabled = true;
    }

    private void CopyDiagLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(diagLogOutput.Text)) return;
        try
        {
            Clipboard.SetText(diagLogOutput.Text);
            diagStatus.Text = "✔ Copied to clipboard";
            diagStatus.Foreground = (Brush)FindResource("SuccessBrush");
        }
        catch (Exception ex)
        {
            diagStatus.Text = $"✖ Copy failed: {ex.Message}";
            diagStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
        }
    }

    private async void SaveDiagLog_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(diagLogOutput.Text)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"dns-log-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            Title = "Save Diagnostic Log"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, diagLogOutput.Text);
            diagStatus.Text = $"✔ Saved to {dialog.FileName}";
            diagStatus.Foreground = (Brush)FindResource("SuccessBrush");
        }
        catch (Exception ex)
        {
            diagStatus.Text = $"✖ Save failed: {ex.Message}";
            diagStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
        }
    }

    private static async Task<string> RunPsCommand(string script, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script.Replace("\"", "\\\"")}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc == null) return "";
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            var exitTask = proc.WaitForExitAsync();
            var completed = await Task.WhenAny(exitTask, Task.Delay(timeoutMs));
            if (completed != exitTask)
            {
                try { proc.Kill(); } catch { }
                return (await outputTask + await errorTask).Trim();
            }
            return (await outputTask + await errorTask).Trim();
        }
        catch { return ""; }
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        try
        {
            Opacity = 0;
            _startupAnim = new Storyboard();
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600));
            Storyboard.SetTarget(anim, this);
            Storyboard.SetTargetProperty(anim, new PropertyPath(OpacityProperty));
            _startupAnim.Children.Add(anim);
            _startupAnim.Completed += (_, _) => Opacity = 1;
            _startupAnim.Begin();
            ShowCurrentDns();
            operationLogList.ItemsSource = _operationLog;
            mainScrollViewer?.ScrollToTop();
        }
        catch { Opacity = 1; }
    }

    private void DnsDropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            dnsDropBg.Background = (Brush)FindResource("NeonButtonBg")!;
            dnsDropText.Text = "📄 Drop here to analyze";
        }
        else
            e.Effects = DragDropEffects.None;
    }

    private void DnsDropZone_DragLeave(object sender, DragEventArgs e)
    {
        dnsDropBg.Background = (Brush)new BrushConverter().ConvertFromString("#0A7C6BFF")!;
        dnsDropText.Text = "📄 Drop .txt file here";
    }

    private async void DnsDropZone_Drop(object sender, DragEventArgs e)
    {
        dnsDropBg.Background = (Brush)new BrushConverter().ConvertFromString("#0A7C6BFF")!;
        dnsDropText.Text = "📄 Drop .txt file here";

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var path = files.FirstOrDefault();
        if (path == null || !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            dnsAnalyzeOutput.Text = "⚠ Drop only .txt files";
            dnsAnalyzeResult.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(path);
            var diagnosis = AnalyzeDnsDiagnostic(content);
            dnsAnalyzeOutput.Text = diagnosis;
            dnsAnalyzeResult.Visibility = Visibility.Visible;
            LogOperation($"DNS analysis: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            dnsAnalyzeOutput.Text = $"✖ Error reading file: {ex.Message}";
            dnsAnalyzeResult.Visibility = Visibility.Visible;
        }
    }

    private static string AnalyzeDnsDiagnostic(string content)
    {
        var result = new System.Text.StringBuilder();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        bool hasDnsServers = false, hasDnsErrors = false, hasTimeout = false;
        bool hasNoReply = false, hasNonExistentDomain = false, hasServerFail = false;
        bool hasDhcpEnabled = false;
        var dnsServers = new List<string>();

        // Detect command type
        bool isIpconfig = content.Contains("Windows IP Configuration", StringComparison.OrdinalIgnoreCase)
                          || content.Contains("IPv4 Address", StringComparison.OrdinalIgnoreCase);
        bool isNslookup = content.Contains("nslookup", StringComparison.OrdinalIgnoreCase)
                          || content.Contains("Non-authoritative answer", StringComparison.OrdinalIgnoreCase)
                          || content.Contains("Address:", StringComparison.OrdinalIgnoreCase)
                          && !content.Contains("IPv4", StringComparison.OrdinalIgnoreCase);
        bool isPing = content.Contains("Reply from", StringComparison.OrdinalIgnoreCase)
                      || content.Contains("Request timed out", StringComparison.OrdinalIgnoreCase)
                      || content.Contains("TTL=", StringComparison.OrdinalIgnoreCase);
        bool isNetsh = content.Contains("netsh", StringComparison.OrdinalIgnoreCase)
                       || content.Contains("DNS servers", StringComparison.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var t = line.Trim();

            if (t.Contains("DHCP Enabled", StringComparison.OrdinalIgnoreCase) && t.Contains("Yes", StringComparison.OrdinalIgnoreCase))
                hasDhcpEnabled = true;

            if (t.Contains("DNS Servers", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("DNS servers", StringComparison.OrdinalIgnoreCase))
            {
                hasDnsServers = true;
                var parts = t.Split(':', '.');
                var ip = System.Text.RegularExpressions.Regex.Match(t, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b");
                if (ip.Success) dnsServers.Add(ip.Value);
                continue;
            }

            // Also capture IP lines following "DNS Servers"
            var ipMatch = System.Text.RegularExpressions.Regex.Match(t, @"^\s*(\.?\s*)?(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\s*$");
            if (ipMatch.Success && hasDnsServers)
                dnsServers.Add(ipMatch.Groups[2].Value);

            if (t.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || t.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                hasTimeout = true;

            if (t.Contains("Reply from", StringComparison.OrdinalIgnoreCase) && t.Contains("Destination host unreachable", StringComparison.OrdinalIgnoreCase))
                hasNoReply = true;

            if (t.Contains("Non-existent domain", StringComparison.OrdinalIgnoreCase)
                || t.Contains("NXDOMAIN", StringComparison.OrdinalIgnoreCase))
                hasNonExistentDomain = true;

            if (t.Contains("server fail", StringComparison.OrdinalIgnoreCase)
                || t.Contains("SERVFAIL", StringComparison.OrdinalIgnoreCase))
                hasServerFail = true;

            if (t.Contains("UnKnown", StringComparison.OrdinalIgnoreCase)
                || (t.Contains("can't find", StringComparison.OrdinalIgnoreCase))
                || (t.Contains("DNS request timed out", StringComparison.OrdinalIgnoreCase)))
                hasDnsErrors = true;

        }

        // Build diagnosis
        if (isIpconfig)
        {
            result.AppendLine("📋 Detected: ipconfig /all");
            result.AppendLine();

            if (dnsServers.Count == 0)
                result.AppendLine("❌ No DNS servers configured on any adapter");
            else if (dnsServers.Count > 0)
            {
                result.AppendLine($"✓ DNS servers found: {string.Join(", ", dnsServers)}");
                foreach (var dns in dnsServers)
                {
                    if (dns.StartsWith("192.168.") || dns.StartsWith("10.") || dns.StartsWith("172.16."))
                        result.AppendLine($"⚠  {dns} is a local/private IP — likely your router (not a public resolver)");
                }
            }

            if (hasDhcpEnabled)
                result.AppendLine("ℹ DHCP is enabled — DNS may be assigned automatically");

            result.AppendLine();
            result.AppendLine("💡 Suggestion: Set a static public DNS (Cloudflare 1.1.1.1, Google 8.8.8.8)");
        }
        else if (isNslookup)
        {
            result.AppendLine("📋 Detected: nslookup");
            result.AppendLine();

            if (hasTimeout)
                result.AppendLine("❌ DNS request timed out — DNS server is unreachable or blocking queries");

            if (hasServerFail)
                result.AppendLine("❌ DNS server failure (SERVFAIL) — server misconfiguration or overload");

            if (hasNonExistentDomain)
                result.AppendLine("⚠ Domain does not exist (NXDOMAIN) — check domain spelling");

            if (hasDnsErrors)
                result.AppendLine("❌ DNS resolution failed — the DNS server is not responding correctly");

            if (dnsServers.Count > 0)
                result.AppendLine($"ℹ Using DNS server: {dnsServers[0]}");

            if (!hasTimeout && !hasServerFail && !hasDnsErrors)
                result.AppendLine("✓ DNS resolution seems OK — no errors detected");

            result.AppendLine();
            result.AppendLine("💡 Suggestion: Try ping 8.8.8.8 — if that works but nslookup fails, it's a DNS issue");
        }
        else if (isPing)
        {
            result.AppendLine("📋 Detected: ping");
            result.AppendLine();

            if (hasTimeout)
                result.AppendLine("❌ Packets timed out — host unreachable or firewall blocking");

            if (hasNoReply)
                result.AppendLine("❌ Destination host unreachable — network or routing issue");

            int received = 0, sent = 0;
            var statMatch = System.Text.RegularExpressions.Regex.Match(content, @"Packets: Sent = (\d+), Received = (\d+)");
            if (statMatch.Success)
            {
                sent = int.Parse(statMatch.Groups[1].Value);
                received = int.Parse(statMatch.Groups[2].Value);
                result.AppendLine($"📊 Packets: {received}/{sent} received ({(sent > 0 ? received * 100 / sent : 0)}%)");
                if (received == sent)
                    result.AppendLine("✓ All packets received — good connectivity");
                else if (received > 0)
                    result.AppendLine($"⚠ Packet loss detected ({sent - received} lost)");
                else
                    result.AppendLine("❌ Complete packet loss — no connectivity");
            }
            else
            {
                // Count reply vs timeout lines
                var replies = lines.Count(l => l.Contains("Reply from", StringComparison.OrdinalIgnoreCase));
                var timeouts = lines.Count(l => l.Contains("timed out", StringComparison.OrdinalIgnoreCase));
                var total = replies + timeouts;
                if (total > 0)
                {
                    result.AppendLine($"📊 Replies: {replies}/{total}");
                    if (timeouts > 0)
                        result.AppendLine($"⚠ {timeouts} packet(s) lost");
                }
            }

            result.AppendLine();
            result.AppendLine("💡 Suggestion: Ping 8.8.8.8 vs ping google.com to distinguish network vs DNS issue");
        }
        else if (isNetsh)
        {
            result.AppendLine("📋 Detected: netsh interface ip show dns");
            result.AppendLine();

            if (dnsServers.Count == 0)
                result.AppendLine("❌ No DNS servers configured — adapters use DHCP");
            else
            {
                result.AppendLine($"✓ DNS: {string.Join(", ", dnsServers)}");
                foreach (var dns in dnsServers)
                {
                    if (dns.StartsWith("192.168.") || dns.StartsWith("10.") || dns.StartsWith("172.16."))
                        result.AppendLine($"⚠  {dns} is a local IP (router), consider public DNS");
                }
            }

            result.AppendLine();
            result.AppendLine("💡 Suggestion: Use 'ipconfig /all' for full network diagnostics");
        }
        else
        {
            result.AppendLine("📋 Unknown format — guessing...");
            result.AppendLine();

            if (hasTimeout)
                result.AppendLine("❌ Timeouts detected — connectivity problem");
            if (hasDnsErrors || hasServerFail)
                result.AppendLine("❌ DNS errors detected");
            if (hasNonExistentDomain)
                result.AppendLine("⚠ NXDOMAIN detected");
            if (dnsServers.Count > 0)
                result.AppendLine($"ℹ DNS servers found: {string.Join(", ", dnsServers)}");

            if (!hasTimeout && !hasDnsErrors && !hasServerFail && dnsServers.Count == 0)
                result.AppendLine("ℹ No obvious DNS or network issues detected in the log");

            result.AppendLine();
            result.AppendLine("💡 Suggestion: Run ipconfig /all and nslookup google.com for better diagnosis");
        }

        return result.ToString().TrimEnd();
    }
}
