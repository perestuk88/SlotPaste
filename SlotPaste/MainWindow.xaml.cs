using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;

namespace SlotPaste
{
    public partial class MainWindow : System.Windows.Window
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private const int WM_HOTKEY = 0x0312;

        // номера хоткеев (ячейки занимают 1–9 и 11–19, остальные идут с сотни)
        private const int POPUP_ID = 100, LAYOUT_ID = 101, QUEUE_ID = 102, QUEUE_NEXT_ID = 103, PAUSE_ID = 104, NEXT_SET_ID = 105;

        // номера вкладок по порядку
        private const int TAB_HISTORY = 0, TAB_PINNED = 1, TAB_SLOTS = 2;

        // коды клавиш, которые программа «нажимает» сама
        private const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_MASK = 0xE8;
        private const byte KEY_C = 0x43, KEY_V = 0x56;
        private const uint KEYUP = 0x0002;

        [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        public ObservableCollection<string> History { get; } = new();
        public ObservableCollection<string> Pinned { get; } = new();         // закреплённые записи
        public ObservableCollection<SlotItem> SlotItems { get; } = new();    // то, что видно во вкладке «Ячейки»
        public ObservableCollection<SlotSet> SlotSets { get; } = new();      // наборы ячеек («Основной», «Работа»…)
        public AppSettings Settings { get; private set; } = Storage.LoadSettings();   // текущие настройки

        private int _currentSet = 0;                                  // номер выбранного набора
        private string?[] CurrentSlots => SlotSets[_currentSet].Slots;   // ячейки выбранного набора
        private Dictionary<string, DateTime> _historyTimes = new();  // когда каждая запись истории попала в буфер

        private int _pendingSlot = 0;
        private IntPtr _hwnd;
        private IntPtr _prevWindow;
        private ICollectionView _historyView = null!;   // «взгляд» на историю через фильтр поиска
        private readonly System.Windows.Controls.ContextMenu _transformMenu;   // меню «вставить как…»

        private bool _paused = false;                       // пауза записи истории
        private bool _queueOn = false;                      // включён режим очереди
        private readonly Queue<string> _queue = new();      // очередь: первым скопировал, первым вставится
        private readonly HashSet<string> _ignoreOnce = new();   // тексты, которые программа кладёт в буфер сама
        private bool _waitingForLayoutText = false;         // ждём выделенный текст для исправления раскладки
        private string? _layoutText;

        public MainWindow()
        {
            InitializeComponent();
            Theme.Attach(this);   // тёмный/светлый заголовок окна под тему
            HistoryList.ItemsSource = History;

            // список показывает историю не напрямую, а через «вид» с фильтром:
            // сама History не меняется, просто на экран попадают только подходящие строки
            _historyView = CollectionViewSource.GetDefaultView(History);
            _historyView.Filter = MatchesSearch;
            PinnedList.ItemsSource = Pinned;
            SlotsList.ItemsSource = SlotItems;
            SetBox.ItemsSource = SlotSets;

            _transformMenu = (System.Windows.Controls.ContextMenu)FindResource("TransformMenu");
            // после открытия по Shift+Enter возвращаем меню обычное поведение (появляться у мышки)
            _transformMenu.Closed += (s, e) => _transformMenu.ClearValue(System.Windows.Controls.ContextMenu.PlacementProperty);

            var data = Storage.Load();
            foreach (var item in data.History) History.Add(item);
            foreach (var item in data.Pinned) Pinned.Add(item);

            // время записей: у старых записей его нет, считаем, что они скопированы сейчас
            foreach (var item in History)
                _historyTimes[item] = data.HistoryTimes.TryGetValue(item, out var time) ? time : DateTime.Now;

            // наборы ячеек; если файл старый (один набор в Slots), переносим его в «Основной»
            foreach (var set in data.SlotSets) SlotSets.Add(set);
            if (SlotSets.Count == 0) SlotSets.Add(new SlotSet { Name = "Основной", Slots = data.Slots ?? new string?[10] });
            foreach (var set in SlotSets)
            {
                if (set.Slots == null) set.Slots = new string?[10];
                if (set.Slots.Length < 10) { var slots = set.Slots; Array.Resize(ref slots, 10); set.Slots = slots; }
            }
            _currentSet = Math.Clamp(data.CurrentSet, 0, SlotSets.Count - 1);
            SetBox.SelectedIndex = _currentSet;

            // заполняем вкладку «Ячейки» строчками 1–9
            for (int n = 1; n <= 9; n++) SlotItems.Add(MakeSlotItem(n));

            // старые записи истории удаляем при запуске и потом раз в час
            CleanupHistory();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            timer.Tick += (s, e) => CleanupHistory();
            timer.Start();
        }

        private void SaveAll()
        {
            // сохраняем время только для тех записей, что ещё есть в истории
            var times = new Dictionary<string, DateTime>();
            foreach (string text in History)
                times[text] = _historyTimes.TryGetValue(text, out var time) ? time : DateTime.Now;
            _historyTimes = times;

            Storage.Save(new SavedData
            {
                History = History.ToList(),
                Pinned = Pinned.ToList(),
                HistoryTimes = times,
                SlotSets = SlotSets.ToList(),
                CurrentSet = _currentSet
            });
        }

        // собирает строчку для вкладки «Ячейки» из ячейки номер n
        private SlotItem MakeSlotItem(int n) => new SlotItem { Number = n, Text = CurrentSlots[n] ?? "" };

        // обновляет строчку ячейки n в окне после того, как её содержимое поменялось
        private void RefreshSlot(int n) => SlotItems[n - 1] = MakeSlotItem(n);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
            AddClipboardFormatListener(_hwnd);

            var failed = RegisterHotkeys();
            if (failed.Count > 0)
                App.ShowTrayMessage("Не удалось занять сочетания: " + string.Join(", ", failed) + ". Поменяй их в «Настройках».");
            UpdateStatus();
        }

