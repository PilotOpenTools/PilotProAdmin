using System.IO;
using System.Linq;
using System.Xml;
using Ascon.Pilot.SDK;

namespace Pilot.ClientSdk
{
    /// <summary>Расширения IType (из NugetPackage.TypeExtensions, безопасные к null и битой конфигурации).</summary>
    public static class TypeExtensions
    {
        /// <summary>
        /// Строковое значение из конфигурации атрибута типа (элемент &lt;Item&gt; с номером <paramref name="itemNum"/>).
        /// "" - атрибута, конфигурации или элемента нет.
        /// </summary>
        public static string ConstAttrValue(this IType type, string attrName, int itemNum = 0)
        {
            var config = type?.Attributes?.FirstOrDefault(a => a.Name == attrName)?.Configuration2();
            if (string.IsNullOrEmpty(config))
                return string.Empty;
            return ItemText(config, itemNum);
        }

        /// <summary>Текст элемента &lt;Item&gt; из XML конфигурации. DTD запрещён (конфигурацию задаёт сервер); битый XML - "".</summary>
        internal static string ItemText(string config, int itemNum)
        {
            try
            {
                var xml = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(new StringReader(config), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    xml.Load(reader);
                var items = xml.GetElementsByTagName("Item");
                return itemNum >= 0 && itemNum < items.Count ? items[itemNum].InnerText : string.Empty;
            }
            catch (XmlException)
            {
                return string.Empty;
            }
        }

        /// <summary>Заголовок атрибута типа ("" если нет).</summary>
        public static string AttributeTitle(this IType type, string attrName) =>
            type?.Attributes?.FirstOrDefault(a => a.Name == attrName)?.Title ?? string.Empty;

        public static bool IsTaskType(this IType type) => type?.Name?.StartsWith(SystemTypeNames.TASK_PREFIX) == true;

        public static bool IsWorkflowType(this IType type) => type?.Name?.StartsWith(SystemTypeNames.WORKFLOW_PREFIX) == true;

        public static bool IsWorkflowStageType(this IType type) => type?.Name?.StartsWith(SystemTypeNames.STAGE_PREFIX) == true;
    }
}
