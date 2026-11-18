using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ascon.Pilot.SDK;
using Pilot.ClientSdk.Infrastructure;

namespace Pilot.ClientSdk.Pilot
{
    /// <summary>
    /// Редактор раздела «Модули общие настройки». Каждое изменение сразу пишется в значение настройки
    /// (<see cref="ISettingValueProvider.SetValue"/>), Pilot сохраняет его кнопкой «ОК»/«Применить» окна настроек.
    /// Окно собирается в коде, без XAML - как <see cref="Startup.StartupWindow"/>.
    /// </summary>
    internal sealed class SettingsEditor : UserControl
    {
        private static readonly Brush Gray = Frozen(0x80, 0x80, 0x80);
        private static readonly Brush Green = Frozen(0x2E, 0x7D, 0x32);
        private static readonly Brush Red = Frozen(0xC6, 0x28, 0x28);

        private static readonly (string Mode, string Title)[] WindowModes =
        {
            (ModuleConfig.WindowAlways, "Показывать при каждом запуске"),
            (ModuleConfig.WindowOnProblems, "Только если модуль не загрузился или упал"),
            (ModuleConfig.WindowNever, "Не показывать (итог только в лог и Grafana)"),
        };

        private readonly ISettingValueProvider _provider;
        private readonly CheckBox _statsEnabled = new CheckBox { Content = "Отправлять статистику использования модулей" };
        private readonly TextBox _statsUrl = new TextBox();
        private readonly TextBlock _check = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _apiKey = new TextBox();
        private readonly TextBox _flushSeconds = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly CheckBox _reportLoaded = new CheckBox { Content = "При запуске клиента отправлять список загруженных модулей и их версии" };
        private readonly ComboBox _windowMode = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 340 };
        private readonly TextBox _lockSeconds = new TextBox { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly CheckBox _autoClose = new CheckBox { Content = "Закрывать окно само, если все модули запустились без ошибок" };
        private readonly TextBox _ignored = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly CheckBox _trayMenu = new CheckBox { Content = "Пункты «Модули версии / загрузка» в меню трея" };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private bool _loading;

        public SettingsEditor(ISettingValueProvider provider)
        {
            _provider = provider;
            foreach (var mode in WindowModes)
                _windowMode.Items.Add(new ComboBoxItem { Content = mode.Title, Tag = mode.Mode });

            var root = new StackPanel { Margin = new Thickness(4) };
            root.Children.Add(Group("Статистика использования (Grafana)",
                _statsEnabled,
                Label("Адрес сервиса статистики (POST …/api/module-events):"),
                UrlRow(),
                Label("API-ключ сервиса (если задан в report-metrics-collector):"),
                _apiKey,
                Label("Интервал отправки, секунд:"),
                _flushSeconds,
                Spaced(_reportLoaded)));
            root.Children.Add(Group("Окно «Загрузка модулей» при запуске клиента",
                _windowMode,
                Label("Сколько ждать загрузки модулей, секунд (окно до этого не закрывается):"),
                _lockSeconds,
                Spaced(_autoClose),
                Label("Не проверять модули - по одному в строке (часть названия модуля или имени сборки):"),
                _ignored));
            root.Children.Add(Group("Прочее", _trayMenu));
            var reset = new Button { Content = "Очистить раздел (действуют настройки из файла Pilot.ClientSdk.config.json)", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0) };
            reset.Click += (s, e) => Reset();
            root.Children.Add(reset);
            root.Children.Add(_status);
            root.Children.Add(new TextBlock
            {
                Foreground = Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Text = "Настройки действуют на всех пользователей и важнее файла Pilot.ClientSdk.config.json. " +
                       "Статистика и трей применяются у пользователей сразу, окно загрузки - при следующем запуске клиента.",
            });
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            Load();

            foreach (var box in new[] { _statsEnabled, _reportLoaded, _autoClose, _trayMenu })
            {
                box.Checked += (s, e) => Save();
                box.Unchecked += (s, e) => Save();
            }
            foreach (var text in new[] { _statsUrl, _apiKey, _flushSeconds, _lockSeconds, _ignored })
                text.TextChanged += (s, e) => Save();
            _windowMode.SelectionChanged += (s, e) => Save();
        }

