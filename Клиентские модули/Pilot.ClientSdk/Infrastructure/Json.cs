using System.Globalization;
using System.Text;

namespace Pilot.ClientSdk.Infrastructure
{
    /// <summary>
    /// Минимальная запись JSON без сторонних библиотек: в общем AppDomain клиента Pilot
    /// чужие версии Newtonsoft.Json/System.Text.Json - частый источник конфликтов.
    /// </summary>
    internal static class Json
    {
        public static void Prop(StringBuilder sb, string name, string value)
        {
            if (value == null)
                return;
            Escape(sb, name);
            sb.Append(':');
            Escape(sb, value);
            sb.Append(',');
        }

        public static void Prop(StringBuilder sb, string name, long? value)
        {
            if (value == null)
                return;
            Escape(sb, name);
            sb.Append(':').Append(value.Value.ToString(CultureInfo.InvariantCulture)).Append(',');
        }

        public static void Escape(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
