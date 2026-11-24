using System.Windows.Input;

namespace ClipApp
{
    // Вариант модификаторов для ячеек: название для списка и код для Windows
    public record ModOption(string Name, uint Mods)
    {
        public override string ToString() => Name;   // так он будет выглядеть в выпадающем списке
    }

    // Всё про сочетания клавиш: коды модификаторов и превращение их в текст вроде «Ctrl+Shift+V»
    public static class Hotkeys
    {
        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;

        // что можно выбрать для ячеек (цифра 1–9 добавляется сама)
        public static readonly ModOption[] SlotModifierOptions =
        {
            new("Ctrl", MOD_CONTROL),
            new("Alt", MOD_ALT),
            new("Ctrl+Shift", MOD_CONTROL | MOD_SHIFT),
            new("Ctrl+Alt", MOD_CONTROL | MOD_ALT),
        };

        // находит вариант в списке по коду; если такого нет, берёт первый
        public static ModOption FindOption(uint mods) =>
            Array.Find(SlotModifierOptions, o => o.Mods == mods) ?? SlotModifierOptions[0];

        // без Ctrl или Alt хоткей сломает обычный набор текста, такое не разрешаем
        public static bool HasCtrlOrAlt(uint mods) => (mods & (MOD_CONTROL | MOD_ALT)) != 0;

        // какие модификаторы зажаты прямо сейчас (в тех же кодах, что и выше)
        public static uint CurrentMods()
        {
            uint mods = 0;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= MOD_CONTROL;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= MOD_ALT;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= MOD_SHIFT;
            return mods;
        }

        public static string ModsToText(uint mods)
        {
            var parts = new List<string>();
            if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((mods & MOD_ALT) != 0) parts.Add("Alt");
            if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
            return string.Join("+", parts);
        }

        // понятные названия для клавиш, у которых в Windows странные имена (OemOpenBrackets, Oem6, D1…)
        private static readonly Dictionary<uint, string> KeyNames = new()
        {
            [0xBA] = ";",
            [0xBB] = "=",
            [0xBC] = ",",
            [0xBD] = "-",
            [0xBE] = ".",
            [0xBF] = "/",
            [0xC0] = "`",
            [0xDB] = "[",
            [0xDC] = "\\",
            [0xDD] = "]",
            [0xDE] = "'",
            [0xE2] = "\\",
            [0x6A] = "NumPad *",
            [0x6B] = "NumPad +",
            [0x6D] = "NumPad -",
            [0x6E] = "NumPad .",
            [0x6F] = "NumPad /",
            [0x08] = "Backspace",
            [0x09] = "Tab",
            [0x0D] = "Enter",
            [0x1B] = "Esc",
            [0x20] = "Пробел",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "←",
            [0x26] = "↑",
            [0x27] = "→",
            [0x28] = "↓",
            [0x2C] = "PrintScreen",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x13] = "Pause",
            [0x14] = "CapsLock",
            [0x90] = "NumLock",
            [0x91] = "ScrollLock",
        };

        public static string KeyToText(uint vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();        // цифры 0–9 (а не D1, D2…)
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();        // латинские буквы
            if (vk >= 0x60 && vk <= 0x69) return "NumPad " + (vk - 0x60);      // цифры на правой клавиатуре
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);            // F1–F24
            if (KeyNames.TryGetValue(vk, out string? name)) return name;      // [ ] ; ' и т.п.
            return KeyInterop.KeyFromVirtualKey((int)vk).ToString();           // всё остальное — как называет Windows
        }

        // «Ctrl+Shift+V»; если модификаторов нет, просто «F2», без плюса в начале
        public static string ToText(uint mods, uint vk)
        {
            string modsText = ModsToText(mods);
            return modsText.Length == 0 ? KeyToText(vk) : modsText + "+" + KeyToText(vk);
        }
    }
}
