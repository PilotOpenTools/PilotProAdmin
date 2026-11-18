using System;
using System.Collections.Generic;
using System.IO;
using Ascon.Pilot.SDK;

namespace Pilot.ClientSdk
{
    /// <summary>
    /// Типовые изменения через IObjectModifier. Замена CreateObject / AddFileByPath / CreateRelation из NugetPackage.
    /// Методы только готовят изменения - <c>modifier.Apply()</c> вызывает модуль (одной транзакцией можно сделать много
    /// изменений), кроме методов с суффиксом AndApply.
    /// <code>
    /// var builder = modifier.CreateChild(folder, "pir_doc", new Dictionary&lt;string, object&gt; { ["name"] = "Пояснительная записка" });
    /// modifier.Link(doc, contract, "project_contract");
    /// modifier.Apply();
    /// var created = await PilotApi.Loader.WaitAsync(builder.DataObject.Id, o => o.Has("name"));
    /// </code>
    /// </summary>
    public static class Modify
    {
        /// <summary>Новый дочерний объект с атрибутами (значения - string, int, long, double, decimal, DateTime, Guid, int[], string[]).</summary>
        public static IObjectBuilder CreateChild(this IObjectModifier modifier, IDataObject parent, IType type,
            IDictionary<string, object> attributes = null)
        {
            if (modifier == null) throw new ArgumentNullException(nameof(modifier));
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            if (type == null) throw new ArgumentNullException(nameof(type));
            var builder = modifier.Create(parent, type);
            builder.SetAttributes(attributes);
            return builder;
        }

        /// <summary>То же по системному имени типа.</summary>
        public static IObjectBuilder CreateChild(this IObjectModifier modifier, IDataObject parent, string typeName,
            IDictionary<string, object> attributes = null) =>
            modifier.CreateChild(parent, PilotApi.Type(typeName) ?? throw new InvalidOperationException($"В базе Pilot нет типа «{typeName}»."), attributes);

        /// <summary>Записать несколько атрибутов; null-значение удаляет атрибут.</summary>
        public static IObjectBuilder SetAttributes(this IObjectBuilder builder, IDictionary<string, object> attributes)
        {
            if (attributes == null)
                return builder;
            foreach (var pair in attributes)
                builder.SetAttributeValue(pair.Key, pair.Value);
            return builder;
        }

        /// <summary>Записать атрибут значением любого поддерживаемого SDK типа; null - удалить атрибут.</summary>
        public static IObjectBuilder SetAttributeValue(this IObjectBuilder builder, string name, object value)
        {
            switch (value)
            {
                case null: builder.RemoveAttribute(name); break;
                case string s: builder.SetAttribute(name, s); break;
                case int i: builder.SetAttribute(name, i); break;
                case long l: builder.SetAttribute(name, l); break;
                case double d: builder.SetAttribute(name, d); break;
                case decimal m: builder.SetAttribute(name, m); break;
                case DateTime dt: builder.SetAttribute(name, dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime()); break;
                case Guid g: builder.SetAttribute(name, g); break;
                case int[] ints: builder.SetAttribute(name, ints); break;
                case string[] strings: builder.SetAttribute(name, strings); break;
                case bool b: builder.SetAttribute(name, b ? 1 : 0); break;
                default: builder.SetAttributeAsObject(name, value); break;
            }
            return builder;
        }

        /// <summary>
        /// Файл во вкладку «Файлы» карточки: дочерний объект системного типа «Файл» с этим файлом.
        /// </summary>
        public static IObjectBuilder AddFileObject(this IObjectModifier modifier, IDataObject parent, string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Файл не найден", path);
            var fileType = PilotApi.Type(SystemTypeNames.PROJECT_FILE)
                           ?? throw new InvalidOperationException("В базе Pilot нет системного типа файла.");
            var builder = modifier.Create(parent, fileType);
            builder.SetAttribute(SystemAttributeNames.PROJECT_ITEM_NAME, Path.GetFileName(path));
            builder.AddFile(path);
            return builder;
        }

        /// <summary>Двусторонняя пользовательская связь между объектами (как «Связи» в карточке).</summary>
        public static void Link(this IObjectModifier modifier, IDataObject a, IDataObject b, string relationName = "Default",
            ObjectRelationType type = ObjectRelationType.Custom)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));
            var id = Guid.NewGuid();
            modifier.CreateLink(
                new Relation { Id = id, Type = type, Name = relationName, TargetId = b.Id },
                new Relation { Id = id, Type = type, Name = relationName, TargetId = a.Id });
        }
    }
}
