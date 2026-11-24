using System.Windows;
using System.Windows.Controls;

namespace ClipApp
{
    // Маленькое окошко «введи текст» (в WPF готового такого нет, поэтому собираем его прямо в коде)
    public static class InputDialog
    {
        // показывает окошко и возвращает введённый текст, или null, если нажали «Отмена»
        public static string? Ask(System.Windows.Window owner, string title, string question, string text = "")
        {
            var box = new System.Windows.Controls.TextBox { Text = text, Margin = new Thickness(0, 8, 0, 16) };
            var ok = new System.Windows.Controls.Button { Content = "OK", MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new System.Windows.Controls.Button { Content = "Отмена", MinWidth = 90, IsCancel = true };

            // OK — синяя «главная» кнопка из темы (Theme.cs)
            if (System.Windows.Application.Current?.TryFindResource("AccentButton") is Style accent) ok.Style = accent;

            var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = question });
            panel.Children.Add(box);
            panel.Children.Add(buttons);

            var window = new System.Windows.Window
            {
                Title = title,
                Content = panel,
                Owner = owner,
                Width = 340,
                UseLayoutRounding = true,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Topmost = true,
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/SlotPaste.ico"))
            };

            // цвета окна берём из темы; SetResourceReference — то же самое, что DynamicResource в XAML
            window.SetResourceReference(System.Windows.Window.BackgroundProperty, "WindowBg");
            window.SetResourceReference(System.Windows.Window.ForegroundProperty, "Text");
            Theme.Attach(window);   // тёмный/светлый заголовок

            ok.Click += (s, e) => window.DialogResult = true;          // OK закрывает окно с ответом «да»
            window.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };   // курсор сразу в поле

            return window.ShowDialog() == true ? box.Text.Trim() : null;
        }
    }
}
