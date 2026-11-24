using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;

namespace ClipApp
{
    public partial class SettingsWindow : System.Windows.Window
    {
        private readonly MainWindow _main;
        private readonly AppSettings _original;   // настройки, которые были до открытия окна

        // для каждого поля с сочетанием помним, что в нём сейчас выбрано: модификаторы и клавишу
        private readonly Dictionary<TextBox, (uint Mods, uint Key)> _combos = new();

        public SettingsWindow(MainWindow main)
        {
            InitializeComponent();
            Theme.Attach(this);   // тёмный/светлый заголовок окна под тему
            _main = main;
            _original = main.Settings;

            CopyModsBox.ItemsSource = Hotkeys.SlotModifierOptions;
            PasteModsBox.ItemsSource = Hotkeys.SlotModifierOptions;
            ShowSettings(_original);

            // пока открыты настройки, хоткеи выключены: иначе Windows перехватит сочетание и поле его не увидит
            _main.UnregisterHotkeys();
            Closed += (s, a) => { if (!App.IsExiting) _main.RegisterHotkeys(); };

            // тему в окне можно «примерить»; при закрытии возвращаем ту, что в настройках
            // (после «Сохранить» там уже новая, после «Отмена» — прежняя)
            Closed += (s, a) => Theme.Apply(_main.Settings.ThemeMode);

            // где лежат данные и включён ли портативный режим
            DataFolderBox.Text = Storage.Folder;
            PortableText.Text = Storage.IsPortable
                ? "Портативный режим: всё хранится в папке Data рядом с SlotPaste.exe, программу можно носить на флешке."
                : "Чтобы программа стала портативной (например, для флешки), положи рядом с SlotPaste.exe пустой файл portable.txt " +
                  "и перезапусти её. Тогда история и настройки будут храниться в папке Data рядом с exe.";
        }

        // запоминает сочетание для поля и показывает его текстом
        private void SetCombo(TextBox box, uint mods, uint key)
        {
            _combos[box] = (mods, key);
            box.Text = Hotkeys.ToText(mods, key);
        }

        // раскладывает настройки по полям окна
        private void ShowSettings(AppSettings s)
        {
            SetCombo(PopupBox, s.PopupModifiers, s.PopupKey);
            SetCombo(PinBox, s.PinModifiers, s.PinKey);
            SetCombo(LayoutBox, s.LayoutModifiers, s.LayoutKey);
            SetCombo(QueueBox, s.QueueModifiers, s.QueueKey);
            SetCombo(QueueNextBox, s.QueueNextModifiers, s.QueueNextKey);
            SetCombo(PauseBox, s.PauseModifiers, s.PauseKey);
            SetCombo(NextSetBox, s.NextSetModifiers, s.NextSetKey);
            CopyModsBox.SelectedItem = Hotkeys.FindOption(s.CopyModifiers);
            PasteModsBox.SelectedItem = Hotkeys.FindOption(s.PasteModifiers);
            DaysBox.Text = s.HistoryDays.ToString();
            MaxBox.Text = s.MaxHistory.ToString();
            IgnoredBox.Text = s.IgnoredProcesses;
            ThemeBox.SelectedIndex = Math.Clamp(s.ThemeMode, Theme.ModeSystem, Theme.ModeDark);
            ErrorText.Text = "";
        }

        // ловим сочетание, которое нажали в одном из полей (sender подсказывает, в каком именно)
        private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not TextBox box) return;

            // с зажатым Alt WPF присылает Key.System, а настоящая клавиша лежит в SystemKey
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            // одиночные Ctrl, Shift, Alt и Win не считаем, ждём основную клавишу
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            {
                e.Handled = true;
                return;
            }

            uint mods = Hotkeys.CurrentMods();

            // голые Tab и Esc работают как обычно: переход по полям и закрытие окна
            if (mods == 0 && (key == Key.Tab || key == Key.Escape)) return;

            e.Handled = true;
            if (!Hotkeys.HasCtrlOrAlt(mods))
            {
                ErrorText.Text = "В сочетании должен быть Ctrl или Alt, иначе оно будет мешать обычному набору текста.";
                return;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

            // Ctrl+A/C/V/X/Z/Y нужны для работы с текстом (в поле поиска и в других программах), их не отдаём
            if (mods == Hotkeys.MOD_CONTROL && "ACVXZY".Contains((char)vk))
            {
                ErrorText.Text = "Это сочетание нужно для работы с текстом, выбери другое.";
                return;
            }

            SetCombo(box, mods, vk);
            ErrorText.Text = "";
        }

