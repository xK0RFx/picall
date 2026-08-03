using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PicallSetup
{
    internal static class Program
    {
        public const string ProductName = "Picall";
        public const string Version = "1.2.4";
        public static readonly string InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Picall");

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Uninstall();
                return;
            }
            Application.Run(new SetupForm());
        }

        public static bool IsRuntimeInstalled()
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"))
                {
                    if (key != null && key.GetValueNames().Any(IsVersionEightOrNewer)) return true;
                }
            }
            catch { }

            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                return Directory.Exists(root) && Directory.GetDirectories(root).Select(Path.GetFileName).Any(IsVersionEightOrNewer);
            }
            catch { return false; }
        }

        private static bool IsVersionEightOrNewer(string value)
        {
            System.Version runtimeVersion;
            return System.Version.TryParse(value.Split('-')[0], out runtimeVersion) && runtimeVersion.Major >= 8;
        }

        public static void Install(bool createDesktopShortcut)
        {
            if (Process.GetProcessesByName("Picall").Any())
                throw new InvalidOperationException("Закройте Picall перед установкой обновления.");

            if (Directory.Exists(InstallDirectory)) Directory.Delete(InstallDirectory, true);
            Directory.CreateDirectory(InstallDirectory);

            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("Picall.Payload.zip"))
            {
                if (payload == null) throw new InvalidOperationException("В установщике отсутствуют файлы приложения.");
                using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
                {
                    var root = Path.GetFullPath(InstallDirectory) + Path.DirectorySeparatorChar;
                    foreach (var entry in archive.Entries)
                    {
                        var target = Path.GetFullPath(Path.Combine(InstallDirectory, entry.FullName));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Некорректный путь в пакете установки.");
                        if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                    }
                }
            }

            var uninstaller = Path.Combine(InstallDirectory, "Uninstall.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, uninstaller, true);
            var app = Path.Combine(InstallDirectory, "Picall.exe");
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Picall.lnk"), app);
            var desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Picall.lnk");
            if (createDesktopShortcut) CreateShortcut(desktopShortcut, app);
            else if (File.Exists(desktopShortcut)) File.Delete(desktopShortcut);

            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Picall"))
            {
                key.SetValue("DisplayName", ProductName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "Picall");
                key.SetValue("DisplayIcon", app + ",0");
                key.SetValue("InstallLocation", InstallDirectory);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new DirectoryInfo(InstallDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024), RegistryValueKind.DWord);
            }
        }

        public static void Launch() { Process.Start(Path.Combine(InstallDirectory, "Picall.exe")); }

        private static void CreateShortcut(string shortcutPath, string targetPath)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = InstallDirectory;
            shortcut.IconLocation = targetPath + ",0";
            shortcut.Description = "Локальная медиатека фото и видео";
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }

        private static void Uninstall()
        {
            if (MessageBox.Show("Удалить Picall с этого компьютера?\n\nЛичный индекс и кэш превью также будут удалены.",
                    "Удаление Picall", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            if (Process.GetProcessesByName("Picall").Any())
            {
                MessageBox.Show("Сначала закройте Picall и повторите удаление.", "Picall", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Picall.lnk"));
                DeleteIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Picall.lnk"));
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Picall", false);
                var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picall");
                if (Directory.Exists(data)) Directory.Delete(data, true);

                var runningExecutable = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                if (Directory.Exists(InstallDirectory))
                {
                    foreach (var file in Directory.GetFiles(InstallDirectory, "*", SearchOption.AllDirectories))
                    {
                        if (string.Equals(Path.GetFullPath(file), runningExecutable, StringComparison.OrdinalIgnoreCase)) continue;
                        try { File.Delete(file); } catch { MoveFileEx(file, null, 4); }
                    }
                    foreach (var directory in Directory.GetDirectories(InstallDirectory, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                        try { Directory.Delete(directory); } catch { }
                }
                if (runningExecutable.StartsWith(Path.GetFullPath(InstallDirectory), StringComparison.OrdinalIgnoreCase))
                    MoveFileEx(runningExecutable, null, 4);
                MoveFileEx(InstallDirectory, null, 4);
                MessageBox.Show("Picall удалён.", "Picall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось полностью удалить Picall:\n" + ex.Message, "Picall", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void DeleteIfExists(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFile, string newFile, int flags);
    }

    internal sealed class SetupForm : Form
    {
        private readonly Button _installButton;
        private readonly CheckBox _desktopShortcut;
        private readonly Label _status;
        private readonly ProgressBar _progress;

        public SetupForm()
        {
            Text = "Установка Picall";
            ClientSize = new Size(640, 414);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(11, 13, 16);
            ForeColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Segoe UI", 9f);
            Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);

            var logo = new LogoPanel { Location = new Point(42, 42), Size = new Size(72, 72) };
            Controls.Add(logo);

            Controls.Add(new Label
            {
                Text = "PICALL  1.2.4", Location = new Point(142, 43), AutoSize = true,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = Color.FromArgb(167, 139, 250)
            });
            Controls.Add(new Label
            {
                Text = "Вся медиатека — сразу перед глазами", Location = new Point(139, 66), Size = new Size(450, 42),
                Font = new Font("Segoe UI", 20f, FontStyle.Bold), ForeColor = Color.White
            });
            Controls.Add(new Label
            {
                Text = "Быстрый локальный поиск фото и видео без аккаунта, облака и лишних инструментов.",
                Location = new Point(142, 108), Size = new Size(430, 38), ForeColor = Color.FromArgb(148, 157, 170)
            });

            var featureText = "✓  Автоматически находит медиа на всех дисках\r\n\r\n✓  Показывает превью фото и видео\r\n\r\n✓  Следит за новыми файлами в реальном времени";
            Controls.Add(new Label
            {
                Text = featureText, Location = new Point(48, 172), Size = new Size(540, 105),
                Font = new Font("Segoe UI", 10f), ForeColor = Color.FromArgb(218, 222, 229)
            });

            var pathLabel = new Label
            {
                Text = "Будет установлено для текущего пользователя:\r\n" + Program.InstallDirectory,
                Location = new Point(48, 286), Size = new Size(520, 40), ForeColor = Color.FromArgb(125, 135, 150)
            };
            Controls.Add(pathLabel);

            _desktopShortcut = new CheckBox
            {
                Text = "Ярлык на рабочем столе", Location = new Point(48, 343), Size = new Size(210, 26),
                Checked = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(218, 222, 229)
            };
            Controls.Add(_desktopShortcut);

            _status = new Label
            {
                Location = new Point(48, 374), Size = new Size(330, 24), ForeColor = Color.FromArgb(167, 139, 250), Visible = false
            };
            Controls.Add(_status);

            _progress = new ProgressBar
            {
                Location = new Point(388, 377), Size = new Size(200, 5), Style = ProgressBarStyle.Marquee, Visible = false
            };
            Controls.Add(_progress);

            _installButton = new Button
            {
                Text = "Установить", Location = new Point(440, 334), Size = new Size(148, 44),
                BackColor = Color.FromArgb(124, 78, 234), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold), Cursor = Cursors.Hand
            };
            _installButton.FlatAppearance.BorderSize = 0;
            _installButton.Click += InstallClicked;
            Controls.Add(_installButton);
        }

        private async void InstallClicked(object sender, EventArgs e)
        {
            if (!Program.IsRuntimeInstalled())
            {
                var answer = MessageBox.Show(
                    "Для Picall нужен бесплатный Microsoft Windows Desktop Runtime 8. Открыть страницу загрузки?",
                    "Нужен компонент Microsoft", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                    Process.Start("https://dotnet.microsoft.com/en-us/download/dotnet/8.0/runtime?cid=getdotnetcore&os=windows&arch=x64");
                return;
            }

            _installButton.Enabled = false;
            _desktopShortcut.Enabled = false;
            _progress.Visible = true;
            _status.Visible = true;
            _status.Text = "Устанавливаю Picall…";
            try
            {
                var createShortcut = _desktopShortcut.Checked;
                await Task.Run(() => Program.Install(createShortcut));
                _progress.Visible = false;
                _status.Text = "Готово";
                _installButton.Text = "Открыть Picall";
                _installButton.Enabled = true;
                _installButton.Click -= InstallClicked;
                _installButton.Click += delegate { Program.Launch(); Close(); };
            }
            catch (Exception ex)
            {
                _progress.Visible = false;
                _status.Text = "Установка не завершена";
                _installButton.Enabled = true;
                _desktopShortcut.Enabled = true;
                MessageBox.Show(ex.Message, "Picall", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class LogoPanel : Panel
    {
        public LogoPanel() { DoubleBuffered = true; BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundedRectangle(new RectangleF(1, 1, Width - 2, Height - 2), 19))
            using (var brush = new LinearGradientBrush(ClientRectangle, Color.FromArgb(167, 139, 250), Color.FromArgb(109, 59, 234), 45f))
                e.Graphics.FillPath(brush, path);
            using (var white = new SolidBrush(Color.White)) e.Graphics.FillEllipse(white, 16, 15, 22, 22);
            using (var soft = new SolidBrush(Color.FromArgb(221, 214, 254))) e.Graphics.FillEllipse(soft, 45, 43, 14, 14);
            using (var white = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            {
                var points = new[] { new PointF(12, 56), new PointF(28, 39), new PointF(40, 51), new PointF(50, 40), new PointF(64, 57), new PointF(64, 63), new PointF(12, 63) };
                e.Graphics.FillPolygon(white, points);
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