        // занимает один хоткей; если не вышло, дописывает его в список неудачных
        private void TryRegister(int id, uint mods, uint key, List<string> failed)
        {
            if (!RegisterHotKey(_hwnd, id, mods | Hotkeys.MOD_NOREPEAT, key))
                failed.Add(Hotkeys.ToText(mods, key));
        }

        // занимает все хоткеи по текущим настройкам и возвращает те, что занять не вышло
        public List<string> RegisterHotkeys()
        {
            UnregisterHotkeys();
            var failed = new List<string>();

            TryRegister(POPUP_ID, Settings.PopupModifiers, Settings.PopupKey, failed);
            TryRegister(LAYOUT_ID, Settings.LayoutModifiers, Settings.LayoutKey, failed);
            TryRegister(QUEUE_ID, Settings.QueueModifiers, Settings.QueueKey, failed);
            TryRegister(PAUSE_ID, Settings.PauseModifiers, Settings.PauseKey, failed);
            TryRegister(NEXT_SET_ID, Settings.NextSetModifiers, Settings.NextSetKey, failed);
            // «вставить следующий» занимаем, только пока очередь включена, чтобы в остальное время не мешать другим программам
            if (_queueOn) TryRegister(QUEUE_NEXT_ID, Settings.QueueNextModifiers, Settings.QueueNextKey, failed);

            for (int i = 1; i <= 9; i++)
            {
                uint key = (uint)(0x30 + i);
                TryRegister(i, Settings.CopyModifiers, key, failed);
                TryRegister(10 + i, Settings.PasteModifiers, key, failed);
            }

            UpdateHints();
            return failed;
        }

        // освобождает все хоткеи
        public void UnregisterHotkeys()
        {
            foreach (int id in new[] { POPUP_ID, LAYOUT_ID, QUEUE_ID, QUEUE_NEXT_ID, PAUSE_ID, NEXT_SET_ID })
                UnregisterHotKey(_hwnd, id);
            for (int i = 1; i <= 9; i++) { UnregisterHotKey(_hwnd, i); UnregisterHotKey(_hwnd, 10 + i); }
        }

