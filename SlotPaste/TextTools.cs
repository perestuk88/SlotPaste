using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SlotPaste
{
    // Всё, что программа умеет делать с текстом: исправить раскладку, поменять регистр, убрать лишние пробелы
    public static class TextTools
    {
        // Одни и те же клавиши в английской и русской раскладке. Символы стоят парами:
        // n-й символ первой строки и n-й символ второй строки печатаются одной клавишей.
        private const string En = "`qwertyuiop[]asdfghjkl;'zxcvbnm,./" +
                                  "~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?" +
                                  "@#$^&|";
        private const string Ru = "ёйцукенгшщзхъфывапролджэячсмитьбю." +
                                  "ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ," +
                                  "\"№;:?/";

        // Исправляет текст, набранный не в той раскладке: «ghbdtn» → «привет», «руддщ» → «hello».
        // Направление выбираем сами: каких букв больше, ту раскладку и считаем ошибочной.
        public static string FixLayout(string text)
        {
            int latin = 0, cyrillic = 0;
            foreach (char c in text)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latin++;
                else if ((c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я') || c == 'ё' || c == 'Ё') cyrillic++;
            }

            // больше русских букв: переводим RU → EN, иначе EN → RU
            string from = cyrillic > latin ? Ru : En;
            string to = cyrillic > latin ? En : Ru;

            var result = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                int index = from.IndexOf(c);
                result.Append(index >= 0 ? to[index] : c);   // чего нет в таблице (цифры, пробелы), оставляем как есть
            }
            return result.ToString();
        }

        public static string Upper(string text) => text.ToUpper();

        public static string Lower(string text) => text.ToLower();

        // «каждое слово с заглавной» (сначала всё в нижний регистр, иначе слова КАПСОМ так и останутся)
        public static string TitleCase(string text) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower());

        // убирает пробелы по краям строк, двойные пробелы и лишние пустые строки
        public static string CleanSpaces(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n')
                            .Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim());
            string joined = string.Join("\n", lines);
            joined = Regex.Replace(joined, @"\n{3,}", "\n\n");   // не больше одной пустой строки подряд
            return joined.Trim().Replace("\n", Environment.NewLine);
        }

        // склеивает весь текст в одну строку
        public static string OneLine(string text) => Regex.Replace(text, @"\s+", " ").Trim();

        // применяет преобразование по его короткому имени (имена стоят в Tag у пунктов меню в MainWindow.xaml)
        public static string Apply(string kind, string text) => kind switch
        {
            "upper" => Upper(text),
            "lower" => Lower(text),
            "title" => TitleCase(text),
            "clean" => CleanSpaces(text),
            "oneline" => OneLine(text),
            "layout" => FixLayout(text),
            _ => text   // "plain": как есть, обычным текстом
        };
    }
}
