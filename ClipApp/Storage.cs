using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipApp
{
    // Один набор ячеек 1–9 (например «Основной» или «Работа»)
    public class SlotSet
    {
        public string Name { get; set; } = "Основной";
        public string?[] Slots { get; set; } = new string?[10];   // [0] не используем, ячейки 1–9

        public override string ToString() => Name;   // так набор выглядит в выпадающем списке
    }

    // То, что сохраняем в data.json: история, наборы ячеек и закреплённые
    public class SavedData
    {
        public List<string> History { get; set; } = new();
        public List<string> Pinned { get; set; } = new();   // закреплённые записи

        // когда каждая запись истории попала в буфер (нужно для автоудаления старых)
        public Dictionary<string, DateTime> HistoryTimes { get; set; } = new();

        public List<SlotSet> SlotSets { get; set; } = new();   // наборы ячеек
        public int CurrentSet { get; set; } = 0;               // какой набор сейчас выбран

        // старый формат (до урока 13), когда набор ячеек был один.
        // Читаем его только один раз, чтобы перенести в набор «Основной», и больше не записываем.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string?[]? Slots { get; set; }
    }

    // То, что сохраняем в settings.json. Если в старом файле какого-то поля нет,
    // берётся значение отсюда, поэтому старые настройки спокойно читаются.
    public class AppSettings
    {
        private const uint CtrlAlt = Hotkeys.MOD_CONTROL | Hotkeys.MOD_ALT;

        public uint PopupModifiers { get; set; } = Hotkeys.MOD_CONTROL | Hotkeys.MOD_SHIFT;
        public uint PopupKey { get; set; } = 0x56;   // V
        public uint CopyModifiers { get; set; } = Hotkeys.MOD_CONTROL;
        public uint PasteModifiers { get; set; } = Hotkeys.MOD_ALT;
        public uint PinModifiers { get; set; } = Hotkeys.MOD_CONTROL;   // закрепление работает только в окне программы
        public uint PinKey { get; set; } = 0x50;   // P

        // исправить раскладку выделенного текста: Ctrl+Alt+R («Раскладка»)
        public uint LayoutModifiers { get; set; } = CtrlAlt;
        public uint LayoutKey { get; set; } = 0x52;   // R
        // включить / выключить режим очереди: Ctrl+Alt+Q («Queue»)
        public uint QueueModifiers { get; set; } = CtrlAlt;
        public uint QueueKey { get; set; } = 0x51;   // Q
        // вставить следующий из очереди: Ctrl+Alt+V (занимаем его, только пока очередь включена)
        public uint QueueNextModifiers { get; set; } = CtrlAlt;
        public uint QueueNextKey { get; set; } = 0x56;   // V
        // пауза записи истории: Ctrl+Alt+P («Pause»)
        public uint PauseModifiers { get; set; } = CtrlAlt;
        public uint PauseKey { get; set; } = 0x50;   // P
        // следующий набор ячеек: Ctrl+Alt+S («Set»)
        public uint NextSetModifiers { get; set; } = CtrlAlt;
        public uint NextSetKey { get; set; } = 0x53;   // S

        // приватность
        public int HistoryDays { get; set; } = 0;     // сколько дней хранить историю, 0 = всегда
        public int MaxHistory { get; set; } = 100;    // сколько записей хранить максимум
        public string IgnoredProcesses { get; set; } = "KeePass.exe, KeePassXC.exe, 1Password.exe, Bitwarden.exe";

        // тема: 0 = как в Windows, 1 = светлая, 2 = тёмная (см. Theme.cs)
        public int ThemeMode { get; set; } = Theme.ModeSystem;

        // защита от кривого файла настроек
        public bool LooksValid() =>
            PopupKey != 0 && Hotkeys.HasCtrlOrAlt(PopupModifiers) &&
            PinKey != 0 && Hotkeys.HasCtrlOrAlt(PinModifiers) &&
            LayoutKey != 0 && Hotkeys.HasCtrlOrAlt(LayoutModifiers) &&
            QueueKey != 0 && Hotkeys.HasCtrlOrAlt(QueueModifiers) &&
            QueueNextKey != 0 && Hotkeys.HasCtrlOrAlt(QueueNextModifiers) &&
            PauseKey != 0 && Hotkeys.HasCtrlOrAlt(PauseModifiers) &&
            NextSetKey != 0 && Hotkeys.HasCtrlOrAlt(NextSetModifiers) &&
            Hotkeys.HasCtrlOrAlt(CopyModifiers) &&
            Hotkeys.HasCtrlOrAlt(PasteModifiers) &&
            CopyModifiers != PasteModifiers &&
            HistoryDays >= 0 && MaxHistory >= 10;
    }

    public static class Storage
    {
        // Где лежат data.json и settings.json:
        //  - обычно в %AppData%\SlotPaste;
        //  - в портативном режиме — в папке Data рядом с SlotPaste.exe.
        // Портативный режим включается сам, если рядом с exe лежит файл portable.txt (или уже есть папка Data).
        public static readonly bool IsPortable;
        public static readonly string Folder;
        private static readonly string DataPath;
        private static readonly string SettingsPath;

        private static readonly string OldFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClipApp");

        static Storage()
        {
            // AppContext.BaseDirectory — папка, где лежит SlotPaste.exe (работает и для exe «одним файлом»)
            string exeFolder = AppContext.BaseDirectory;
            string portableFolder = Path.Combine(exeFolder, "Data");
            IsPortable = File.Exists(Path.Combine(exeFolder, "portable.txt")) || Directory.Exists(portableFolder);

            Folder = IsPortable
                ? portableFolder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SlotPaste");
            DataPath = Path.Combine(Folder, "data.json");
            SettingsPath = Path.Combine(Folder, "settings.json");

            // перенос из старой папки ClipApp нужен только в обычном режиме
            if (!IsPortable) TryMigrateFromClipApp();
        }

        // при первом запуске под новым именем копируем данные из старой папки ClipApp (не удаляем её)
        private static void TryMigrateFromClipApp()
        {
            try
            {
                if (Directory.Exists(Folder) || !Directory.Exists(OldFolder)) return;
                Directory.CreateDirectory(Folder);
                foreach (string name in new[] { "data.json", "settings.json" })
                {
                    string src = Path.Combine(OldFolder, name);
                    string dst = Path.Combine(Folder, name);
                    if (File.Exists(src) && !File.Exists(dst))
                        File.Copy(src, dst);
                }
            }
            catch { }
        }

        public static SavedData Load() => LoadJson<SavedData>(DataPath);
        public static void Save(SavedData data) => SaveJson(DataPath, data);

        public static AppSettings LoadSettings()
        {
            var settings = LoadJson<AppSettings>(SettingsPath);
            return settings.LooksValid() ? settings : new AppSettings();
        }

        public static void SaveSettings(AppSettings settings) => SaveJson(SettingsPath, settings);

        // <T> значит «любой тип»: одним методом читаем и историю, и настройки.
        // Если файла нет или он битый, возвращаем новый объект со значениями по умолчанию.
        private static T LoadJson<T>(string path) where T : new()
        {
            try
            {
                if (!File.Exists(path)) return new T();
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
            }
            catch { return new T(); }
        }

        private static void SaveJson<T>(string path, T value)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }
    }
}
