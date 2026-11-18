using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using Ascon.Pilot.SDK;
using Pilot.ClientSdk.Infrastructure;

namespace Pilot.ClientSdk.Pilot
{
    /// <summary>
    /// Раздел «Модули» в общих настройках Pilot (Сервис / Настройки / Общие настройки).
    /// Значение - JSON <see cref="ModuleConfig"/>: адрес статистики, окно загрузки модулей и т.д.
    /// Задаёт администратор; клиенты получают его при запуске и при каждом изменении (без перезапуска).
    /// Пока раздел не заполнен, действуют настройки из Pilot.ClientSdk.config.json.
    /// </summary>
    [Export(typeof(ISettingsFeature2))]
    public sealed class CommonSettings : ISettingsFeature2
    {
        /// <summary>Ключ настройки в базе Pilot. Не менять: по нему клиенты находят сохранённое значение.</summary>
        public const string SettingKey = "Pilot.ClientSdk-6C1F2E7A-3B54-4D8E-9A0B-71E5C2D4F813";

        private ISettingValueProvider _valueProvider;

        public string Key => SettingKey;

        public string Title => "Модули общие настройки";

        public FrameworkElement Editor
        {
            get
            {
                try
                {
                    return new SettingsEditor(_valueProvider);
                }
                catch (Exception ex)
                {
                    ModuleLog.Error("Не удалось открыть редактор общих настроек", ex);
                    MessageBox.Show("Не удалось открыть настройки модулей: " + ex.Message, "Модули",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return null;
                }
            }
        }

        public bool IsValid(string settingsItemValue) => SettingsEditor.Validate(settingsItemValue) == null;

        public void SetValueProvider(ISettingValueProvider settingValueProvider)
        {
            _valueProvider = settingValueProvider;
        }

        /// <summary>Подписка на значение раздела: каждое изменение сразу применяется (<see cref="ModuleConfig.ApplyCommon"/>).</summary>
        internal static IDisposable Subscribe(IPersonalSettings settings)
        {
            if (settings == null)
                return null;
            try
            {
                return settings.SubscribeSetting(SettingKey).Subscribe(new Observer());
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось подписаться на общие настройки «Модули»", ex);
                return null;
            }
        }

        private sealed class Observer : IObserver<KeyValuePair<string, string>>
        {
            public void OnNext(KeyValuePair<string, string> value)
            {
                if (value.Key == SettingKey)
                    ModuleConfig.ApplyCommon(value.Value);
            }

            public void OnError(Exception error) => ModuleLog.Warn("Общие настройки «Модули»: ошибка подписки", error);

            public void OnCompleted()
            {
            }
        }
    }
}