        // подсказки во вкладках «Ячейки» и «Закреплённые» под текущие настройки
        private void UpdateHints()
        {
            SlotsHint.Text = $"{Hotkeys.ModsToText(Settings.CopyModifiers)}+цифра кладёт выделенное в ячейку, " +
                             $"{Hotkeys.ModsToText(Settings.PasteModifiers)}+цифра вставляет её. Delete очищает ячейку. " +
                             $"{Hotkeys.ToText(Settings.NextSetModifiers, Settings.NextSetKey)} переключает набор.";
            string pin = Hotkeys.ToText(Settings.PinModifiers, Settings.PinKey);
            PinnedHint.Text = $"{pin} в «Истории» закрепляет запись. Здесь {pin} или Delete убирает её из закреплённых.";
        }

        // применяет новые настройки: запоминает, сохраняет в файл и заново занимает хоткеи
        public List<string> ApplySettings(AppSettings settings)
        {
            Settings = settings;
            Storage.SaveSettings(settings);
            Theme.Apply(settings.ThemeMode);   // тема меняется сразу, без перезапуска
            CleanupHistory();   // вдруг уменьшили срок хранения или размер истории
            return RegisterHotkeys();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                OnClipboardChanged();
            }
            else if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == POPUP_ID) ShowPopup();
                else if (id == LAYOUT_ID) FixLayoutInActiveApp();
                else if (id == QUEUE_ID) ToggleQueue();
                else if (id == QUEUE_NEXT_ID) PasteNextFromQueue();
                else if (id == PAUSE_ID) TogglePause();
                else if (id == NEXT_SET_ID) NextSet();
                else if (id >= 1 && id <= 9) CopyToSlot(id);
                else if (id >= 11 && id <= 19) PasteFromSlot(id - 10);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void OnClipboardChanged()
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsText()) return;
                string text = System.Windows.Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;

                // это программа сама положила текст в буфер (исправленная раскладка, очередь…), второй раз не записываем
                if (_ignoreOnce.Remove(text)) return;

                // ждём выделенный текст для исправления раскладки: забираем его, в историю он не идёт
                if (_waitingForLayoutText)
                {
                    _layoutText = text;
                    _waitingForLayoutText = false;
                    return;
                }

                // «положить в ячейку» работает всегда, даже на паузе: это ты попросил явно
                bool toSlot = _pendingSlot > 0;
                if (toSlot)
                {
                    CurrentSlots[_pendingSlot] = text;
                    RefreshSlot(_pendingSlot);   // сразу показываем новое содержимое во вкладке «Ячейки»
                    _pendingSlot = 0;
                    SaveAll();
                }

                // пауза или пароль из менеджера паролей: ничего не записываем
                if (_paused || Privacy.ShouldIgnore(Settings.IgnoredProcesses)) return;

                if (_queueOn && !toSlot)
                {
                    _queue.Enqueue(text);
                    UpdateStatus();
                }
                AddToHistory(text);
            }
            catch { }
        }

        // ставит текст первым в истории (если он там уже был, переносит наверх)
        private void AddToHistory(string text)
        {
            History.Remove(text);
            History.Insert(0, text);
            _historyTimes[text] = DateTime.Now;
            while (History.Count > Settings.MaxHistory) History.RemoveAt(History.Count - 1);
            SaveAll();
        }

        // удаляет из истории записи старше N дней и лишние сверх лимита (закреплённые и ячейки не трогает)
        private void CleanupHistory()
        {
            int before = History.Count;
            if (Settings.HistoryDays > 0)
            {
                DateTime border = DateTime.Now.AddDays(-Settings.HistoryDays);
                foreach (string text in History.Where(t => _historyTimes.TryGetValue(t, out var time) && time < border).ToList())
                    History.Remove(text);
            }
            while (History.Count > Settings.MaxHistory) History.RemoveAt(History.Count - 1);
            if (History.Count != before) SaveAll();
        }

        // запоминает, что следующее появление этого текста в буфере записывать не надо (через 2 секунды забываем)
        private async void IgnoreOnce(string text)
        {
            _ignoreOnce.Add(text);
            await Task.Delay(2000);
            _ignoreOnce.Remove(text);
        }

        // кладёт текст в буфер «тихо»: ни в историю, ни в очередь он из-за этого не попадёт
        private bool PutOnClipboard(string text)
        {
            try
            {
                IgnoreOnce(text);
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch { return false; }
        }

        private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        // Жмёт Ctrl+key. Shift и Alt, которые ты держишь, на миг «отпускаем», чтобы они не мешали,
        // а Ctrl, если он уже зажат, используем как есть. Поэтому отпускать клавиши руками не нужно.
        private static void SendCtrlCombo(byte key)
        {
            keybd_event(VK_MASK, 0, 0, UIntPtr.Zero);       // «пустая» клавиша, чтобы отпускание Alt не открыло меню окна
            keybd_event(VK_MASK, 0, KEYUP, UIntPtr.Zero);
            if (IsDown(VK_MENU)) keybd_event(VK_MENU, 0, KEYUP, UIntPtr.Zero);
            if (IsDown(VK_SHIFT)) keybd_event(VK_SHIFT, 0, KEYUP, UIntPtr.Zero);

            bool ctrlHeld = IsDown(VK_CONTROL);
            if (!ctrlHeld) keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KEYUP, UIntPtr.Zero);
            if (!ctrlHeld) keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
        }

        // «Положить в ячейку»: запоминаем номер и жмём Ctrl+C, текст поймает OnClipboardChanged
        private void CopyToSlot(int n)
        {
            _pendingSlot = n;
            SendCtrlCombo(KEY_C);
        }

        // «Вставить из ячейки»: кладём текст в буфер и жмём Ctrl+V
        private async void PasteFromSlot(int n)
        {
            string? text = CurrentSlots[n];
            if (string.IsNullOrEmpty(text)) return;

            if (!_paused) AddToHistory(text);
            if (!PutOnClipboard(text)) return;
            await Task.Delay(30);
            SendCtrlCombo(KEY_V);
        }

        // ===== Исправление раскладки =====

        // копирует выделенный текст в активной программе, переводит «ghbdtn» → «привет» и вставляет обратно
        private async void FixLayoutInActiveApp()
        {
            _layoutText = null;
            _waitingForLayoutText = true;
            SendCtrlCombo(KEY_C);

            // ждём до 0,6 секунды, пока программа положит выделенное в буфер
            for (int i = 0; i < 20 && _layoutText == null; i++) await Task.Delay(30);
            _waitingForLayoutText = false;

            string? text = _layoutText;
            if (string.IsNullOrEmpty(text))
            {
                System.Media.SystemSounds.Beep.Play();   // ничего не выделено: просто пискнем
                return;
            }

            string fixedText = TextTools.FixLayout(text);
            if (fixedText == text || !PutOnClipboard(fixedText)) return;
            await Task.Delay(30);
            SendCtrlCombo(KEY_V);   // вставка заменит выделенный текст исправленным
        }

        // ===== Очередь =====

        private string QueueNextText => Hotkeys.ToText(Settings.QueueNextModifiers, Settings.QueueNextKey);

        // хоткей «очередь»: включает или выключает режим очереди
        private void ToggleQueue()
        {
            if (_queueOn)
                SetQueue(false, "Режим очереди выключен.");
            else
                SetQueue(true, $"Режим очереди включён. Копируй всё нужное по порядку, потом вставляй по одному: {QueueNextText}.");
        }

        private void SetQueue(bool on, string message)
        {
            _queue.Clear();
            UnregisterHotKey(_hwnd, QUEUE_NEXT_ID);
            if (on && !RegisterHotKey(_hwnd, QUEUE_NEXT_ID, Settings.QueueNextModifiers | Hotkeys.MOD_NOREPEAT, Settings.QueueNextKey))
            {
                App.ShowTrayMessage($"Не удалось занять {QueueNextText}, его использует другая программа. Поменяй его в «Настройках».");
                on = false;
                message = "";
            }
            _queueOn = on;
            UpdateStatus();
            if (message.Length > 0) App.ShowTrayMessage(message, false);
        }

        // вставляет самый первый текст из очереди и убирает его оттуда
        private async void PasteNextFromQueue()
        {
            if (!_queueOn) return;
            if (_queue.Count == 0)
            {
                SetQueue(false, "Очередь пуста, режим очереди выключен.");
                return;
            }

            string text = _queue.Dequeue();
            UpdateStatus();
            if (!PutOnClipboard(text)) return;
            await Task.Delay(30);
            SendCtrlCombo(KEY_V);

            if (_queue.Count == 0) SetQueue(false, "Очередь закончилась, режим очереди выключен.");
        }

        // ===== Пауза =====

        // пауза записи: хоткей или пункт «Пауза записи» в меню трея
        public void TogglePause()
        {
            _paused = !_paused;
            UpdateStatus();
            string combo = Hotkeys.ToText(Settings.PauseModifiers, Settings.PauseKey);
            App.ShowTrayMessage(_paused ? $"Запись истории на паузе. Продолжить: {combo} или «Пауза записи» в трее."
                                        : "Запись истории снова включена.", false);
        }

        // показывает, что включено: в заголовке окна, в строке внизу окна и в подсказке у иконки в трее
        private void UpdateStatus()
        {
            var shortParts = new List<string>();
            var longParts = new List<string>();
            if (_paused)
            {
                shortParts.Add("пауза");
                longParts.Add($"⏸ Запись истории на паузе ({Hotkeys.ToText(Settings.PauseModifiers, Settings.PauseKey)} — продолжить)");
            }
            if (_queueOn)
            {
                shortParts.Add($"очередь: {_queue.Count}");
                longParts.Add($"Очередь: {_queue.Count} шт., вставлять по одному: {QueueNextText}");
            }

            string status = string.Join(", ", shortParts);
            Title = status.Length == 0 ? "SlotPaste" : $"SlotPaste ({status})";
            StatusText.Text = string.Join(Environment.NewLine, longParts);
            StatusText.Visibility = longParts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            App.UpdateTray(status.Length == 0 ? "SlotPaste" : $"SlotPaste: {status}", _paused);
        }

        // ===== Наборы ячеек =====

        // переключается на набор номер index и показывает его ячейки
        private void SwitchSet(int index)
        {
            _currentSet = index;
            for (int n = 1; n <= 9; n++) RefreshSlot(n);
            if (SetBox.SelectedIndex != index) SetBox.SelectedIndex = index;
            SaveAll();
        }

        // хоткей «следующий набор»: по кругу
        private void NextSet()
        {
            SwitchSet((_currentSet + 1) % SlotSets.Count);
            App.ShowTrayMessage($"Набор ячеек: {SlotSets[_currentSet].Name}", false);
        }

        // выбрали набор в выпадающем списке
        private void SetBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SetBox.SelectedIndex < 0 || SetBox.SelectedIndex == _currentSet) return;
            SwitchSet(SetBox.SelectedIndex);
            FocusList(SlotsList);
        }

        private void AddSet_Click(object sender, RoutedEventArgs e)
        {
            string? name = InputDialog.Ask(this, "Новый набор", "Название набора:");
            if (string.IsNullOrEmpty(name)) return;
            SlotSets.Add(new SlotSet { Name = name });
            SwitchSet(SlotSets.Count - 1);
            FocusList(SlotsList);
        }

        private void RenameSet_Click(object sender, RoutedEventArgs e)
        {
            var set = SlotSets[_currentSet];
            string? name = InputDialog.Ask(this, "Переименовать набор", "Новое название:", set.Name);
            if (string.IsNullOrEmpty(name)) return;
            // заменяем набор новым с тем же содержимым: так выпадающий список точно покажет новое имя
            SlotSets[_currentSet] = new SlotSet { Name = name, Slots = set.Slots };
            SetBox.SelectedIndex = _currentSet;
            SaveAll();
        }

        private void DeleteSet_Click(object sender, RoutedEventArgs e)
        {
            if (SlotSets.Count == 1)
            {
                System.Windows.MessageBox.Show(this, "Последний набор удалить нельзя.", "SlotPaste",
                                               MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var answer = System.Windows.MessageBox.Show(this, $"Удалить набор «{SlotSets[_currentSet].Name}» вместе с его ячейками?",
                                                        "SlotPaste", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            int index = _currentSet;
            _currentSet = 0;   // на время удаления встаём на первый набор, чтобы никто не обратился к удаляемому
            SlotSets.RemoveAt(index);
            SwitchSet(Math.Min(index, SlotSets.Count - 1));
            FocusList(SlotsList);
        }

        // ===== Окно =====

        private void ShowPopup()
        {
            _prevWindow = GetForegroundWindow();
            var mouse = System.Windows.Forms.Cursor.Position;
            Left = mouse.X;
            Top = mouse.Y;
            Show();
            WindowState = WindowState.Normal;
            Activate();

            // по хоткею всегда открываемся на «Истории» с пустым поиском, курсор в поле поиска
            SearchBox.Text = "";
            Tabs.SelectedIndex = TAB_HISTORY;
            if (HistoryList.Items.Count > 0) HistoryList.SelectedIndex = 0;
            Dispatcher.InvokeAsync(() => SearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // подходит ли строка истории под то, что набрано в поиске (без учёта регистра)
        private bool MatchesSearch(object item)
        {
            string query = SearchBox.Text;
            return query.Length == 0 || ((string)item).Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        // набрали что-то в поиске: перефильтровываем список и встаём на первую найденную строку
        private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _historyView.Refresh();
            if (HistoryList.Items.Count > 0)
            {
                HistoryList.SelectedIndex = 0;
                HistoryList.ScrollIntoView(HistoryList.SelectedItem);
            }
        }

        // стрелки в поле поиска двигают выделение в списке, а курсор остаётся в поле, можно печатать дальше
        private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            int count = HistoryList.Items.Count;
            if (count == 0) return;
            if (e.Key == Key.Down)
            {
                HistoryList.SelectedIndex = Math.Min(HistoryList.SelectedIndex + 1, count - 1);
                HistoryList.ScrollIntoView(HistoryList.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                HistoryList.SelectedIndex = Math.Max(HistoryList.SelectedIndex - 1, 0);
                HistoryList.ScrollIntoView(HistoryList.SelectedItem);
                e.Handled = true;
            }
        }

        // ставит клавиатурный фокус на выбранную строчку списка, чтобы сразу работали стрелки и Enter
        private static void FocusList(System.Windows.Controls.ListBox list)
        {
            if (list.Items.Count == 0) { list.Focus(); return; }
            if (list.SelectedIndex < 0) list.SelectedIndex = 0;
            list.UpdateLayout();
            (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as UIElement)?.Focus();
        }

        // список на открытой вкладке
        private System.Windows.Controls.ListBox CurrentList() => Tabs.SelectedIndex switch
        {
            TAB_HISTORY => HistoryList,
            TAB_PINNED => PinnedList,
            _ => SlotsList
        };

        // переключили вкладку (мышкой или Ctrl+Tab): переносим фокус в её список
        private void Tabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != Tabs || !IsVisible) return;
            var list = CurrentList();
            Dispatcher.InvokeAsync(() => FocusList(list), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // текст выбранной строчки на открытой вкладке
        private string? GetSelectedText()
        {
            if (Tabs.SelectedIndex == TAB_HISTORY) return HistoryList.SelectedItem as string;
            if (Tabs.SelectedIndex == TAB_PINNED) return PinnedList.SelectedItem as string;
            if (SlotsList.SelectedItem is SlotItem slot && !slot.IsEmpty) return slot.Text;
            return null;
        }

        // прячет окно, возвращает фокус в программу, где был курсор, и вставляет туда text
        private async void PasteText(string text, bool addToHistory)
        {
            if (addToHistory && !_paused) AddToHistory(text);
            if (!PutOnClipboard(text)) return;
            Hide();
            SetForegroundWindow(_prevWindow);
            await Task.Delay(100);
            SendCtrlCombo(KEY_V);
        }

        private void PasteSelected()
        {
            string? text = GetSelectedText();
            if (!string.IsNullOrEmpty(text)) PasteText(text, true);
        }

        // пункт меню «вставить как…»: в Tag у пункта лежит, что сделать с текстом
        private void Transform_Click(object sender, RoutedEventArgs e)
        {
            string? text = GetSelectedText();
            if (string.IsNullOrEmpty(text) || sender is not System.Windows.Controls.MenuItem item) return;
            PasteText(TextTools.Apply(item.Tag as string ?? "plain", text), false);
        }

        // Shift+Enter: открывает то же меню прямо под выбранной строчкой
        private void OpenTransformMenu()
        {
            if (GetSelectedText() == null) return;
            var list = CurrentList();
            _transformMenu.PlacementTarget = list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as UIElement ?? list;
            _transformMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            _transformMenu.IsOpen = true;
        }

        // убирает из списка list все выделенные строки (source это коллекция, которую этот список показывает)
        private static void RemoveSelected(System.Windows.Controls.ListBox list, ObservableCollection<string> source)
        {
            if (list.SelectedItems.Count == 0) return;
            // запоминаем самую верхнюю выделенную строку (в том, что видно на экране), чтобы после удаления встать рядом
            int index = list.SelectedItems.Cast<object>().Min(list.Items.IndexOf);
            // копируем выделенное в отдельный список: удалять прямо из SelectedItems во время перебора нельзя
            foreach (string text in list.SelectedItems.Cast<string>().ToList())
                source.Remove(text);
            if (list.Items.Count > 0) list.SelectedIndex = Math.Min(index, list.Items.Count - 1);
            FocusList(list);
        }

        // Delete: в «Истории» и «Закреплённых» удаляет все выделенные записи, в «Ячейках» очищает ячейку
        private void DeleteSelected()
        {
            if (Tabs.SelectedIndex == TAB_HISTORY) RemoveSelected(HistoryList, History);
            else if (Tabs.SelectedIndex == TAB_PINNED) RemoveSelected(PinnedList, Pinned);
            else
            {
                if (SlotsList.SelectedItem is not SlotItem slot) return;
                int index = SlotsList.SelectedIndex;
                CurrentSlots[slot.Number] = null;
                RefreshSlot(slot.Number);
                SlotsList.SelectedIndex = index;
                FocusList(SlotsList);
            }
            SaveAll();
        }

        // сочетание закрепления (по умолчанию Ctrl+P): в «Истории» закрепляет выделенное, в «Закреплённых» открепляет
        private void TogglePinSelected()
        {
            if (Tabs.SelectedIndex == TAB_HISTORY)
            {
                // берём выделенные строки в том порядке, в каком они стоят в списке
                var texts = HistoryList.SelectedItems.Cast<string>().OrderBy(HistoryList.Items.IndexOf).ToList();
                foreach (string text in texts)
                    if (!Pinned.Contains(text)) Pinned.Add(text);
                if (texts.Count > 0) ShowPinnedNote(texts.Count);
            }
            else if (Tabs.SelectedIndex == TAB_PINNED)
            {
                RemoveSelected(PinnedList, Pinned);
            }
            SaveAll();
        }

        // на полторы секунды показываем в заголовке окна, что запись закреплена
        private async void ShowPinnedNote(int count)
        {
            Title = count == 1 ? "SlotPaste: закреплено" : $"SlotPaste: закреплено записей: {count}";
            await Task.Delay(1500);
            UpdateStatus();   // возвращаем обычный заголовок
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // пока открыто меню «вставить как…», клавиши (стрелки, Enter) достаются меню
            if (_transformMenu.IsOpen) return;

            // с зажатым Alt WPF присылает Key.System, а настоящая клавиша лежит в SystemKey
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool isPinCombo = Hotkeys.CurrentMods() == Settings.PinModifiers &&
                              key != Key.None && (uint)KeyInterop.VirtualKeyFromKey(key) == Settings.PinKey;

            if (isPinCombo) { TogglePinSelected(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift) { OpenTransformMenu(); e.Handled = true; }
            else if (e.Key == Key.Enter) { PasteSelected(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                // первый Esc очищает поиск, второй прячет окно
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && !SearchBox.IsKeyboardFocused)
            {
                // в поле поиска Delete стирает буквы как обычно, а в списке удаляет записи
                DeleteSelected();
                e.Handled = true;
            }
        }

        // кнопка «Очистить историю»: спрашиваем подтверждение и удаляем всё (ячейки не трогаем)
        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (History.Count == 0) return;
            var answer = System.Windows.MessageBox.Show(this, "Удалить всю историю? Закреплённые и ячейки останутся.", "SlotPaste",
                                                        MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            History.Clear();
            SearchBox.Text = "";
            SaveAll();
            FocusList(HistoryList);
        }

        // двойной клик по строчке в любой вкладке вставляет её
        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PasteSelected();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!App.IsExiting) { e.Cancel = true; Hide(); }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            UnregisterHotkeys();
            RemoveClipboardFormatListener(_hwnd);
            base.OnClosed(e);
        }
    }
}