        private void Defaults_Click(object sender, RoutedEventArgs e) => ShowSettings(new AppSettings());

        // выбрали тему в списке: сразу показываем, как она выглядит (сохранится только по «Сохранить»)
        private void ThemeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ThemeBox.SelectedIndex >= 0) Theme.Apply(ThemeBox.SelectedIndex);
        }

        // кнопка «Открыть папку»: показывает в проводнике папку с data.json и settings.json
        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Storage.Folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Storage.Folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ErrorText.Text = "Не удалось открыть папку: " + ex.Message;
            }
        }

        // проверяет, что сочетания не мешают друг другу; возвращает текст ошибки или null, если всё хорошо
        private string? FindConflict(uint copyMods, uint pasteMods)
        {
            if (copyMods == pasteMods) return "«Положить» и «Вставить» не могут быть одинаковыми.";

            var boxes = _combos.Keys.ToList();
            for (int i = 0; i < boxes.Count; i++)
            {
                var (mods, key) = _combos[boxes[i]];
                string name = (string)boxes[i].Tag;

                // цифра 1–9 с теми же модификаторами, что у ячеек
                bool isDigit = key >= 0x31 && key <= 0x39;
                if (isDigit && (mods == copyMods || mods == pasteMods))
                    return $"«{name}» совпадает с сочетанием для ячеек.";

                // два поля с одинаковым сочетанием
                for (int j = i + 1; j < boxes.Count; j++)
                    if (_combos[boxes[j]] == (mods, key))
                        return $"«{name}» и «{boxes[j].Tag}» не могут быть одинаковыми.";
            }
            return null;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (CopyModsBox.SelectedItem is not ModOption copy || PasteModsBox.SelectedItem is not ModOption paste) return;

            string? conflict = FindConflict(copy.Mods, paste.Mods);
            if (conflict != null) { ErrorText.Text = conflict; return; }

            if (!int.TryParse(DaysBox.Text.Trim(), out int days) || days < 0 || days > 3650)
            {
                ErrorText.Text = "Дней хранения: целое число от 0 до 3650 (0 = хранить всегда).";
                return;
            }
            if (!int.TryParse(MaxBox.Text.Trim(), out int max) || max < 10 || max > 10000)
            {
                ErrorText.Text = "Сколько записей хранить: целое число от 10 до 10000.";
                return;
            }

            var settings = new AppSettings
            {
                PopupModifiers = _combos[PopupBox].Mods,
                PopupKey = _combos[PopupBox].Key,
                PinModifiers = _combos[PinBox].Mods,
                PinKey = _combos[PinBox].Key,
                LayoutModifiers = _combos[LayoutBox].Mods,
                LayoutKey = _combos[LayoutBox].Key,
                QueueModifiers = _combos[QueueBox].Mods,
                QueueKey = _combos[QueueBox].Key,
                QueueNextModifiers = _combos[QueueNextBox].Mods,
                QueueNextKey = _combos[QueueNextBox].Key,
                PauseModifiers = _combos[PauseBox].Mods,
                PauseKey = _combos[PauseBox].Key,
                NextSetModifiers = _combos[NextSetBox].Mods,
                NextSetKey = _combos[NextSetBox].Key,
                CopyModifiers = copy.Mods,
                PasteModifiers = paste.Mods,
                HistoryDays = days,
                MaxHistory = max,
                IgnoredProcesses = IgnoredBox.Text.Trim(),
                ThemeMode = Math.Max(ThemeBox.SelectedIndex, Theme.ModeSystem)
            };

            var failed = _main.ApplySettings(settings);
            if (failed.Count == 0)
            {
                Close();
                return;
            }

            // что-то занято другой программой: возвращаем старые настройки и показываем, что именно
            _main.ApplySettings(_original);
            _main.UnregisterHotkeys();
            ErrorText.Text = "Не удалось занять: " + string.Join(", ", failed) +
                             ". Скорее всего, их уже использует другая программа. Выбери другие сочетания.";
        }
    }
}
