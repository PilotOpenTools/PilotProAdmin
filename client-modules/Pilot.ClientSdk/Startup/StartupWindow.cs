using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pilot.ClientSdk.Startup
{
    /// <summary>
    /// Окно «Загрузка модулей»: список модулей пользователя, статус и время запуска каждого.
    /// Закрыть можно, когда всё проверено или прошло <c>lockSeconds</c>; до этого окно модальное и не закрывается.
    /// </summary>
    internal sealed class StartupWindow : Window
    {
        private static readonly Brush Gray = Brush(0x80, 0x80, 0x80);
        private static readonly Brush Blue = Brush(0x1E, 0x6F, 0xD9);
        private static readonly Brush Green = Brush(0x2E, 0x7D, 0x32);
        private static readonly Brush Orange = Brush(0xC7, 0x6A, 0x00);
        private static readonly Brush Red = Brush(0xC6, 0x28, 0x28);

        private readonly Func<bool, StartupState> _evaluate;
        private readonly TimeSpan _lock;
        private readonly bool _autoClose;
        private readonly DateTime _shownUtc = DateTime.UtcNow;
        private readonly DispatcherTimer _timer;
        private readonly ObservableCollection<RowView> _rows = new ObservableCollection<RowView>();
        private readonly TextBlock _header = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _note = new TextBlock { Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        private readonly ProgressBar _progress = new ProgressBar { Height = 6, Margin = new Thickness(0, 10, 0, 10), Minimum = 0, Maximum = 1 };
        private readonly TextBox _details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 54, Margin = new Thickness(0, 8, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        private readonly Button _close = new Button { Content = "Закрыть", MinWidth = 120, Padding = new Thickness(12, 4, 12, 4), IsEnabled = false, IsDefault = true };
        private readonly ListView _list = new ListView();

        private StartupState _state;
        private bool _finishedReported;
        private DateTime? _autoCloseAtUtc;

        /// <param name="evaluate">Пересчитать строки; аргумент - вышло ли время ожидания.</param>
        /// <param name="lockTime">Сколько окно нельзя закрыть, если проверка ещё идёт. Zero - можно сразу.</param>
        /// <param name="autoClose">Закрыться самому, если всё запустилось без ошибок.</param>
        public StartupWindow(Func<bool, StartupState> evaluate, TimeSpan lockTime, bool autoClose)
        {
            _evaluate = evaluate;
            _lock = lockTime;
            _autoClose = autoClose;

            Title = "Загрузка модулей";
            Width = 760;
            Height = 520;
            MinWidth = 520;
            MinHeight = 320;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = SystemColors.WindowBrush;

            // Тема Pilot (Ascon.Pilot.Theme) задаёт неявные стили ListView/ListViewItem без GridViewRowPresenter -
            // с ними колонки GridView не рисуются и в строке виден только ToString(). Явные пустые стили
            // отключают неявные, и применяются стандартные шаблоны WPF.
            _list.Style = new Style(typeof(ListView));
            _list.ItemContainerStyle = new Style(typeof(ListViewItem));
            _list.ItemsSource = _rows;
            _list.View = BuildColumns();
            _list.SelectionChanged += (s, e) => ShowDetails(_list.SelectedItem as RowView);

            _close.Click += (s, e) => Close();
            var copy = new Button { Content = "Скопировать отчёт", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            copy.Click += (s, e) => CopyReport();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(copy);
            buttons.Children.Add(_close);

            var top = new StackPanel();
            top.Children.Add(_header);
            top.Children.Add(_note);
            top.Children.Add(_progress);

            var root = new DockPanel { Margin = new Thickness(16) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            DockPanel.SetDock(_details, Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(buttons);
            root.Children.Add(_details);
            root.Children.Add(_list);
            Content = root;

            Closing += OnClosing;
            Closed += (s, e) =>
            {
                _timer.Stop();
                if (!_finishedReported && _state != null)
                {
                    _finishedReported = true;
                    Finished?.Invoke(_state);
                }
            };
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && CanClose)
                    Close();
            };

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) =>
            {
                Refresh();
                _timer.Start();
            };
        }

        /// <summary>Проверка закончилась (вызывается один раз). Аргумент - итоговое состояние.</summary>
        public event Action<StartupState> Finished;

        private TimeSpan Elapsed => DateTime.UtcNow - _shownUtc;

        private bool DeadlinePassed => Elapsed >= _lock;

        private bool CanClose => _state == null || _state.IsDone || DeadlinePassed;

        private void Refresh()
        {
            try
            {
                _state = _evaluate(DeadlinePassed);
            }
            catch (Exception ex)
            {
                _state = new StartupState(new List<RowResult>(), "Ошибка проверки модулей: " + ex.Message, true);
            }

            Sync(_state.Rows);
            var total = _state.Rows.Count;
            var done = _state.Rows.Count(r => r.IsDone);
            var errors = _state.Rows.Count(r => r.State == RowState.Error);
            var warnings = _state.Rows.Count(r => r.State == RowState.Warning);

            _progress.Maximum = Math.Max(total, 1);
            _progress.Value = _state.IsDone ? _progress.Maximum : done;
            _progress.Foreground = errors > 0 ? Red : _state.IsDone ? Green : Blue;

            if (!_state.IsDone)
                _header.Text = $"Загрузка модулей… {done} из {total}";
            else if (errors > 0)
                _header.Text = $"Модули загружены с ошибками: {errors} из {total}";
            else if (warnings > 0)
                _header.Text = $"Модули загружены: {total}, замечаний: {warnings}";
            else
                _header.Text = $"Все модули загружены: {total}";

            _note.Text = _state.Note ?? "";
            _note.Visibility = string.IsNullOrEmpty(_note.Text) ? Visibility.Collapsed : Visibility.Visible;

            var left = (int)Math.Ceiling((_lock - Elapsed).TotalSeconds);
            _close.IsEnabled = CanClose;
            _close.Content = CanClose || left <= 0 ? "Закрыть" : $"Закрыть ({left})";

            if (_list.SelectedItem is RowView selected)
                ShowDetails(selected);

            if ((_state.IsDone || DeadlinePassed) && !_finishedReported)
            {
                _finishedReported = true;
                Finished?.Invoke(_state);
                if (_autoClose && _state.IsDone && errors == 0)
                    _autoCloseAtUtc = DateTime.UtcNow.AddSeconds(1.5);
            }
            if (_autoCloseAtUtc.HasValue && DateTime.UtcNow >= _autoCloseAtUtc.Value)
            {
                _autoCloseAtUtc = null;
                Close();
            }
        }

        private void Sync(IReadOnlyList<RowResult> rows)
        {
            var keys = new HashSet<string>(rows.Select(r => r.Key));
            for (var i = _rows.Count - 1; i >= 0; i--)
                if (!keys.Contains(_rows[i].Key))
                    _rows.RemoveAt(i);

            for (var i = 0; i < rows.Count; i++)
            {
                var existing = _rows.FirstOrDefault(r => r.Key == rows[i].Key);
                if (existing == null)
                {
                    existing = new RowView(rows[i].Key);
                    _rows.Insert(Math.Min(i, _rows.Count), existing);
                }
                else if (_rows.IndexOf(existing) != i && i < _rows.Count)
                {
                    _rows.Move(_rows.IndexOf(existing), i);
                }
                existing.Update(rows[i]);
            }
        }

        private void ShowDetails(RowView row)
        {
            _details.Text = row == null ? "" : row.Details ?? "";
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!CanClose)
                e.Cancel = true;
        }

        private void CopyReport()
        {
            var sb = new StringBuilder().AppendLine(_header.Text);
            if (!string.IsNullOrEmpty(_note.Text))
                sb.AppendLine(_note.Text);
            sb.AppendLine();
            foreach (var r in _rows)
            {
                sb.Append(r.Title).Append(" | ").Append(r.Version).Append(" | ").Append(r.Status).Append(" | ").AppendLine(r.Time);
                if (!string.IsNullOrEmpty(r.Details))
                    sb.Append("    ").AppendLine(r.Details.Replace(Environment.NewLine, Environment.NewLine + "    "));
            }
            try { Clipboard.SetText(sb.ToString()); }
            catch { /* буфер обмена занят другим процессом */ }
        }

        private static GridView BuildColumns()
        {
            var view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = "Модуль", Width = 360, DisplayMemberBinding = new Binding(nameof(RowView.Title)) });
            view.Columns.Add(new GridViewColumn { Header = "Версия", Width = 90, DisplayMemberBinding = new Binding(nameof(RowView.Version)) });

            var status = new FrameworkElementFactory(typeof(TextBlock));
            status.SetBinding(TextBlock.TextProperty, new Binding(nameof(RowView.Status)));
            status.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(RowView.Color)));
            status.SetBinding(ToolTipProperty, new Binding(nameof(RowView.Details)));
            view.Columns.Add(new GridViewColumn { Header = "Статус", Width = 180, CellTemplate = new DataTemplate { VisualTree = status } });

            view.Columns.Add(new GridViewColumn { Header = "Время", Width = 70, DisplayMemberBinding = new Binding(nameof(RowView.Time)) });
            return view;
        }

        private static Brush Brush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        internal static Brush ColorOf(RowState state) =>
            state == RowState.Ok ? Green
            : state == RowState.Warning ? Orange
            : state == RowState.Error ? Red
            : state == RowState.Running ? Blue
            : Gray;
    }

    /// <summary>
    /// Строка таблицы окна. Обязательно public и не вложенный: WPF-привязки не читают свойства
    /// непубличных типов и молча показывают ToString() вместо значений.
    /// </summary>
    public sealed class RowView : INotifyPropertyChanged
    {
        internal RowView(string key) => Key = key;

        public string Key { get; }
        public string Title { get; private set; }
        public string Version { get; private set; }
        public string Status { get; private set; }
        public string Time { get; private set; }
        public string Details { get; private set; }
        public Brush Color { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        internal void Update(RowResult r)
        {
            Set(r.Title, Title, v => Title = v, nameof(Title));
            Set(r.Version ?? "", Version, v => Version = v, nameof(Version));
            Set(r.Status, Status, v => Status = v, nameof(Status));
            Set(r.Details, Details, v => Details = v, nameof(Details));
            Set(FormatTime(r.DurationMs), Time, v => Time = v, nameof(Time));
            Set(StartupWindow.ColorOf(r.State), Color, v => Color = v, nameof(Color));
        }

        public override string ToString() => Title ?? Key;

        private void Set<T>(T value, T current, Action<T> assign, string name)
        {
            if (Equals(value, current))
                return;
            assign(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static string FormatTime(long? ms) =>
            ms == null ? "" : ms < 1000 ? $"{ms} мс" : $"{ms / 1000.0:0.0} с";
    }

    /// <summary>Результат одного пересчёта для окна.</summary>
    internal sealed class StartupState
    {
        public StartupState(IReadOnlyList<RowResult> rows, string note, bool isDone)
        {
            Rows = rows;
            Note = note;
            IsDone = isDone;
        }

        public IReadOnlyList<RowResult> Rows { get; }

        /// <summary>Пояснение под заголовком (например, «список модулей с сервера не получен»).</summary>
        public string Note { get; }

        /// <summary>Ничего больше не ждём: список с сервера получен (или не получен окончательно), все строки в конечном статусе.</summary>
        public bool IsDone { get; }
    }
}
