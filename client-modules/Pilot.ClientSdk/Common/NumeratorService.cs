using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ascon.Pilot.SDK;

namespace Pilot.ClientSdk
{
    /// <summary>
    /// Атрибуты-нумераторы (перенос PilotNumeratorService из NugetPackage).
    /// В атрибут перед Apply пишется не номер, а декларация нумератора из конфигурации типа;
    /// номер Pilot считает после Apply асинхронно.
    /// <code>
    /// var numerator = new NumeratorService(attributeFormatParser);    // IAttributeFormatParser из ImportingConstructor
    /// var builder = modifier.CreateById(id, parent.Id, type);
    /// numerator.SetNumeratorAttribute(builder, type, "number");
    /// modifier.Apply();
    /// var created = await numerator.WaitNumberAsync(repository, id, "number");
    /// </code>
    /// </summary>
    public sealed class NumeratorService
    {
        private const string PlaceholderPrefix = "{counter";

        private readonly IAttributeFormatParser _parser;

        public NumeratorService(IAttributeFormatParser parser) =>
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));

        /// <summary>Значение, которое нужно записать в атрибут-нумератор до Apply ("" - у атрибута нет нумератора).</summary>
        public string GetNumeratorValue(IType type, string attributeName)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (string.IsNullOrWhiteSpace(attributeName)) throw new ArgumentException("Не указан атрибут.", nameof(attributeName));

            var attribute = type.Attributes?.FirstOrDefault(a => a.Name == attributeName);
            if (attribute == null || !_parser.TryParseNumeratorDeclaration(attribute.Configuration2(), out var infos) || infos == null)
                return string.Empty;

            foreach (var info in infos)
            {
                var value = info.IsDeferredRegistration
                    ? _parser.DeferredNumeratorAttributeValuePrefix + info.Configuration
                    : info.Configuration;
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
            return string.Empty;
        }

        public void SetNumeratorAttribute(IObjectBuilder builder, IType type, string attributeName)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            builder.SetAttribute(attributeName, GetNumeratorValue(type, attributeName));
        }

        /// <summary>Номер уже посчитан (не пустой и не заглушка «{counter…}»).</summary>
        public static bool IsNumberReady(string value) =>
            !string.IsNullOrWhiteSpace(value) && !value.StartsWith(PlaceholderPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Ждёт, пока Pilot заменит заглушку нумератора реальным номером. Бросает TimeoutException, если не дождались
        /// (по умолчанию 3 минуты).
        /// </summary>
        public async Task<IDataObject> WaitNumberAsync(IObjectsRepository repository, Guid objectId, string attributeName,
            TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var wait = timeout ?? TimeSpan.FromMinutes(3);
            var obj = await new Loader(repository).WaitAsync(objectId, o => IsNumberReady(o.Str(attributeName)), wait, ct).ConfigureAwait(false);
            return obj ?? throw new TimeoutException($"Нумератор «{attributeName}» объекта {objectId} не посчитан за {wait.TotalSeconds:0} с.");
        }
    }
}
