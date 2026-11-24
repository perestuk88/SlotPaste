using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace SlotPaste
{
    public partial class App : System.Windows.Application
    {
        private Forms.NotifyIcon? _tray;
        private Forms.ToolStripMenuItem? _pauseItem;   // пункт «Пауза записи» (с галочкой, когда пауза)
        private Forms.ContextMenuStrip? _menu;          // меню у значка в трее
        private SettingsWindow? _settingsWindow;
        public static bool IsExiting = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // тема включается до того, как откроется первое окно, чтобы оно сразу было нужного цвета
            Theme.Apply(Storage.LoadSettings().ThemeMode);

            _tray = new Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "SlotPaste",
                Visible = true
            };

            _pauseItem = new Forms.ToolStripMenuItem("Пауза записи");
            _pauseItem.Click += (s, a) => (MainWindow as SlotPaste.MainWindow)?.TogglePause();

            _menu = new Forms.ContextMenuStrip();
            _menu.Items.Add("Открыть", null, (s, a) => ShowMain());
            _menu.Items.Add("Настройки", null, (s, a) => ShowSettings());
            _menu.Items.Add(_pauseItem);
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add("Выход", null, (s, a) => { IsExiting = true; Shutdown(); });
            _tray.ContextMenuStrip = _menu;

            // меню трея тоже красим под тему: сейчас и каждый раз, когда тема меняется
            Theme.StyleTrayMenu(_menu);
            Theme.Changed += dark => { if (_menu != null) Theme.StyleTrayMenu(_menu); };
            _tray.DoubleClick += (s, a) => ShowMain();
        }

        private void ShowMain()
        {
            MainWindow.Show();
            MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        }

        // открывает окно настроек; если оно уже открыто, просто выводит его вперёд
        private void ShowSettings()
        {
            if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
            if (MainWindow is not SlotPaste.MainWindow main) return;

            _settingsWindow = new SettingsWindow(main);
            _settingsWindow.Closed += (s, a) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        // всплывающее уведомление у иконки в трее (warning: false — обычное, без жёлтого значка)
        public static void ShowTrayMessage(string text, bool warning = true)
        {
            if (Current is App app && app._tray != null)
                app._tray.ShowBalloonTip(5000, "SlotPaste", text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        }

        // обновляет подсказку при наведении на иконку и галочку у «Пауза записи»
        public static void UpdateTray(string tooltip, bool paused)
        {
            if (Current is not App app || app._tray == null) return;
            app._tray.Text = tooltip.Length > 120 ? tooltip[..120] : tooltip;   // Windows не даёт подсказку длиннее
            if (app._pauseItem != null) app._pauseItem.Checked = paused;
        }

        // иконка для трея: берём размер, который Windows рисует в области уведомлений, чтобы не мылить
        private static Icon LoadTrayIcon()
        {
            var stream = GetResourceStream(new Uri("pack://application:,,,/SlotPaste.ico"))?.Stream;
            if (stream == null) return SystemIcons.Application;
            using (stream)
            using (var raw = new Icon(stream, Forms.SystemInformation.SmallIconSize))
                return (Icon)raw.Clone();   // копия, чтобы поток можно было закрыть
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Theme.Stop();
            if (_tray != null) _tray.Visible = false;
            _tray?.Dispose();
            base.OnExit(e);
        }
    }
}
