using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace Setup;

static class Program
{
    const string RuntimeVersion = "10.0.9";
    const string RuntimeUrl = $"https://dotnetcli.azureedge.net/dotnet/Runtime/{RuntimeVersion}/dotnet-runtime-{RuntimeVersion}-win-x64.exe";
    const string InstallDir = @"C:\Program Files\RobloxImageFix";

    [STAThread]
    static void Main()
    {
        if (!IsAdmin())
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { Verb = "runas", UseShellExecute = true };
            try { Process.Start(psi); } catch { }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupWizard());
    }

    internal static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(id);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    internal static bool CheckDotNetRuntime()
    {
        // Registry check
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
            if (key != null)
                foreach (var name in key.GetValueNames())
                    if (key.GetValue(name)?.ToString()?.StartsWith("10.") == true) return true;
            using var key32 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
            if (key32 != null)
                foreach (var name in key32.GetValueNames())
                    if (key32.GetValue(name)?.ToString()?.StartsWith("10.") == true) return true;
        }
        catch { }

        // Fallback: try running dotnet --list-runtimes
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                if (output.Contains("Microsoft.WindowsDesktop.App 10."))
                    return true;
            }
        }
        catch { }

        return false;
    }

    internal static string GetInstallDir() => InstallDir;
    internal static string GetRuntimeUrl() => RuntimeUrl;
    internal static string GetRuntimeVersion() => RuntimeVersion;
}

class SetupWizard : Form
{
    private Panel cardPanel = new();
    private Panel[] pages = new Panel[4];
    private Button backBtn = new();
    private Button nextBtn = new();
    private Button cancelBtn = new();

    private Label welcomeLabel = new();
    private Label infoLabel = new();
    private Label progressTitle = new();
    private Label progressDesc = new();
    private ProgressBar progressBar = new();
    private Label completeLabel = new();

    private int currentPage = 0;
    private bool installing;

    public SetupWizard()
    {
        InitializeForm();
    }

    private void InitializeForm()
    {
        Text = "Roblox Image Fix — Setup";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowIcon = false;
        BackColor = Color.FromArgb(0xF0, 0xF0, 0xF5);
        ClientSize = new Size(580, 420);

        // Header
        var header = new Panel
        {
            Height = 80,
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(0x09, 0x09, 0x0F)
        };
        var title = new Label
        {
            Text = "⚡  Roblox Image Fix",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.FromArgb(0xF0, 0xF0, 0xF5),
            Location = new Point(24, 20),
            AutoSize = true
        };
        var subtitle = new Label
        {
            Text = "Setup Wizard",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(0x88, 0x88, 0xA0),
            Location = new Point(24, 50),
            AutoSize = true
        };
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        Controls.Add(header);

        // Bottom buttons bar
        var bottomBar = new Panel
        {
            Height = 60,
            Dock = DockStyle.Bottom,
            BackColor = Color.FromArgb(0xE8, 0xE8, 0xF0)
        };

        cancelBtn.Text = "Cancel";
        cancelBtn.Size = new Size(100, 34);
        cancelBtn.Font = new Font("Segoe UI", 10);
        cancelBtn.Location = new Point(ClientSize.Width - 320, 14);
        cancelBtn.FlatStyle = FlatStyle.Flat;
        cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(0xCC, 0xCC, 0xD0);
        cancelBtn.BackColor = Color.White;
        cancelBtn.ForeColor = Color.FromArgb(0x33, 0x33, 0x44);
        cancelBtn.Click += (_, _) => Close();
        bottomBar.Controls.Add(cancelBtn);

        backBtn.Text = "< Back";
        backBtn.Size = new Size(100, 34);
        backBtn.Font = new Font("Segoe UI", 10);
        backBtn.Location = new Point(ClientSize.Width - 210, 14);
        backBtn.FlatStyle = FlatStyle.Flat;
        backBtn.FlatAppearance.BorderColor = Color.FromArgb(0xCC, 0xCC, 0xD0);
        backBtn.BackColor = Color.White;
        backBtn.ForeColor = Color.FromArgb(0x33, 0x33, 0x44);
        backBtn.Enabled = false;
        backBtn.Click += Back_Click;
        bottomBar.Controls.Add(backBtn);

        nextBtn.Text = "Next >";
        nextBtn.Size = new Size(100, 34);
        nextBtn.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        nextBtn.Location = new Point(ClientSize.Width - 100, 14);
        nextBtn.BackColor = Color.FromArgb(0x7C, 0x6B, 0xFF);
        nextBtn.ForeColor = Color.White;
        nextBtn.FlatStyle = FlatStyle.Flat;
        nextBtn.Click += Next_Click;
        bottomBar.Controls.Add(nextBtn);

        Controls.Add(bottomBar);

        // Card panel (content area)
        cardPanel.Location = new Point(20, 96);
        cardPanel.Size = new Size(ClientSize.Width - 40, ClientSize.Height - 96 - 60 - 8);
        cardPanel.BackColor = Color.White;
        Controls.Add(cardPanel);

        // === Pages ===
        CreateWelcomePage();
        CreateInfoPage();
        CreateInstallPage();
        CreateCompletePage();

        ShowPage(0);
    }

