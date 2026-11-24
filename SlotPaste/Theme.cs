using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Markup;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Media = System.Windows.Media;

namespace SlotPaste
{
    // Тема оформления: светлая или тёмная.
    // Всё лежит в одном файле, никаких отдельных .xaml не нужно:
    //  1) цвета (кисти) с именами вроде "WindowBg" или "Text" собираются здесь в коде;
    //  2) внешний вид кнопок, полей, списков и т.д. описан в XAML-строке ниже (StylesXaml);
    //  3) окна берут цвета через DynamicResource, поэтому при смене темы всё перекрашивается сразу.
    public static class Theme
    {
        public const int ModeSystem = 0, ModeLight = 1, ModeDark = 2;   // значения настройки ThemeMode

        public static bool IsDark { get; private set; }     // сейчас включена тёмная?
        public static event Action<bool>? Changed;          // сообщает, что тема поменялась (нужно трею)

        private static int _mode = ModeSystem;
        private static System.Windows.ResourceDictionary? _colors;   // кисти текущей темы
        private static System.Windows.ResourceDictionary? _styles;   // стили элементов (одни на обе темы)
        private static bool _listening;                               // подписались на смену темы Windows

        // ---------- Windows API для тёмного заголовка окна ----------
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;       // тёмный заголовок (Windows 10 20H1+ и 11)
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // то же на старых сборках Windows 10
        private const int DWMWA_CAPTION_COLOR = 35;                 // цвет заголовка (только Windows 11)
        private const uint SWP_REDRAW_FRAME = 0x0002 | 0x0001 | 0x0004 | 0x0010 | 0x0020;   // NOMOVE|NOSIZE|NOZORDER|NOACTIVATE|FRAMECHANGED

        // включает тему: 0 = как в Windows, 1 = светлая, 2 = тёмная
        public static void Apply(int mode)
        {
            _mode = Math.Clamp(mode, ModeSystem, ModeDark);
            IsDark = _mode == ModeDark || (_mode == ModeSystem && WindowsUsesDark());

            var app = System.Windows.Application.Current;
            if (app == null) return;

            // стили читаем из XAML-строки один раз
            _styles ??= (System.Windows.ResourceDictionary)XamlReader.Parse(StylesXaml);

            // меняем словарь с цветами на новый: всё, что на него ссылается через DynamicResource, перекрасится
            var dicts = app.Resources.MergedDictionaries;

            // при первом запуске убираем «черновые» цвета из App.xaml: они нужны только
            // конструктору Visual Studio, чтобы он видел имена WindowBg, Text, AccentButton и т.д.
            if (_colors == null) dicts.Clear();

            var colors = BuildColors(IsDark);
            if (_colors != null) dicts.Remove(_colors);
            dicts.Insert(0, colors);
            _colors = colors;
            if (!dicts.Contains(_styles)) dicts.Add(_styles);

            foreach (System.Windows.Window window in app.Windows) ApplyTitleBar(window);

            // следим за сменой темы в Windows (нужно для режима «Системная»)
            if (!_listening)
            {
                SystemEvents.UserPreferenceChanged += OnWindowsSettingsChanged;
                _listening = true;
            }

            Changed?.Invoke(IsDark);
        }

        // отписываемся при выходе из программы
        public static void Stop()
        {
            if (_listening) SystemEvents.UserPreferenceChanged -= OnWindowsSettingsChanged;
            _listening = false;
        }

