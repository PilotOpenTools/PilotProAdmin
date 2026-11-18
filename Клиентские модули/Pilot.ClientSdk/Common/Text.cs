using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Pilot.ClientSdk
{
    /// <summary>Работа со строками, которая повторяется в модулях: ФИО, очистка названий, сортировка шифров.</summary>
    public static class Text
    {
        private static readonly Regex FullName = new Regex(@"^\s*(\S+)\s+(\S+)(?:\s+(\S+))?", RegexOptions.Compiled);

        /// <summary>"Иванов Иван Иванович" / "И.И. Иванов" (для подписей). Непохожее на ФИО возвращается как есть.</summary>
        public static string Fio(string fullName)
        {
            var m = FullName.Match(fullName ?? string.Empty);
            return m.Success ? Initials(m) + " " + m.Groups[1].Value : fullName ?? string.Empty;
        }

        /// <summary>"Иванов Иван Иванович" / "Иванов И.И." (для таблиц).</summary>
        public static string ShortName(string fullName)
        {
            var m = FullName.Match(fullName ?? string.Empty);
            return m.Success ? m.Groups[1].Value + " " + Initials(m) : fullName ?? string.Empty;
        }

        private static string Initials(Match m)
        {
            var result = char.ToUpper(m.Groups[2].Value[0]) + ".";
            if (m.Groups[3].Success)
                result += char.ToUpper(m.Groups[3].Value[0]) + ".";
            return result;
        }

        /// <summary>Убирает кавычки всех видов и лишние пробелы по краям.</summary>
        public static string CleanTitle(string title) =>
            (title ?? string.Empty)
                .Replace("\"", "").Replace("'", "").Replace("«", "").Replace("»", "").Replace("“", "").Replace("”", "")
                .Trim();

        /// <summary>Склеивает непустые значения через разделитель.</summary>
        public static string JoinNonEmpty(string separator, params string[] values) =>
            string.Join(separator, (values ?? Array.Empty<string>()).Where(v => !string.IsNullOrWhiteSpace(v)));

        public static string JoinNonEmpty(string separator, IEnumerable<string> values) => JoinNonEmpty(separator, values?.ToArray());

        /// <summary>Сравнение шифров вида "1.2.10" по числам: "1.2" &lt; "1.10".</summary>
        public static int CompareCodes(string a, string b)
        {
            a = a?.Trim() ?? string.Empty;
            b = b?.Trim() ?? string.Empty;
            if (string.Equals(a, b, StringComparison.Ordinal))
                return 0;
            if (a.Length == 0)
                return -1;
            if (b.Length == 0)
                return 1;

            var partsA = a.Split('.', '-', ' ');
            var partsB = b.Split('.', '-', ' ');
            for (var i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
            {
                var result = long.TryParse(partsA[i], out var na) && long.TryParse(partsB[i], out var nb)
                    ? na.CompareTo(nb)
                    : string.Compare(partsA[i], partsB[i], StringComparison.CurrentCultureIgnoreCase);
                if (result != 0)
                    return result;
            }
            return partsA.Length.CompareTo(partsB.Length);
        }

        /// <summary>Компаратор для OrderBy: <c>docs.OrderBy(d =&gt; d.Code, Text.CodeComparer)</c>.</summary>
        public static IComparer<string> CodeComparer { get; } = Comparer<string>.Create(CompareCodes);

        /// <summary>bool / 0/1 - так Pilot хранит флажки в целочисленных атрибутах.</summary>
        public static int ToInt(this bool value) => value ? 1 : 0;

        public static bool ToBool(this int value) => value == 1;
    }
}
