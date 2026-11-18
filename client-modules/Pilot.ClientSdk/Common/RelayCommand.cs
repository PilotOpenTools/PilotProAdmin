using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Pilot.ClientSdk
{
    /// <summary>Команда MVVM для WPF.</summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object parameter) => _execute(parameter);
    }

    /// <summary>
    /// Асинхронная команда: пока выполняется - недоступна (двойной клик не запустит второй раз),
    /// исключение уходит в обработчик onError (по умолчанию - в лог Pilot.ClientSdk), а не роняет клиент.
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object, Task> _execute;
        private readonly Func<object, bool> _canExecute;
        private readonly Action<Exception> _onError;
        private bool _running;

        public AsyncRelayCommand(Func<object, Task> execute, Func<object, bool> canExecute = null, Action<Exception> onError = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onError = onError ?? (ex => Infrastructure.ModuleLog.Error("Ошибка команды", ex));
        }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool IsRunning => _running;

        public bool CanExecute(object parameter) => !_running && (_canExecute == null || _canExecute(parameter));

        public async void Execute(object parameter)
        {
            if (_running)
                return;
            _running = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                _onError(ex);
            }
            finally
            {
                _running = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