        /// <summary>Проверка значения настройки: null - значение подходит, иначе текст ошибки.</summary>
        internal static string Validate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            ModuleConfig config;
            try
            {
                config = ModuleConfig.Parse(value);
            }
            catch (Exception ex)
            {
                return "Значение не является настройками модулей: " + ex.GetBaseException().Message;
            }
            return Validate(config);
        }

        private static string Validate(ModuleConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config.StatsUrl) && !IsHttpUrl(config.StatsUrl))
                return "Адрес сервиса статистики должен начинаться с http:// или https://.";
            if (config.StatsEnabled && string.IsNullOrWhiteSpace(config.StatsUrl))
                return "Статистика включена, но адрес сервиса не задан.";
            if (config.FlushSeconds < 1 || config.FlushSeconds > 3600)
                return "Интервал отправки - от 1 до 3600 секунд.";
            if (config.StartupLockSeconds < 0 || config.StartupLockSeconds > 600)
                return "Ожидание загрузки модулей - от 0 до 600 секунд.";
            return null;
        }

        private void Load()
        {
            _loading = true;
            try
            {
                var value = _provider?.GetValue();
                ModuleConfig config = null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    try
                    {
                        config = ModuleConfig.Parse(value);
                    }
                    catch (Exception ex)
                    {
                        ShowStatus("Сохранённое значение не разобрано, показаны настройки из файла: " + ex.GetBaseException().Message, Red);
                    }
                }
                if (config == null)
                {
                    // Раздел пуст: показываем то, что действует сейчас, - администратор правит от текущих значений.
                    config = ModuleConfig.File.Clone();
                    if (_status.Text.Length == 0)
                        ShowStatus("Раздел ещё не заполнен - показаны настройки из файла Pilot.ClientSdk.config.json. Любое изменение сохранит их в общие настройки.", Gray);
                }

                _statsEnabled.IsChecked = config.StatsEnabled;
                _statsUrl.Text = config.StatsUrl ?? "";
                _apiKey.Text = config.ApiKey ?? "";
                _flushSeconds.Text = config.FlushSeconds.ToString(CultureInfo.InvariantCulture);
                _reportLoaded.IsChecked = config.ReportLoadedModules;
                _windowMode.SelectedIndex = Math.Max(0, Array.FindIndex(WindowModes, m => m.Mode == config.WindowMode));
                _lockSeconds.Text = config.StartupLockSeconds.ToString(CultureInfo.InvariantCulture);
                _autoClose.IsChecked = config.StartupAutoClose;
                _ignored.Text = string.Join(Environment.NewLine, config.IgnoredModules ?? Array.Empty<string>());
                _trayMenu.IsChecked = config.TrayMenu;
            }
            finally
            {
                _loading = false;
            }
        }

        private void Save()
        {
            if (_loading || _provider == null)
                return;

            if (!int.TryParse(_flushSeconds.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var flush))
            {
                ShowStatus("Интервал отправки - целое число секунд. Не сохранено.", Red);
                return;
            }
            if (!int.TryParse(_lockSeconds.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lockSeconds))
            {
                ShowStatus("Ожидание загрузки модулей - целое число секунд. Не сохранено.", Red);
                return;
            }

            var config = new ModuleConfig
            {
                StatsEnabled = _statsEnabled.IsChecked == true,
                StatsUrl = string.IsNullOrWhiteSpace(_statsUrl.Text) ? null : _statsUrl.Text.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(_apiKey.Text) ? null : _apiKey.Text.Trim(),
                FlushSeconds = flush,
                ReportLoadedModules = _reportLoaded.IsChecked == true,
                StartupWindowMode = (_windowMode.SelectedItem as ComboBoxItem)?.Tag as string ?? ModuleConfig.WindowAlways,
                StartupLockSeconds = lockSeconds,
                StartupAutoClose = _autoClose.IsChecked == true,
                IgnoredModules = _ignored.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                TrayMenu = _trayMenu.IsChecked == true,
            };

            var error = Validate(config);
            if (error != null)
            {
                ShowStatus(error + " Не сохранено.", Red);
                return;
            }
            _provider.SetValue(config.ToJson());
            ShowStatus("Изменения будут сохранены кнопкой «ОК» или «Применить».", Gray);
        }

        private void Reset()
        {
            _provider?.SetValue(string.Empty);
            _status.Text = "";
            Load();
            ShowStatus("Раздел очищен: после «ОК» у пользователей будут действовать настройки из файла Pilot.ClientSdk.config.json.", Gray);
        }

        private FrameworkElement UrlRow()
        {
            var check = new Button { Content = "Проверить", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(6, 0, 0, 0) };
            check.Click += async (s, e) => await CheckUrlAsync(check);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(check, 1);
            grid.Children.Add(_statsUrl);
            grid.Children.Add(check);

            var panel = new StackPanel();
            panel.Children.Add(grid);
            _check.Margin = new Thickness(0, 2, 0, 0);
            panel.Children.Add(_check);
            return panel;
        }

        /// <summary>GET {сервер}/health сервиса статистики - доступен ли он с этой машины.</summary>
        private async Task CheckUrlAsync(Button button)
        {
            var url = _statsUrl.Text.Trim();
            if (!IsHttpUrl(url))
            {
                SetCheck("Укажите адрес вида http://сервер:5310/api/module-events.", Red);
                return;
            }
            var health = new Uri(url).GetLeftPart(UriPartial.Authority) + "/health";
            button.IsEnabled = false;
            SetCheck("Проверка " + health + " …", Gray);
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                using (var response = await http.GetAsync(health))
                {
                    if (response.IsSuccessStatusCode)
                        SetCheck("Сервис статистики доступен с этого компьютера.", Green);
                    else
                        SetCheck($"Сервис ответил {(int)response.StatusCode} {response.ReasonPhrase}.", Red);
                }
            }
            catch (Exception ex)
            {
                SetCheck("Сервис недоступен: " + ex.GetBaseException().Message, Red);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private void SetCheck(string text, Brush brush)
        {
            _check.Text = text;
            _check.Foreground = brush;
        }

        private void ShowStatus(string text, Brush brush)
        {
            _status.Text = text;
            _status.Foreground = brush;
        }

        private static bool IsHttpUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private static GroupBox Group(string header, params UIElement[] children)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            foreach (var child in children)
            {
                if (child is FrameworkElement fe && fe.Margin == default(Thickness) && !(child is TextBlock))
                    fe.Margin = new Thickness(0, 2, 0, 2);
                panel.Children.Add(child);
            }
            return new GroupBox { Header = header, Content = panel, Margin = new Thickness(0, 0, 0, 8) };
        }

        private static TextBlock Label(string text) =>
            new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap };

        private static UIElement Spaced(FrameworkElement element)
        {
            element.Margin = new Thickness(0, 8, 0, 2);
            return element;
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
