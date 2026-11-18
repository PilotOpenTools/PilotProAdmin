using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ascon.Pilot.SDK;

namespace Pilot.ClientSdk
{
    /// <summary>
    /// Чтение атрибутов и состояния IDataObject. Все методы безопасны к null: нет объекта, атрибута
    /// или значения - вернётся ""/null/пустой список, исключения не будет.
    /// Замена GetStringAttribute / GetAttribute&lt;T&gt; из NugetPackage.
    /// <code>
    /// var name = obj.Str("name");
    /// var deadline = obj.Date("deadlineDate");          // DateTime? (локальное время)
    /// var executors = obj.OrgUnitIds("executor");
    /// if (obj.Is("0_project") &amp;&amp; obj.IsAlive()) …
    /// </code>
    /// </summary>
    public static class DataObjectExtensions
    {
        /// <summary>Сырое значение атрибута или null.</summary>
        public static object Attr(this IDataObject obj, string name) =>
            obj?.Attributes != null && name != null && obj.Attributes.TryGetValue(name, out var value) ? value : null;

        /// <summary>Значение нужного типа или default (числа приводятся между int/long/double/decimal).</summary>
        public static T Attr<T>(this IDataObject obj, string name)
        {
            var value = obj.Attr(name);
            if (value == null)
                return default;
            if (value is T typed)
                return typed;
            try
            {
                var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
                    return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            {
                // несовместимый тип - default
            }
            return default;
        }

        /// <summary>Атрибут есть и не пустой.</summary>
        public static bool Has(this IDataObject obj, string name)
        {
            var value = obj.Attr(name);
            return value != null && !(value is string s && s.Length == 0) && !(value is Array a && a.Length == 0);
        }

        /// <summary>Строка ("" если нет). Массивы склеиваются через ", ", даты - в локальном формате.</summary>
        public static string Str(this IDataObject obj, string name) => Format(obj.Attr(name));

        /// <summary>Строка или null, если атрибута нет или он пустой.</summary>
        public static string StrOrNull(this IDataObject obj, string name)
        {
            var s = obj.Str(name);
            return s.Length == 0 ? null : s;
        }

        /// <summary>Дата в локальном времени (Pilot хранит UTC).</summary>
        public static DateTime? Date(this IDataObject obj, string name) =>
            obj.Attr(name) is DateTime d ? d.Kind == DateTimeKind.Local ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime() : (DateTime?)null;

        public static string DateStr(this IDataObject obj, string name, string format = "dd.MM.yyyy") =>
            obj.Date(name)?.ToString(format, CultureInfo.CurrentCulture) ?? string.Empty;

        public static long? Int(this IDataObject obj, string name)
        {
            var value = obj.Attr(name);
            switch (value)
            {
                case int i: return i;
                case long l: return l;
                case double d: return (long)d;
                case decimal m: return (long)m;
                case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed): return parsed;
                default: return null;
            }
        }

        public static double? Double(this IDataObject obj, string name)
        {
            var value = obj.Attr(name);
            switch (value)
            {
                case double d: return d;
                case int i: return i;
                case long l: return l;
                case decimal m: return (double)m;
                case string s when double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed): return parsed;
                default: return null;
            }
        }

        public static Guid? GuidAttr(this IDataObject obj, string name)
        {
            var value = obj.Attr(name);
            if (value is Guid g) return g;
            return value is string s && System.Guid.TryParse(s, out var parsed) ? parsed : (Guid?)null;
        }

        /// <summary>Id оргединиц (должностей) из атрибута-оргединицы.</summary>
        public static IReadOnlyList<int> OrgUnitIds(this IDataObject obj, string name)
        {
            switch (obj.Attr(name))
            {
                case int[] ids: return ids;
                case int id: return new[] { id };
                case long l: return new[] { (int)l };
                default: return Array.Empty<int>();
            }
        }

        /// <summary>Системное имя типа ("" если нет).</summary>
        public static string TypeName(this IDataObject obj) => obj?.Type?.Name ?? string.Empty;

        /// <summary>Объект одного из типов.</summary>
        public static bool Is(this IDataObject obj, params string[] typeNames) => obj?.Type != null && typeNames.Contains(obj.Type.Name);

        /// <summary>Живой: не в корзине, не удалён, не заморожен.</summary>
        public static bool IsAlive(this IDataObject obj) => obj?.ObjectStateInfo?.State == ObjectState.Alive;

        public static bool IsFrozen(this IDataObject obj) => obj?.ObjectStateInfo?.State == ObjectState.Frozen;

        /// <summary>В корзине или удалён.</summary>
        public static bool IsRemoved(this IDataObject obj) =>
            obj == null || obj.ObjectStateInfo?.State == ObjectState.InRecycleBin || obj.ObjectStateInfo?.State == ObjectState.DeletedPermanently;

        /// <summary>Файлы актуальной версии; <paramref name="extension"/> - только с этим расширением (".pdf").</summary>
        public static IReadOnlyList<IFile> ActualFiles(this IDataObject obj, string extension = null) =>
            (obj?.ActualFileSnapshot?.Files ?? Enumerable.Empty<IFile>())
            .Where(f => extension == null || f.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .ToList();

        /// <summary>Ссылка piloturi:// на объект - открывает его в клиенте Pilot.</summary>
        public static string Link(this IDataObject obj) => obj == null ? string.Empty : "piloturi://" + obj.Id;

        internal static string Format(object value)
        {
            switch (value)
            {
                case null: return string.Empty;
                case string s: return s;
                case DateTime d: return (d.Kind == DateTimeKind.Local ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime()).ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
                case double d: return d.ToString(CultureInfo.CurrentCulture);
                case decimal m: return m.ToString(CultureInfo.CurrentCulture);
                case string[] arr: return string.Join(", ", arr.Where(x => !string.IsNullOrEmpty(x)));
                case int[] ints: return string.Join(", ", ints);
                default: return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
            }
        }
    }
}
