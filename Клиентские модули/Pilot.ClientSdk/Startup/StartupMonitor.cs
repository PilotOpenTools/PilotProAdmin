using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Ascon.Pilot.SDK;
using Pilot.ClientSdk.Infrastructure;
using Pilot.ClientSdk.Telemetry;

namespace Pilot.ClientSdk.Startup
{
    /// <summary>
    /// Проверка загрузки модулей при старте клиента: когда появится главное окно Pilot, поверх него открывается
    /// модальное окно <see cref="StartupWindow"/>
    /// (или только при проблемах / никогда - общие настройки «Модули», <see cref="ModuleConfig.WindowMode"/>). Итог пишется в лог Pilot.ClientSdk и в Grafana (действие «Проверка загрузки модулей»).
    /// </summary>
    internal static class StartupMonitor
    {
        public const string StatsAction = "Проверка загрузки модулей";
        private static readonly TimeSpan MainWindowWait = TimeSpan.FromMinutes(3);

        private static int _started;
        private static ModuleSources _sources;
        private static Task<IReadOnlyList<ExpectedModule>> _expected;

        /// <summary>
        /// Запустить проверку (один раз за сеанс клиента). Вызывается из Pilot.ClientSdk на UI-потоке.
        /// Настройки окна читаются, когда откроется главное окно Pilot, - к этому времени приходят общие настройки Pilot.
        /// </summary>
        public static void Start(IObjectsRepository repository)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;
            _sources = new ModuleSources(repository);
            _expected = _sources.LoadExpectedAsync();

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                var config = ModuleConfig.Current;
                Task.Delay(TimeSpan.FromSeconds(config.StartupLockSeconds)).ContinueWith(_ => Report(Evaluate(true)), TaskScheduler.Default);
                return;
            }

            var waited = DateTime.UtcNow;
            var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (s, e) =>
            {
                var main = Application.Current?.MainWindow;
                var ready = main != null && main.IsLoaded && main.IsVisible;
                if (!ready && DateTime.UtcNow - waited < MainWindowWait)
                    return;
                timer.Stop();
                OnMainWindowReady(dispatcher, ModuleConfig.Current);
            };
            timer.Start();
        }

        private static void OnMainWindowReady(Dispatcher dispatcher, ModuleConfig config)
        {
            var lockTime = TimeSpan.FromSeconds(config.StartupLockSeconds);
            switch (config.WindowMode)
            {
                case ModuleConfig.WindowAlways:
                    Show(lockTime, config.StartupAutoClose, modal: true);
                    break;

                case ModuleConfig.WindowOnProblems:
                    // Ждём молча, пока всё проверится (или выйдет время); окно - только если есть ошибки.
                    var started = DateTime.UtcNow;
                    var poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
                    poll.Tick += (s, e) =>
                    {
                        var deadline = DateTime.UtcNow - started >= lockTime;
                        var state = Evaluate(deadline);
                        if (!state.IsDone && !deadline)
                            return;
                        poll.Stop();
                        if (state.Rows.Any(IsProblem))
                            Show(TimeSpan.Zero, autoClose: false, modal: true);
                        else
                            Report(state);
                    };
                    poll.Start();
                    break;

                default:
                    Task.Delay(lockTime).ContinueWith(_ => Report(Evaluate(true)), TaskScheduler.Default);
                    break;
            }
        }

        private static bool IsProblem(RowResult row) =>
            row.State == RowState.Error || row.State == RowState.Waiting || row.State == RowState.Running;

        /// <summary>Открыть окно повторно (пункт трея): немодальное, закрывается сразу.</summary>
        public static void ShowReport()
        {
            if (_sources == null)
            {
                MessageBox.Show("Проверка загрузки модулей не запускалась: Pilot.ClientSdk не инициализирован.", "Модули",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Show(TimeSpan.Zero, autoClose: false, modal: false);
        }

        private static void Show(TimeSpan lockTime, bool autoClose, bool modal)
        {
            try
            {
                var window = new StartupWindow(Evaluate, lockTime, autoClose);
                var main = Application.Current?.MainWindow;
                if (main != null && main.IsVisible && !ReferenceEquals(main, window))
                {
                    window.Owner = main;
                    window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }
                if (modal)
                {
                    window.Finished += Report;
                    window.ShowDialog();
                }
                else
                {
                    window.Topmost = false;
                    window.Show();
                }
            }
            catch (Exception ex)
            {
                ModuleLog.Error("Не удалось показать окно загрузки модулей", ex);
            }
        }

        /// <summary>Текущее состояние проверки. <paramref name="deadlinePassed"/> - больше не ждём.</summary>
        private static StartupState Evaluate(bool deadlinePassed)
        {
            var expectedReady = _expected.IsCompleted;
            var expected = expectedReady && _expected.Status == TaskStatus.RanToCompletion ? _expected.Result : null;

            var config = ModuleConfig.Current;
            var rows = ModuleCheck.Evaluate(expected, _sources.PluginFolder, _sources.DllNamesIn, _sources.Loaded(),
                StartupRunner.Reports, deadlinePassed)
                .Where(r => !config.IsIgnored(r.Title, r.Key))
                .ToList();

            string note = null;
            if (!expectedReady)
                note = deadlinePassed
                    ? "Список модулей с сервера не получен за отведённое время - показаны только загруженные модули."
                    : "Получаем список модулей с сервера…";
            else if (expected == null)
                note = "Список модулей с сервера получить не удалось - показаны только загруженные модули.";

            var isDone = (expectedReady || deadlinePassed) && rows.All(r => r.IsDone);
            return new StartupState(rows, note, isDone);
        }

        private static void Report(StartupState state)
        {
            try
            {
                var problems = state.Rows.Where(IsProblem).ToList();
                var warnings = state.Rows.Where(r => r.State == RowState.Warning).ToList();
                ModuleLog.Info($"Проверка загрузки модулей: всего {state.Rows.Count}, проблем {problems.Count}, замечаний {warnings.Count}" +
                               (state.Note == null ? "" : ". " + state.Note));
                foreach (var row in problems.Concat(warnings))
                    ModuleLog.Warn($"  {row.Title} {row.Version}: {row.Status}. {row.Details}");

                var stats = ModuleStats.For(typeof(StartupMonitor).Assembly);
                var errors = state.Rows.Where(r => r.State == RowState.Error).ToList();
                if (errors.Count == 0)
                    stats.Send(StatsAction, ModuleEventStatus.Success, null, state.Rows.Count, null);
                else
                    stats.Send(StatsAction, ModuleEventStatus.Error, null, state.Rows.Count, new ModulesNotLoadedException(
                        string.Join("; ", errors.Select(r => $"{r.Title}: {r.Status}"))));
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось записать итог проверки модулей", ex);
            }
        }
    }

    /// <summary>Тип ошибки в Grafana для «модуль не скачан / не загружен / упал при запуске».</summary>
    public sealed class ModulesNotLoadedException : Exception
    {
        public ModulesNotLoadedException(string message) : base(message)
        {
        }
    }
}