        // в настройках Windows что-то поменяли: если у нас «Системная» и тема другая, перекрашиваемся
        private static void OnWindowsSettingsChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General || _mode != ModeSystem) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (WindowsUsesDark() != IsDark) Apply(ModeSystem);
            });
        }

        // читает из реестра, какая тема приложений выбрана в Windows (Параметры → Персонализация → Цвета)
        public static bool WindowsUsesDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch { return false; }
        }

        // вызывать в конструкторе каждого окна: когда у окна появится заголовок, красим его под тему
        public static void Attach(System.Windows.Window window)
        {
            window.SourceInitialized += (s, e) => ApplyTitleBar(window);
        }

        // тёмный/светлый заголовок окна (рамку с крестиком рисует сама Windows, поэтому через её API)
        private static void ApplyTitleBar(System.Windows.Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;   // окно ещё ни разу не показывалось

            int dark = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));

            // на Windows 11 заголовок того же цвета, что и окно (на Windows 10 просто ничего не произойдёт)
            var bg = IsDark ? DarkWindowBg : LightWindowBg;
            int colorRef = bg.R | (bg.G << 8) | (bg.B << 16);   // Windows ждёт цвет в порядке 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));

            // просим Windows перерисовать рамку, иначе заголовок поменяется только после переключения окон
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_REDRAW_FRAME);
        }

        // ---------- цвета ----------
        private static readonly Media.Color LightWindowBg = Hex("#F3F3F3");
        private static readonly Media.Color DarkWindowBg = Hex("#202020");

        private static Media.Color Hex(string hex) => (Media.Color)Media.ColorConverter.ConvertFromString(hex);

        // собирает словарь кистей для темы. Имена одинаковые в обеих темах, отличаются только цвета
        private static System.Windows.ResourceDictionary BuildColors(bool dark)
        {
            var d = new System.Windows.ResourceDictionary();
            void Add(string name, string light, string darkHex)
            {
                var brush = new Media.SolidColorBrush(Hex(dark ? darkHex : light));
                brush.Freeze();   // «заморозка»: кисть больше не меняется, так WPF рисует её быстрее
                d[name] = brush;
            }

            //   имя                 светлая      тёмная
            Add("WindowBg", "#F3F3F3", "#202020");   // фон окна
            Add("PanelBg", "#FFFFFF", "#2B2B2B");   // фон списков
            Add("InputBg", "#FFFFFF", "#2D2D2D");   // фон полей ввода
            Add("ControlBg", "#FDFDFD", "#2D2D2D");   // кнопки, выпадающие списки
            Add("ControlHoverBg", "#F3F3F3", "#383838");   // кнопка под мышкой
            Add("ControlPressedBg", "#EAEAEA", "#262626");   // кнопка нажата
            Add("Border", "#D9D9D9", "#3F3F3F");   // рамки
            Add("BorderHover", "#C4C4C4", "#525252");   // рамка под мышкой
            Add("Text", "#1B1B1B", "#F0F0F0");   // основной текст
            Add("SubtleText", "#5C5C5C", "#B4B4B4");   // подсказки, серый текст
            Add("Accent", "#0078D7", "#0078D7");   // фирменный синий
            Add("AccentHover", "#1A86DD", "#1A86DD");
            Add("AccentPressed", "#006CC1", "#006CC1");
            Add("AccentText", "#FFFFFF", "#FFFFFF");   // текст на синем
            Add("SelectionBg", "#0078D7", "#0078D7");   // выбранная строка в списке
            Add("SelectionText", "#FFFFFF", "#FFFFFF");
            Add("HoverBg", "#E5F1FB", "#353535");   // строка под мышкой
            Add("MenuBg", "#FFFFFF", "#2B2B2B");   // меню и выпадающие списки
            Add("Separator", "#E5E5E5", "#3D3D3D");   // линии-разделители
            Add("ScrollThumb", "#C2C2C2", "#5C5C5C");   // ползунок прокрутки
            Add("ScrollThumbHover", "#9E9E9E", "#808080");
            Add("Error", "#C42B1C", "#FF99A4");   // текст ошибки
            Add("Warning", "#B05000", "#FFB86B");   // строка состояния (очередь, пауза)
            return d;
        }

        // ---------- трей (меню у значка возле часов) ----------
        // это меню из WinForms, поэтому красится по-своему: через «рисовальщика» (Renderer)
        public static void StyleTrayMenu(Forms.ContextMenuStrip menu)
        {
            if (IsDark)
            {
                menu.Renderer = new DarkTrayRenderer();
                menu.BackColor = System.Drawing.Color.FromArgb(0x2B, 0x2B, 0x2B);
                menu.ForeColor = System.Drawing.Color.FromArgb(0xF2, 0xF2, 0xF2);
            }
            else
            {
                menu.RenderMode = Forms.ToolStripRenderMode.ManagerRenderMode;   // обычный вид Windows
                menu.BackColor = System.Drawing.SystemColors.Control;
                menu.ForeColor = System.Drawing.SystemColors.ControlText;
            }
        }

        private class DarkTrayRenderer : Forms.ToolStripProfessionalRenderer
        {
            public DarkTrayRenderer() : base(new DarkTrayColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? System.Drawing.Color.FromArgb(0xF2, 0xF2, 0xF2) : System.Drawing.Color.Gray;
                base.OnRenderItemText(e);
            }

            // галочку («Пауза записи») рисуем сами белой, стандартная чёрная на тёмном не видна
            protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
            {
                var r = e.ImageRectangle;
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2);
                g.DrawLines(pen, new[]
                {
                    new System.Drawing.PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
                    new System.Drawing.PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
                    new System.Drawing.PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f)
                });
            }
        }

        private class DarkTrayColors : Forms.ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(0x2B, 0x2B, 0x2B);
            private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x3D, 0x3D, 0x3D);
            private static readonly System.Drawing.Color Line = System.Drawing.Color.FromArgb(0x45, 0x45, 0x45);

            public override System.Drawing.Color ToolStripDropDownBackground => Bg;
            public override System.Drawing.Color ImageMarginGradientBegin => Bg;
            public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
            public override System.Drawing.Color ImageMarginGradientEnd => Bg;
            public override System.Drawing.Color MenuBorder => Line;
            public override System.Drawing.Color MenuItemBorder => Hover;
            public override System.Drawing.Color MenuItemSelected => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
            public override System.Drawing.Color SeparatorDark => Line;
            public override System.Drawing.Color SeparatorLight => Bg;
            public override System.Drawing.Color CheckBackground => Bg;
            public override System.Drawing.Color CheckSelectedBackground => Hover;
            public override System.Drawing.Color CheckPressedBackground => Hover;
            public override System.Drawing.Color ButtonSelectedBorder => Hover;
        }

        // ---------- внешний вид элементов ----------
        // Это обычный XAML, просто записанный строкой. Цвета везде берутся по именам через DynamicResource.
        // Стили без x:Key применяются ко всем элементам своего типа во всех окнах программы.
        private const string StylesXaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- ===== Кнопка: скруглённая, с тонкой рамкой ===== -->
    <Style TargetType="Button">
        <Setter Property="Background" Value="{DynamicResource ControlBg}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Border}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="12,4"/>
        <Setter Property="MinHeight" Value="28"/>
        <Setter Property="HorizontalContentAlignment" Value="Center"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="SnapsToDevicePixels" Value="True"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Bd" CornerRadius="4" Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                            Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                        <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                          VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="Bd" Property="Opacity" Value="0.45"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource ControlHoverBg}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource BorderHover}"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
                <Setter Property="Background" Value="{DynamicResource ControlPressedBg}"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
                <Setter Property="BorderBrush" Value="{DynamicResource Accent}"/>
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- синяя «главная» кнопка (Сохранить, OK) -->
    <Style x:Key="AccentButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
        <Setter Property="Background" Value="{DynamicResource Accent}"/>
        <Setter Property="Foreground" Value="{DynamicResource AccentText}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Accent}"/>
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="{DynamicResource AccentHover}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource AccentHover}"/>
            </Trigger>
            <Trigger Property="IsPressed" Value="True">
                <Setter Property="Background" Value="{DynamicResource AccentPressed}"/>
                <Setter Property="BorderBrush" Value="{DynamicResource AccentPressed}"/>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
                <Setter Property="BorderBrush" Value="{DynamicResource Text}"/>
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- ===== Поле ввода: скруглённое, при фокусе снизу синяя полоска, как в Windows 11 ===== -->
    <Style TargetType="TextBox">
        <Setter Property="Background" Value="{DynamicResource InputBg}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Border}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="CaretBrush" Value="{DynamicResource Text}"/>
        <Setter Property="SelectionBrush" Value="{DynamicResource Accent}"/>
        <Setter Property="Padding" Value="6,4"/>
        <Setter Property="MinHeight" Value="28"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="SnapsToDevicePixels" Value="True"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                    <Grid>
                        <Border x:Name="Bd" CornerRadius="4" Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                                Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                            <ScrollViewer x:Name="PART_ContentHost" Focusable="False"
                                          VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                        </Border>
                        <!-- синяя полоска снизу, видна только когда в поле курсор -->
                        <Border x:Name="FocusLine" CornerRadius="4" BorderThickness="0,0,0,2"
                                BorderBrush="{DynamicResource Accent}" Visibility="Collapsed" IsHitTestVisible="False"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource BorderHover}"/>
                        </Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                            <Setter TargetName="FocusLine" Property="Visibility" Value="Visible"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="Bd" Property="Opacity" Value="0.45"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ===== Выпадающий список ===== -->
    <Style TargetType="ComboBox">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="MinHeight" Value="28"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="SnapsToDevicePixels" Value="True"/>
        <Setter Property="ScrollViewer.CanContentScroll" Value="True"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBox">
                    <Grid>
                        <!-- вся площадь списка: кнопка, которая открывает/закрывает выпадашку -->
                        <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press"
                                      IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                            <ToggleButton.Template>
                                <ControlTemplate TargetType="ToggleButton">
                                    <Border x:Name="Bd" CornerRadius="4" Background="{DynamicResource ControlBg}"
                                            BorderBrush="{DynamicResource Border}" BorderThickness="1" SnapsToDevicePixels="True">
                                        <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,1,10,0"
                                              Data="M 0 0 L 4 4 L 8 0" Stroke="{DynamicResource SubtleText}" StrokeThickness="1.5"
                                              StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
                                    </Border>
                                    <ControlTemplate.Triggers>
                                        <Trigger Property="IsMouseOver" Value="True">
                                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ControlHoverBg}"/>
                                            <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource BorderHover}"/>
                                        </Trigger>
                                        <Trigger Property="IsChecked" Value="True">
                                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ControlPressedBg}"/>
                                        </Trigger>
                                    </ControlTemplate.Triggers>
                                </ControlTemplate>
                            </ToggleButton.Template>
                        </ToggleButton>
                        <!-- выбранный пункт -->
                        <ContentPresenter IsHitTestVisible="False" Margin="10,4,28,4" VerticalAlignment="Center"
                                          Content="{TemplateBinding SelectionBoxItem}"
                                          ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                          ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"/>
                        <!-- выпадашка -->
                        <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                               PopupAnimation="Fade" IsOpen="{TemplateBinding IsDropDownOpen}">
                            <Border Margin="0,4,0,0" Padding="4" CornerRadius="6"
                                    Background="{DynamicResource MenuBg}" BorderBrush="{DynamicResource Border}" BorderThickness="1"
                                    TextElement.Foreground="{DynamicResource Text}"
                                    MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                                    MaxHeight="{TemplateBinding MaxDropDownHeight}">
                                <ScrollViewer>
                                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                                </ScrollViewer>
                            </Border>
                        </Popup>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.45"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="ComboBoxItem">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Padding" Value="8,5"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBoxItem">
                    <Border x:Name="Bd" CornerRadius="4" Background="Transparent" Padding="{TemplateBinding Padding}" Margin="0,1">
                        <ContentPresenter/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsHighlighted" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource HoverBg}"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource SelectionBg}"/>
                            <Setter Property="Foreground" Value="{DynamicResource SelectionText}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ===== Вкладки: без рамки-коробки, у выбранной синяя полоска снизу ===== -->
    <!-- Foreground задаём явно: стандартный стиль Windows красит вкладки чёрным,
         и текст внутри вкладок в тёмной теме получался чёрным на тёмном -->
    <Style TargetType="TabControl">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Padding" Value="0,10,0,0"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TabControl">
                    <DockPanel>
                        <Grid DockPanel.Dock="Top">
                            <Border VerticalAlignment="Bottom" Height="1" Background="{DynamicResource Separator}"/>
                            <TabPanel IsItemsHost="True" KeyboardNavigation.TabIndex="1"/>
                        </Grid>
                        <ContentPresenter x:Name="PART_SelectedContentHost" ContentSource="SelectedContent"
                                          Margin="{TemplateBinding Padding}"/>
                    </DockPanel>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="TabItem">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TabItem">
                    <Grid Margin="0,0,2,0" Background="Transparent">
                        <Border x:Name="Bd" CornerRadius="4" Margin="0,2,0,4" Background="Transparent"/>
                        <ContentPresenter x:Name="Header" ContentSource="Header" Margin="12,6,12,10"
                                          HorizontalAlignment="Center" VerticalAlignment="Center"
                                          TextElement.Foreground="{DynamicResource SubtleText}" RecognizesAccessKey="True"/>
                        <Border x:Name="Line" Height="3" Width="20" CornerRadius="1.5" VerticalAlignment="Bottom"
                                Background="{DynamicResource Accent}" Visibility="Hidden"/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ControlHoverBg}"/>
                            <Setter TargetName="Header" Property="TextElement.Foreground" Value="{DynamicResource Text}"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Line" Property="Visibility" Value="Visible"/>
                            <Setter TargetName="Header" Property="TextElement.Foreground" Value="{DynamicResource Text}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ===== Список и его строчки ===== -->
    <Style TargetType="ListBox">
        <Setter Property="Background" Value="{DynamicResource PanelBg}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Border}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="3"/>
        <Setter Property="ScrollViewer.CanContentScroll" Value="True"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ListBox">
                    <Border CornerRadius="6" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" SnapsToDevicePixels="True">
                        <ScrollViewer Focusable="False" Padding="{TemplateBinding Padding}"
                                      CanContentScroll="{TemplateBinding ScrollViewer.CanContentScroll}"
                                      HorizontalScrollBarVisibility="{TemplateBinding ScrollViewer.HorizontalScrollBarVisibility}"
                                      VerticalScrollBarVisibility="{TemplateBinding ScrollViewer.VerticalScrollBarVisibility}">
                            <ItemsPresenter/>
                        </ScrollViewer>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- выбранная строка всегда ярко-синяя с белым текстом, даже когда курсор в поле поиска -->
    <Style TargetType="ListBoxItem">
        <Setter Property="Padding" Value="8,4"/>
        <Setter Property="Margin" Value="0,1"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                    <Border x:Name="Bd" CornerRadius="4" Background="Transparent" Padding="{TemplateBinding Padding}"
                            SnapsToDevicePixels="True">
                        <ContentPresenter/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource HoverBg}"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource SelectionBg}"/>
                            <Setter Property="Foreground" Value="{DynamicResource SelectionText}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ===== Тонкая полоса прокрутки без стрелок ===== -->
    <Style x:Key="ScrollPageButton" TargetType="RepeatButton">
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="IsTabStop" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="RepeatButton">
                    <Border Background="Transparent"/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="ScrollThumbStyle" TargetType="Thumb">
        <Setter Property="Focusable" Value="False"/>
        <Setter Property="IsTabStop" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Thumb">
                    <Border x:Name="Bd" CornerRadius="3" Margin="3" Background="{DynamicResource ScrollThumb}"/>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ScrollThumbHover}"/>
                        </Trigger>
                        <Trigger Property="IsDragging" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ScrollThumbHover}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="ScrollBar">
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="Width" Value="10"/>
        <Setter Property="MinWidth" Value="10"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ScrollBar">
                    <Grid Background="Transparent">
                        <Track x:Name="PART_Track" IsDirectionReversed="True">
                            <Track.DecreaseRepeatButton>
                                <RepeatButton Command="ScrollBar.PageUpCommand" Style="{StaticResource ScrollPageButton}"/>
                            </Track.DecreaseRepeatButton>
                            <Track.Thumb>
                                <Thumb Style="{StaticResource ScrollThumbStyle}"/>
                            </Track.Thumb>
                            <Track.IncreaseRepeatButton>
                                <RepeatButton Command="ScrollBar.PageDownCommand" Style="{StaticResource ScrollPageButton}"/>
                            </Track.IncreaseRepeatButton>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
                <Setter Property="Width" Value="Auto"/>
                <Setter Property="MinWidth" Value="0"/>
                <Setter Property="Height" Value="10"/>
                <Setter Property="MinHeight" Value="10"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="ScrollBar">
                            <Grid Background="Transparent">
                                <Track x:Name="PART_Track" IsDirectionReversed="False">
                                    <Track.DecreaseRepeatButton>
                                        <RepeatButton Command="ScrollBar.PageLeftCommand" Style="{StaticResource ScrollPageButton}"/>
                                    </Track.DecreaseRepeatButton>
                                    <Track.Thumb>
                                        <Thumb Style="{StaticResource ScrollThumbStyle}"/>
                                    </Track.Thumb>
                                    <Track.IncreaseRepeatButton>
                                        <RepeatButton Command="ScrollBar.PageRightCommand" Style="{StaticResource ScrollPageButton}"/>
                                    </Track.IncreaseRepeatButton>
                                </Track>
                            </Grid>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- область прокрутки: свой шаблон, чтобы в углу не оставался серый квадратик -->
    <Style TargetType="ScrollViewer">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ScrollViewer">
                    <Grid Background="{TemplateBinding Background}">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <Grid.RowDefinitions>
                            <RowDefinition Height="*"/>
                            <RowDefinition Height="Auto"/>
                        </Grid.RowDefinitions>
                        <ScrollContentPresenter x:Name="PART_ScrollContentPresenter" Margin="{TemplateBinding Padding}"
                                                CanContentScroll="{TemplateBinding CanContentScroll}"
                                                Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}"/>
                        <ScrollBar x:Name="PART_VerticalScrollBar" Grid.Column="1" Cursor="Arrow"
                                   Value="{TemplateBinding VerticalOffset}" Maximum="{TemplateBinding ScrollableHeight}"
                                   ViewportSize="{TemplateBinding ViewportHeight}"
                                   Visibility="{TemplateBinding ComputedVerticalScrollBarVisibility}"/>
                        <ScrollBar x:Name="PART_HorizontalScrollBar" Grid.Row="1" Orientation="Horizontal" Cursor="Arrow"
                                   Value="{TemplateBinding HorizontalOffset}" Maximum="{TemplateBinding ScrollableWidth}"
                                   ViewportSize="{TemplateBinding ViewportWidth}"
                                   Visibility="{TemplateBinding ComputedHorizontalScrollBarVisibility}"/>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ===== Меню по правому клику («вставить как…») ===== -->
    <Style TargetType="ContextMenu">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="SnapsToDevicePixels" Value="True"/>
        <Setter Property="HasDropShadow" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ContextMenu">
                    <Border Background="{DynamicResource MenuBg}" BorderBrush="{DynamicResource Border}" BorderThickness="1"
                            CornerRadius="6" Padding="4" MinWidth="200">
                        <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="MenuItem">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="MenuItem">
                    <Border x:Name="Bd" CornerRadius="4" Background="Transparent" Padding="10,6" Margin="0,1">
                        <ContentPresenter ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsHighlighted" Value="True">
                            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource HoverBg}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="Bd" Property="Opacity" Value="0.45"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- линия-разделитель в меню -->
    <Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Separator">
                    <Border Height="1" Margin="6,4" Background="{DynamicResource Separator}"/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ===== Галочка (на будущее, если появятся CheckBox) ===== -->
    <Style TargetType="CheckBox">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                    <StackPanel Orientation="Horizontal" Background="Transparent">
                        <Border x:Name="Box" Width="18" Height="18" CornerRadius="4" VerticalAlignment="Center"
                                Background="{DynamicResource InputBg}" BorderBrush="{DynamicResource BorderHover}" BorderThickness="1">
                            <Path x:Name="Mark" Data="M 3.5 8.5 L 7 12 L 14 5" Stroke="{DynamicResource AccentText}"
                                  StrokeThickness="2" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                                  Visibility="Collapsed"/>
                        </Border>
                        <ContentPresenter Margin="8,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                    </StackPanel>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="Box" Property="Background" Value="{DynamicResource Accent}"/>
                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                            <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.45"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- ===== Всплывающая подсказка ===== -->
    <Style TargetType="ToolTip">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="HasDropShadow" Value="False"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ToolTip">
                    <Border Background="{DynamicResource MenuBg}" BorderBrush="{DynamicResource Border}" BorderThickness="1"
                            CornerRadius="4" Padding="8,4">
                        <ContentPresenter/>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
</ResourceDictionary>
""";
    }
}
