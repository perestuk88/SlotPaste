using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ClipApp
{
    // Проверка, можно ли записывать то, что сейчас в буфере (пароли записывать не надо)
    public static class Privacy
    {
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        // true, если содержимое буфера записывать нельзя
        public static bool ShouldIgnore(string ignoredProcesses)
        {
            return HasPrivateMark() || IsFromIgnoredProgram(ignoredProcesses);
        }

        // Менеджеры паролей (KeePass, 1Password, Bitwarden и др.) кладут вместе с паролем специальные «пометки».
        // Это общепринятое правило Windows: такие пометки значат «не сохраняй меня в историю».
        private static bool HasPrivateMark()
        {
            try
            {
                if (System.Windows.Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return true;
                if (System.Windows.Clipboard.ContainsData("Clipboard Viewer Ignore")) return true;

                // эта пометка содержит число: 0 значит «в историю нельзя»
                if (System.Windows.Clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream stream &&
                    stream.Length >= 4)
                {
                    var bytes = new byte[4];
                    stream.ReadExactly(bytes, 0, 4);
                    if (BitConverter.ToInt32(bytes, 0) == 0) return true;
                }
            }
            catch { }
            return false;
        }

        // скопировано из программы, которая есть в списке исключений?
        private static bool IsFromIgnoredProgram(string ignoredProcesses)
        {
            // список из настроек: «KeePass.exe, Bitwarden.exe» → «keepass», «bitwarden»
            var names = ignoredProcesses.Split(',', ';')
                                        .Select(n => n.Trim().ToLower())
                                        .Select(n => n.EndsWith(".exe") ? n[..^4] : n)
                                        .Where(n => n.Length > 0)
                                        .ToList();
            if (names.Count == 0) return false;

            // чьё окно положило текст в буфер; если Windows не говорит, берём активное окно
            IntPtr window = GetClipboardOwner();
            if (window == IntPtr.Zero) window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;

            try
            {
                GetWindowThreadProcessId(window, out uint pid);
                using var process = Process.GetProcessById((int)pid);
                return names.Contains(process.ProcessName.ToLower());
            }
            catch { return false; }
        }
    }
}