    private void CreateWelcomePage()
    {
        pages[0] = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };
        pages[0].Controls.Add(new Label
        {
            Text = "Добро пожаловать в установщик Roblox Image Fix",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x09, 0x09, 0x0F),
            Location = new Point(0, 10),
            AutoSize = true
        });
        pages[0].Controls.Add(new Label
        {
            Text = "Эта программа установит Roblox Image Fix на ваш компьютер.\n\n" +
                   "Нажмите «Далее», чтобы продолжить, или «Отмена», чтобы выйти.",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(0x66, 0x66, 0x80),
            Location = new Point(0, 44),
            Size = new Size(480, 120)
        });
        pages[0].Controls.Add(new Label
        {
            Text = "Рекомендуется закрыть все запущенные приложения перед установкой.",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(0x99, 0x99, 0xB0),
            Location = new Point(0, 160),
            AutoSize = true
        });
    }

    private Label netStatusLabel = new();
    private bool netRuntimeChecked;

    private void CreateInfoPage()
    {
        pages[1] = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };
        pages[1].Controls.Add(new Label
        {
            Text = "Информация о программе",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x09, 0x09, 0x0F),
            Location = new Point(0, 10),
            AutoSize = true
        });

        netStatusLabel = new Label
        {
            Text = "⏳ Проверка .NET Runtime...",
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x88, 0x88, 0xA0),
            Location = new Point(0, 36),
            AutoSize = true
        };
        pages[1].Controls.Add(netStatusLabel);

        var infoBox = new TextBox
        {
            Text = "Roblox Image Fix — программа для быстрого восстановления изображений Roblox через настройку DNS.\n\n" +
                   "Помогает исправить проблемы с загрузкой CDN-контента (аватары, одежда, иконки и т.д.) путём переключения на рабочие DNS-серверы.\n\n" +
                   "Основные возможности:\r\n" +
                   "• Автоматическая настройка DNS\r\n" +
                   "• Поддержка популярных серверов (Geohide, Cloudflare, Google, Quad9)\r\n" +
                   "• Работа со всеми сетевыми адаптерами\r\n" +
                   "• Мгновенное применение изменений\r\n\n" +
                   "Разработано для удобства пользователей Roblox.",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(0x44, 0x44, 0x55),
            Location = new Point(0, 60),
            Size = new Size(480, 230),
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White
        };
        pages[1].Controls.Add(infoBox);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Check .NET runtime in background when first shown
        if (!netRuntimeChecked)
        {
            netRuntimeChecked = true;
            Task.Run(() =>
            {
                var found = Program.CheckDotNetRuntime();
                Invoke(() =>
                {
                    netStatusLabel.Text = found
                        ? "✔ .NET Desktop Runtime 10 установлен"
                        : "✗ .NET Desktop Runtime 10 не найден — будет скачан";
                    netStatusLabel.ForeColor = found
                        ? Color.FromArgb(0x22, 0xC5, 0x5E)
                        : Color.FromArgb(0xEF, 0x44, 0x44);
                });
            });
        }
    }

    private void CreateInstallPage()
    {
        pages[2] = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };

        progressTitle = new Label
        {
            Text = "Установка...",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x09, 0x09, 0x0F),
            Location = new Point(0, 10),
            AutoSize = true
        };
        pages[2].Controls.Add(progressTitle);

        progressDesc = new Label
        {
            Text = "Пожалуйста, подождите...",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(0x66, 0x66, 0x80),
            Location = new Point(0, 38),
            AutoSize = true
        };
        pages[2].Controls.Add(progressDesc);

        progressBar = new ProgressBar
        {
            Style = ProgressBarStyle.Continuous,
            Location = new Point(0, 74),
            Size = new Size(480, 18),
            ForeColor = Color.FromArgb(0x7C, 0x6B, 0xFF),
            BackColor = Color.FromArgb(0xE8, 0xE8, 0xF0)
        };
        pages[2].Controls.Add(progressBar);
    }

    private void CreateCompletePage()
    {
        pages[3] = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };
        completeLabel = new Label
        {
            Text = "✔  Установка завершена!",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x22, 0xC5, 0x5E),
            Location = new Point(0, 30),
            AutoSize = true
        };
        pages[3].Controls.Add(completeLabel);
        pages[3].Controls.Add(new Label
        {
            Text = "Roblox Image Fix успешно установлен на ваш компьютер.\n\n" +
                   "Запустить программу можно через меню Пуск или по ярлыку на рабочем столе.\n\n" +
                   "Спасибо, что выбрали Roblox Image Fix!",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(0x66, 0x66, 0x80),
            Location = new Point(0, 66),
            Size = new Size(480, 100)
        });
        var runNow = new CheckBox
        {
            Text = "Запустить Roblox Image Fix",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(0x33, 0x33, 0x44),
            Location = new Point(0, 160),
            AutoSize = true,
            Checked = true
        };
        pages[3].Controls.Add(runNow);
    }

    private void ShowPage(int index)
    {
        foreach (var p in pages) p.Visible = false;
        pages[index].Visible = true;
        pages[index].Parent = cardPanel;
        cardPanel.Controls.Clear();
        cardPanel.Controls.Add(pages[index]);

        currentPage = index;
        backBtn.Enabled = index > 0 && !installing;
        nextBtn.Enabled = !installing;
        cancelBtn.Enabled = !installing;

        if (index == 0) backBtn.Visible = false;
        else backBtn.Visible = true;

        if (index == 3)
        {
            nextBtn.Text = "Finish";
            backBtn.Visible = false;
        }
        else
        {
            nextBtn.Text = "Next >";
        }
    }

    private void Back_Click(object? sender, EventArgs e)
    {
        if (currentPage > 0 && !installing)
            ShowPage(currentPage - 1);
    }

    private async void Next_Click(object? sender, EventArgs e)
    {
        if (currentPage == 0)
        {
            ShowPage(1);
        }
        else if (currentPage == 1)
        {
            ShowPage(2);
            await RunInstall();
        }
        else if (currentPage == 3)
        {
            Close();
        }
    }

    private async Task RunInstall()
    {
        installing = true;
        backBtn.Enabled = false;
        nextBtn.Enabled = false;
        cancelBtn.Enabled = false;

        try
        {
            if (!Program.CheckDotNetRuntime())
            {
                SetProgress("Скачивание .NET Desktop Runtime...", "Это может занять несколько минут", 0);
                await DownloadAndInstallRuntime();
            }
            else
            {
                SetProgress(".NET Runtime уже установлен", "Продолжаем установку", 15);
            }

            SetProgress("Установка приложения...", "Копирование файлов и создание ярлыков", 30);
            await Task.Delay(200);
            InstallAppFiles();
            CreateShortcuts();
            CreateUninstallEntry();

            SetProgress("", "", 100);
            ShowPage(3);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка установки: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowPage(0);
        }
        finally
        {
            installing = false;
            backBtn.Enabled = false;
            nextBtn.Enabled = true;
            cancelBtn.Enabled = true;
        }
    }

    private async Task DownloadAndInstallRuntime()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "RIF-Setup");
        Directory.CreateDirectory(tmpDir);
        var installerPath = Path.Combine(tmpDir, "dotnet-runtime.exe");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxImageFix-Setup/1.0");

        var response = await client.GetAsync(Program.GetRuntimeUrl(), HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        using var stream = await response.Content.ReadAsStreamAsync();

        // Remove old installer if exists
        try { File.Delete(installerPath); } catch { }

        using (var fileStream = File.Create(installerPath))
        {
            var buffer = new byte[81920];
            long read = 0;
            int bytes;
            while ((bytes = await stream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytes));
                read += bytes;
                if (total > 0)
                {
                    var pct = 10 + (int)(read * 20 / total);
                    progressBar.Value = Math.Min(pct, 30);
                }
            }
            await fileStream.FlushAsync();
        }

        // Wait for file to be released
        await Task.Delay(500);

        SetProgress("Установка .NET Runtime...", "Пожалуйста, подождите", 30);

        // Retry starting the installer with backoff
        Process? proc = null;
        for (int retry = 0; retry < 5; retry++)
        {
            try
            {
                var psi = new ProcessStartInfo(installerPath, "/install /quiet /norestart")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                proc = Process.Start(psi);
                break;
            }
            catch when (retry < 4) { await Task.Delay(1000); }
        }

        if (proc != null)
        {
            proc.WaitForExit();
            if (proc.ExitCode != 0 && proc.ExitCode != 1641 && proc.ExitCode != 3010)
                throw new Exception($"Runtime installer failed (code {proc.ExitCode})");
        }
        else
        {
            throw new Exception("Could not start the runtime installer after 5 retries");
        }

        try { File.Delete(installerPath); } catch { }
    }

    private void InstallAppFiles()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (appDir == null) throw new Exception("Cannot determine source directory");

        var srcDir = Path.Combine(appDir, "App");
        if (!Directory.Exists(srcDir))
            srcDir = appDir;
        if (!Directory.Exists(srcDir))
            throw new Exception("Source files not found");

        var installDir = Program.GetInstallDir();
        Directory.CreateDirectory(installDir);

        var files = Directory.GetFiles(srcDir, "*");
        for (int i = 0; i < files.Length; i++)
        {
            var name = Path.GetFileName(files[i]);
            if (name.StartsWith("RobloxImageFix-Setup", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(files[i], Path.Combine(installDir, name), true);
            progressBar.Value = 30 + (int)((double)(i + 1) / files.Length * 50);
        }
    }

    private void CreateShortcuts()
    {
        var target = Path.Combine(Program.GetInstallDir(), "RobloxImageFix.exe");

        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            "Programs", "Roblox Image Fix.lnk");
        CreateShortcut(startMenu, target);

        var desktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "Roblox Image Fix.lnk");
        CreateShortcut(desktop, target);

        progressBar.Value = 85;
    }

    private static void CreateShortcut(string path, string target)
    {
        try
        {
            var dir = Path.GetDirectoryName(target);
            var ps = $"""
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut('{path.Replace("'", "''")}')
$sc.TargetPath = '{target.Replace("'", "''")}'
$sc.WorkingDirectory = '{dir?.Replace("'", "''")}'
$sc.Save()
""";
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{ps.Replace("\"", "\\\"")}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { }
    }

    private static void CreateUninstallEntry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RobloxImageFix");
            if (key == null) return;
            key.SetValue("DisplayName", "Roblox Image Fix");
            key.SetValue("DisplayVersion", "1.2.0");
            key.SetValue("Publisher", "Roblox Image Fix");
            key.SetValue("DisplayIcon", Path.Combine(Program.GetInstallDir(), "app.ico"));
            key.SetValue("UninstallString", $"\"{Path.Combine(Program.GetInstallDir(), "RobloxImageFix.exe")}\" /uninstall");
            key.SetValue("InstallLocation", Program.GetInstallDir());
            key.SetValue("NoModify", 1);
            key.SetValue("NoRepair", 1);
        }
        catch { }
    }

    private void SetProgress(string title, string desc, int progress)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetProgress(title, desc, progress));
            return;
        }
        progressTitle.Text = title;
        progressDesc.Text = desc;
        if (progress > 0) progressBar.Value = Math.Min(progress, 100);
    }
}
