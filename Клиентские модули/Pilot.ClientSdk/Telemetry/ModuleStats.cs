using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Pilot.ClientSdk.Telemetry
{
    /// <summary>
    /// Статистика использования модуля / сервис сбора / PostgreSQL / Grafana (дашборд «Pilot - модули»).
    /// <code>
    /// private static readonly ModuleStats Stats = ModuleStats.For&lt;StartPoint&gt;();
    ///
    /// public void OnMenuItemClick(string name, ObjectsViewContext context)
    /// {
    ///     if (name == "CreateBlanks")
    ///         Stats.Run("Создание бланков", () => CreateBlanks(context));     // время, успех/ошибка
    /// }
    ///
    /// await Stats.RunAsync("Выгрузка в Storadge", async op =>
    /// {
    ///     var files = await ExportAsync();
    ///     op.Items = files.Count;                                            // сколько обработано
    /// });
    ///
    /// Stats.Use("Открыть справку");                                           // просто факт использования
    /// </code>
    /// Модуль, версия и пользователь подставляются сами. Отправка идёт в фоне пачками,
    /// работа модуля сеть не ждёт; ошибки статистики только пишутся в лог Pilot.ClientSdk.
    /// </summary>
    public sealed class ModuleStats
    {
        private static readonly ConcurrentDictionary<string, ModuleStats> Cache = new ConcurrentDictionary<string, ModuleStats>();

        private readonly StatsSender _sender;

        internal ModuleStats(ModuleIdentity identity, StatsSender sender = null)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _sender = sender;
        }

        public ModuleIdentity Identity { get; }

        private StatsSender Sender => _sender ?? StatsSender.Default;

        /// <summary>Статистика модуля, которому принадлежит сборка.</summary>
        public static ModuleStats For(Assembly assembly)
        {
            var identity = ModuleIdentity.Of(assembly);
            return Cache.GetOrAdd(identity.Module, _ => new ModuleStats(identity));
        }

        /// <summary>Статистика модуля, в сборке которого объявлен <typeparamref name="T"/>.</summary>
        public static ModuleStats For<T>() => For(typeof(T).Assembly);

        /// <summary>Статистика модуля, из которого вызвано свойство.</summary>
        public static ModuleStats Current
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get => For(Assembly.GetCallingAssembly());
        }

        /// <summary>Факт использования (успешно, без длительности).</summary>
        public void Use(string action, long? items = null) =>
            Send(action, ModuleEventStatus.Success, null, items, null);

        /// <summary>Ошибка действия, пойманная модулем самостоятельно.</summary>
        public void Error(string action, Exception ex, long? durationMs = null) =>
            Send(action, ModuleEventStatus.Error, durationMs, null, ex);

        /// <summary>Пользователь отменил действие.</summary>
        public void Cancelled(string action, long? durationMs = null) =>
            Send(action, ModuleEventStatus.Cancelled, durationMs, null, null);

        /// <summary>
        /// Замер действия через using: Dispose без <see cref="ModuleOperation.Fail"/> / <see cref="ModuleOperation.Cancel"/> - успех.
        /// Для кода с исключениями удобнее <see cref="Run(string, Action)"/>: он сам поймает и запишет ошибку.
        /// </summary>
        public ModuleOperation Start(string action) => new ModuleOperation(this, action);

        /// <summary>Выполнить и записать: время, успех, ошибка (исключение пробрасывается дальше), отмена (OperationCanceledException).</summary>
        public void Run(string action, Action body) => Run(action, _ => body());

        public void Run(string action, Action<ModuleOperation> body)
        {
            using (var op = Start(action))
            {
                try
                {
                    body(op);
                }
                catch (Exception ex)
                {
                    op.Fail(ex);
                    throw;
                }
            }
        }

        public T Run<T>(string action, Func<T> body)
        {
            var result = default(T);
            Run(action, _ => { result = body(); });
            return result;
        }

        public Task RunAsync(string action, Func<Task> body) => RunAsync(action, _ => body());

        public async Task RunAsync(string action, Func<ModuleOperation, Task> body)
        {
            using (var op = Start(action))
            {
                try
                {
                    await body(op).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    op.Fail(ex);
                    throw;
                }
            }
        }

        public async Task<T> RunAsync<T>(string action, Func<Task<T>> body)
        {
            var result = default(T);
            await RunAsync(action, async _ => { result = await body().ConfigureAwait(true); }).ConfigureAwait(true);
            return result;
        }

        internal void Send(string action, string status, long? durationMs, long? items, Exception ex)
        {
            try
            {
                var e = ModuleEvent.For(Identity, ModuleEventKind.Action, action);
                e.Status = status;
                e.DurationMs = durationMs;
                e.Items = items;
                if (ex != null)
                    e.SetError(ex);
                Sender.Enqueue(e);
                // Ошибку отправляем сразу: если за ней клиент закроется или упадёт, она не потеряется в очереди.
                if (status == ModuleEventStatus.Error)
                    Sender.FlushInBackground();
            }
            catch
            {
                // статистика не должна ломать модуль
            }
        }

        /// <summary>Событие «модуль загружен» (отправляет сам Pilot.ClientSdk для всех модулей при старте клиента).</summary>
        internal void Loaded()
        {
            try { Sender.Enqueue(ModuleEvent.For(Identity, ModuleEventKind.Load, null)); }
            catch { /* статистика не должна ломать модуль */ }
        }
    }

    /// <summary>Одно замеряемое действие (см. <see cref="ModuleStats.Start"/>).</summary>
    public sealed class ModuleOperation : IDisposable
    {
        private readonly ModuleStats _stats;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private string _status = ModuleEventStatus.Success;
        private Exception _error;
        private bool _done;

        internal ModuleOperation(ModuleStats stats, string action)
        {
            _stats = stats;
            Action = action;
        }

        public string Action { get; }

        /// <summary>Сколько объектов/файлов/строк обработано (попадёт в Grafana).</summary>
        public long? Items { get; set; }

        /// <summary>Действие закончилось ошибкой. OperationCanceledException считается отменой.</summary>
        public void Fail(Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                Cancel();
                return;
            }
            _status = ModuleEventStatus.Error;
            _error = ex;
        }

        public void Cancel() => _status = ModuleEventStatus.Cancelled;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            _watch.Stop();
            _stats.Send(Action, _status, _watch.ElapsedMilliseconds, Items, _error);
        }
    }
}
