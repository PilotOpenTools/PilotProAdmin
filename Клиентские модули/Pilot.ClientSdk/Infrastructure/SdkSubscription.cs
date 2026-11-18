using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Pilot.ClientSdk.Infrastructure
{
    /// <summary>
    /// Превращает подписку SDK (IObservable) в Task.
    /// Подписка создаётся и снимается на UI-потоке клиента - SDK рассчитан на работу из него.
    /// Таймаут и отмена не бросают исключение таймаута: возвращается то, что успели получить (partial).
    /// </summary>
    internal static class SdkSubscription
    {
        /// <summary>Подмена диспетчера для тестов; в клиенте не задаётся.</summary>
        internal static Dispatcher DispatcherOverride;

        public static Dispatcher UiDispatcher => DispatcherOverride ?? Application.Current?.Dispatcher;

        /// <param name="subscribe">Создать подписку (вызывается на UI-потоке).</param>
        /// <param name="onNext">Обработать элемент; true - результат готов.</param>
        /// <param name="result">Текущий (возможно, неполный) результат.</param>
        /// <param name="timeout">Сколько ждать ответа SDK.</param>
        /// <param name="ct">Отмена.</param>
        /// <param name="what">Название операции для лога.</param>
        public static Task<TResult> RunAsync<TItem, TResult>(
            Func<IObserver<TItem>, IDisposable> subscribe,
            Func<TItem, bool> onNext,
            Func<TResult> result,
            TimeSpan timeout,
            CancellationToken ct,
            string what)
        {
            var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sync = new object();
            IDisposable subscription = null;
            var finished = 0;

            void Finish(Action complete)
            {
                if (Interlocked.Exchange(ref finished, 1) != 0)
                    return;
                // Подписка снимается синхронно (на UI-потоке клиента), раньше, чем задача получает результат.
                var sub = subscription;
                try
                {
                    if (sub != null)
                        OnUiSync(sub.Dispose);
                }
                finally
                {
                    complete();
                }
            }

            var observer = new Observer<TItem>(
                item =>
                {
                    bool done;
                    lock (sync)
                        done = onNext(item);
                    if (done)
                        Finish(() => { lock (sync) tcs.TrySetResult(result()); });
                },
                ex => Finish(() => tcs.TrySetException(ex)),
                () => Finish(() => { lock (sync) tcs.TrySetResult(result()); }));

            var timer = new CancellationTokenSource(timeout);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
            linked.Token.Register(() =>
            {
                if (ct.IsCancellationRequested)
                    Finish(() => tcs.TrySetCanceled(ct));
                else
                {
                    ModuleLog.Warn($"{what}: нет ответа SDK за {timeout.TotalSeconds:0} с - возвращается неполный результат");
                    Finish(() => { lock (sync) tcs.TrySetResult(result()); });
                }
            });

            try
            {
                OnUiSync(() =>
                {
                    var sub = subscribe(observer);
                    subscription = sub;
                    // Подписка могла закончиться синхронно, ещё до присвоения.
                    if (Volatile.Read(ref finished) != 0)
                        sub.Dispose();
                });
            }
            catch (Exception ex)
            {
                Finish(() => tcs.TrySetException(ex));
            }

            tcs.Task.ContinueWith(_ =>
            {
                linked.Dispose();
                timer.Dispose();
            }, TaskScheduler.Default);
            return tcs.Task;
        }

        /// <summary>
        /// Синхронно дождаться задачи. На UI-потоке - во вложенном цикле сообщений (интерфейс не замирает,
        /// колбэки SDK продолжают приходить), на фоновом - обычным ожиданием.
        /// </summary>
        public static T Wait<T>(Task<T> task)
        {
            var dispatcher = UiDispatcher;
            if (dispatcher != null && dispatcher.CheckAccess() && !task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false)),
                    TaskScheduler.Default);
                if (!task.IsCompleted)
                    Dispatcher.PushFrame(frame);
            }
            return task.GetAwaiter().GetResult();
        }

        private static void OnUiSync(Action action)
        {
            var dispatcher = UiDispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.Invoke(action);
        }

        internal sealed class Observer<T> : IObserver<T>
        {
            private readonly Action<T> _next;
            private readonly Action<Exception> _error;
            private readonly Action _completed;

            public Observer(Action<T> next, Action<Exception> error, Action completed)
            {
                _next = next;
                _error = error;
                _completed = completed;
            }

            public void OnNext(T value) => _next(value);
            public void OnError(Exception error) => _error(error);
            public void OnCompleted() => _completed();
        }
    }
}
