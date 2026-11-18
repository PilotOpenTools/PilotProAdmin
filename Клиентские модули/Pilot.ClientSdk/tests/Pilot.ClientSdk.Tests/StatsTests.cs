using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pilot.ClientSdk.Infrastructure;
using Pilot.ClientSdk.Telemetry;
using System.Runtime.CompilerServices;
using Xunit;

namespace Pilot.ClientSdk.Tests
{
    /// <summary>Интеграционный тест с настоящим сервисом статистики: пропускается, если не задан адрес в CLIENTSDK_IT_URL.</summary>
    internal sealed class CollectorFactAttribute : FactAttribute
    {
        public const string UrlVariable = "CLIENTSDK_IT_URL";

        public CollectorFactAttribute([CallerFilePath] string sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
            : base(sourceFilePath, sourceLineNumber)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(UrlVariable)))
                Skip = "не задан " + UrlVariable + " (адрес сервиса статистики)";
        }
    }

    public class StatsTests
    {
        [Fact]
        public void Error_message_is_truncated_for_stats()
        {
            var e = ModuleEvent.For(new ModuleIdentity("m", null, ""), ModuleEventKind.Action, "a");
            e.SetError(new InvalidOperationException(new string('x', ModuleEvent.MaxErrorMessage + 500)));
            Assert.Equal(ModuleEvent.MaxErrorMessage, e.ErrorMessage.Length);

            e.SetError(new InvalidOperationException("короткое"));
            Assert.Equal("короткое", e.ErrorMessage);

            e.SetError(null);
            Assert.Null(e.ErrorMessage);
        }

        [Fact]
        public void Assembly_declares_previous_names_for_admin_rename()
        {
            var meta = typeof(ModuleStats).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
                .Cast<AssemblyMetadataAttribute>().Single(a => a.Key == "PreviousNames");
            Assert.Equal("Modules;113Modules.ext2", meta.Value);
        }

        [Fact]
        public void Event_json_is_snake_case_and_escaped()
        {
            var e = ModuleEvent.For(new ModuleIdentity("087OpenStoradge.ext2", "01.087 - Открыть \"Storadge\"", "1.0.1.0"), ModuleEventKind.Action, "Открыть");
            e.EventId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            e.TsUtc = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
            e.DurationMs = 1500;
            e.SetError(new InvalidOperationException("строка\nвторая"));

            var json = e.ToJson();

            Assert.Contains("\"event_id\":\"11111111-2222-3333-4444-555555555555\"", json);
            Assert.Contains("\"ts\":\"2026-09-27T10:00:00.000Z\"", json);
            Assert.Contains("\"module\":\"087OpenStoradge.ext2\"", json);
            Assert.Contains("\"module_title\":\"01.087 - Открыть \\\"Storadge\\\"\"", json);
            Assert.Contains("\"status\":\"error\"", json);
            Assert.Contains("\"duration_ms\":1500", json);
            Assert.Contains("\"error_type\":\"System.InvalidOperationException\"", json);
            Assert.Contains("\"error_message\":\"строка\\nвторая\"", json);
            Assert.DoesNotContain("\"items\"", json);
            Assert.EndsWith("}", json);
        }

        [Fact]
        public void Identity_is_taken_from_assembly_attributes()
        {
            var id = ModuleIdentity.Of(typeof(ModuleStats).Assembly);
            Assert.Equal("113Pilot.ClientSdk.ext2", id.Module);
            Assert.Equal("0 - Общий модуль для клиентских модулей - (Pilot.ClientSdk) - (CICD)", id.Title);
            Assert.Equal(ModuleVersions.Display(typeof(ModuleStats).Assembly.GetName().Version), id.Version);
            Assert.Equal(3, id.Version.Split('.').Length); // X.Y.Z
        }

        [Fact]
        public void Config_defaults_and_parse()
        {
            var c = ModuleConfig.Parse("{\"statsUrl\":\"http://h:5310/api/module-events\"}");
            Assert.True(c.StatsEnabled);
            Assert.True(c.StatsActive);
            Assert.True(c.ReportLoadedModules);
            Assert.Equal(ModuleConfig.DefaultFlushSeconds, c.FlushSeconds);

            var off = ModuleConfig.Parse("{\"statsEnabled\":false,\"statsUrl\":\"http://h\"}");
            Assert.False(off.StatsActive);
        }

        [Fact]
        public async Task Run_records_success_error_and_cancel()
        {
            var handler = new CaptureHandler();
            var sender = Started(handler);
            var stats = new ModuleStats(new ModuleIdentity("m.ext2", "Модуль", "1.2.3.0"), sender);

            stats.Run("ok", op => op.Items = 3);
            Assert.Throws<InvalidOperationException>(() => stats.Run("bad", () => throw new InvalidOperationException("x")));
            await Assert.ThrowsAsync<OperationCanceledException>(() => stats.RunAsync("cancel", async () =>
            {
                await Task.Yield();
                throw new OperationCanceledException();
            }));
            stats.Use("click");
            Assert.Equal(42, stats.Run("value", () => 42));

            await sender.FlushAsync();
            for (var i = 0; i < 50 && !string.Join("", handler.Bodies).Contains("\"action\":\"value\""); i++)
                await Task.Delay(20, TestContext.Current.CancellationToken);                          // ошибка ушла отдельной пачкой в фоне

            var body = string.Join("", handler.Bodies);
            Assert.InRange(handler.Bodies.Count, 1, 5);        // ошибка уходит сразу в фоне и делит очередь с основным потоком: пачек от 1 до числа событий
            Assert.Contains("\"action\":\"ok\",\"status\":\"success\"", body);
            Assert.Contains("\"items\":3", body);
            Assert.Contains("\"action\":\"bad\",\"status\":\"error\"", body);
            Assert.Contains("\"action\":\"cancel\",\"status\":\"cancelled\"", body);
            Assert.Contains("\"action\":\"click\",\"status\":\"success\"", body);
            Assert.Contains("\"login\":\"ivanov\"", body);
            Assert.Contains("\"lib_version\":\"" + ModuleVersions.Display(typeof(ModuleStats).Assembly.GetName().Version) + "\"", body);
            Assert.StartsWith("[", body);
            Assert.NotNull(handler.ClientTime);
        }

        [Fact]
        public async Task Events_before_start_wait_in_queue()
        {
            var handler = new CaptureHandler();
            var sender = new StatsSender();
            var stats = new ModuleStats(new ModuleIdentity("early.ext2", null, "1.0.0.0"), sender);

            stats.Use("before start");
            Assert.Equal(1, sender.Queued);

            sender.Start(new ModuleConfig { StatsUrl = "http://collector/api/module-events", FlushSeconds = 3600 },
                new ClientEnvironment { Login = "ivanov" }, handler);
            await sender.FlushAsync();

            Assert.Contains("before start", handler.Bodies.Single());
            Assert.Equal(0, sender.Queued);
        }

        [Fact]
        public async Task Disabled_stats_drop_events()
        {
            var handler = new CaptureHandler();
            var sender = new StatsSender();
            sender.Start(new ModuleConfig { StatsEnabled = false, StatsUrl = "http://collector" }, null, handler);
            new ModuleStats(new ModuleIdentity("m", null, ""), sender).Use("x");
            await sender.FlushAsync();
            Assert.Empty(handler.Bodies);
            Assert.Equal(0, sender.Queued);
        }

        [Fact]
        public async Task Unavailable_service_keeps_rest_of_queue()
        {
            var handler = new CaptureHandler { Status = HttpStatusCode.ServiceUnavailable };
            var sender = new StatsSender();
            var stats = new ModuleStats(new ModuleIdentity("m", null, ""), sender);
            for (var i = 0; i < StatsSender.BatchSize * 2 + 50; i++)
                stats.Use("a" + i);           // до старта - копятся в очереди
            sender.Start(new ModuleConfig { StatsUrl = "http://collector/api/module-events", FlushSeconds = 3600 }, null, handler);

            await sender.FlushAsync();

            // Первая пачка потеряна (503), остаток ждёт следующего тика, а не долбит недоступный сервис.
            Assert.Single(handler.Bodies);
            Assert.Equal(StatsSender.BatchSize + 50, sender.Queued);
        }

        [CollectorFact]
        public async Task Real_collector_accepts_events_when_configured()
        {
            var url = Environment.GetEnvironmentVariable(CollectorFactAttribute.UrlVariable);
            var sender = new StatsSender();
            sender.Start(new ModuleConfig { StatsUrl = url, FlushSeconds = 3600 },
                new ClientEnvironment { Login = "it-test", Person = "Интеграционный тест", Server = "http://localhost:5547/it" });
            new ModuleStats(new ModuleIdentity("999ItTest.ext2", "Интеграционный тест Pilot.ClientSdk", "1.0.0.0"), sender).Use("it");
            await sender.FlushAsync();
            Assert.Equal(0, sender.Queued);
        }

        private static StatsSender Started(CaptureHandler handler)
        {
            var sender = new StatsSender();
            sender.Start(new ModuleConfig { StatsUrl = "http://collector/api/module-events", FlushSeconds = 3600 },
                new ClientEnvironment { Login = "ivanov", Person = "Иванов Иван", Server = "http://pilot:5547/db" }, handler);
            return sender;
        }

        private sealed class CaptureHandler : HttpMessageHandler
        {
            public List<string> Bodies { get; } = new List<string>();
            public string ClientTime { get; private set; }
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content.ReadAsStringAsync();
                lock (Bodies)
                    Bodies.Add(body);
                ClientTime = request.Headers.TryGetValues("X-Client-Time", out var v) ? v.First() : null;
                return new HttpResponseMessage(Status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
        }
    }
}
