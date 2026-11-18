using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Pilot.ClientSdk.Infrastructure;
using Pilot.ClientSdk.Pilot;
using Pilot.ClientSdk.Telemetry;
using Xunit;

namespace Pilot.ClientSdk.Tests
{
    /// <summary>Общие настройки Pilot «Модули»: JSON, приоритет над файлом, проверка значения, смена адреса на лету.</summary>
    public class SettingsTests
    {
        [Fact]
        public void Json_round_trip_keeps_all_fields()
        {
            var config = new ModuleConfig
            {
                StatsEnabled = false,
                StatsUrl = "http://stats:5310/api/module-events",
                ApiKey = "k",
                FlushSeconds = 42,
                ReportLoadedModules = false,
                StartupWindowMode = ModuleConfig.WindowOnProblems,
                StartupLockSeconds = 7,
                StartupAutoClose = false,
                IgnoredModules = new[] { "OpenStoradge", "Старый модуль" },
                TrayMenu = false,
            };

            var back = ModuleConfig.Parse(config.ToJson());

            Assert.False(back.StatsEnabled);
            Assert.Equal("http://stats:5310/api/module-events", back.StatsUrl);
            Assert.Equal("k", back.ApiKey);
            Assert.Equal(42, back.FlushSeconds);
            Assert.False(back.ReportLoadedModules);
            Assert.Equal(ModuleConfig.WindowOnProblems, back.WindowMode);
            Assert.True(back.StartupWindow);
            Assert.Equal(7, back.StartupLockSeconds);
            Assert.False(back.StartupAutoClose);
            Assert.Equal(new[] { "OpenStoradge", "Старый модуль" }, back.IgnoredModules);
            Assert.False(back.TrayMenu);
        }

        [Fact]
        public void Window_mode_falls_back_to_old_flag()
        {
            Assert.Equal(ModuleConfig.WindowAlways, ModuleConfig.Parse("{}").WindowMode);
            Assert.Equal(ModuleConfig.WindowNever, ModuleConfig.Parse("{\"startupWindow\":false}").WindowMode);
            Assert.Equal(ModuleConfig.WindowOnProblems, ModuleConfig.Parse("{\"startupWindow\":false,\"startupWindowMode\":\"problems\"}").WindowMode);
            Assert.Equal(ModuleConfig.WindowAlways, ModuleConfig.Parse("{\"startupWindowMode\":\"нечто\"}").WindowMode);
            Assert.True(ModuleConfig.Parse("{}").TrayMenu);
            Assert.Empty(ModuleConfig.Parse("{}").IgnoredModules);
        }

        [Fact]
        public void Ignored_modules_match_part_of_name()
        {
            var config = new ModuleConfig { IgnoredModules = new[] { "storadge" } };
            Assert.True(config.IsIgnored("0 - Хранилище - (OpenStoradge) - (CICD)", null));
            Assert.True(config.IsIgnored(null, "087OpenStoradge.ext2"));
            Assert.False(config.IsIgnored("Reports", "112Reports.ext2"));
        }

        [Fact]
        public void Common_settings_override_file_and_empty_value_restores_it()
        {
            ModuleConfig.Reset(new ModuleConfig { StatsUrl = "http://file/api/module-events" });
            try
            {
                var changes = new List<ModuleConfig>();
                ModuleConfig.Changed += changes.Add;
                Assert.Equal("http://file/api/module-events", ModuleConfig.Current.StatsUrl);

                ModuleConfig.ApplyCommon("{\"statsUrl\":\"http://pilot-settings/api/module-events\",\"startupWindowMode\":\"never\"}");
                Assert.Equal("http://pilot-settings/api/module-events", ModuleConfig.Current.StatsUrl);
                Assert.Equal(ModuleConfig.WindowNever, ModuleConfig.Current.WindowMode);
                Assert.Equal("общие настройки Pilot", ModuleConfig.Current.Source);

                ModuleConfig.ApplyCommon("{ не json");                     // битое значение не сбрасывает действующее
                Assert.Equal("http://pilot-settings/api/module-events", ModuleConfig.Current.StatsUrl);

                ModuleConfig.ApplyCommon("");
                Assert.Equal("http://file/api/module-events", ModuleConfig.Current.StatsUrl);
                Assert.Equal(2, changes.Count);
            }
            finally
            {
                ModuleConfig.Reset();
            }
        }

        [Theory]
        [InlineData("", true)]
        [InlineData("{\"statsUrl\":\"http://h:5310/api/module-events\"}", true)]
        [InlineData("{\"statsEnabled\":false}", true)]
        [InlineData("{\"statsUrl\":\"h:5310\"}", false)]
        [InlineData("{\"statsEnabled\":true}", false)]
        [InlineData("{\"statsUrl\":\"http://h\",\"flushSeconds\":0}", false)]
        [InlineData("{\"statsUrl\":\"http://h\",\"startupLockSeconds\":-1}", false)]
        [InlineData("не json", false)]
        public void Settings_value_validation(string value, bool valid)
        {
            Assert.Equal(valid, new CommonSettings().IsValid(value));
        }

        [Fact]
        public async Task Sender_switches_url_without_losing_queue()
        {
            var handler = new UrlHandler();
            var sender = new StatsSender();
            sender.Start(new ModuleConfig { StatsUrl = "http://old/api/module-events", FlushSeconds = 3600 }, null, handler);
            var stats = new ModuleStats(new ModuleIdentity("m.ext2", null, "1.0.0.0"), sender);

            stats.Use("a");
            sender.Configure(new ModuleConfig { StatsUrl = "http://new/api/module-events", FlushSeconds = 3600 });
            await sender.FlushAsync();

            Assert.Equal(new[] { "http://new/api/module-events" }, handler.Urls);

            sender.Configure(new ModuleConfig { StatsEnabled = false, StatsUrl = "http://new/api/module-events" });
            stats.Use("b");
            await sender.FlushAsync();
            Assert.Single(handler.Urls);
            Assert.Equal(0, sender.Queued);
        }

        [Fact]
        public void Editor_writes_json_on_change()
        {
            var provider = new FakeProvider();
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var editor = new SettingsEditor(provider);
                    var url = FindTextBoxes(editor)[0];
                    url.Text = "http://stats-new:5310/api/module-events";
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            Assert.Null(error);
            Assert.NotNull(provider.Value);
            Assert.Equal("http://stats-new:5310/api/module-events", ModuleConfig.Parse(provider.Value).StatsUrl);
        }

        private static List<System.Windows.Controls.TextBox> FindTextBoxes(System.Windows.DependencyObject root)
        {
            var result = new List<System.Windows.Controls.TextBox>();
            foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
            {
                if (child is System.Windows.Controls.TextBox box)
                    result.Add(box);
                if (child is System.Windows.DependencyObject d)
                    result.AddRange(FindTextBoxes(d));
            }
            return result;
        }

        private sealed class FakeProvider : Ascon.Pilot.SDK.ISettingValueProvider
        {
            public string Value { get; private set; }
            public string GetValue() => Value;
            public void SetValue(string value) => Value = value;
        }

        private sealed class UrlHandler : HttpMessageHandler
        {
            public List<string> Urls { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (Urls)
                    Urls.Add(request.RequestUri.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
