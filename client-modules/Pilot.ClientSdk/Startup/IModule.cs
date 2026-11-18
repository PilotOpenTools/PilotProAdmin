using System.Threading.Tasks;

namespace Pilot.ClientSdk
{
    /// <summary>
    /// Стартовый класс модуля (IDataPlugin / первый экспорт модуля). Обязателен по регламенту:
    /// по нему окно «Загрузка модулей» видит, что модуль запустился, сколько это заняло и с какой ошибкой упал.
    /// <code>
    /// [Export(typeof(IDataPlugin))]
    /// public sealed class StartPoint : IDataPlugin, IModule
    /// {
    ///     [ImportingConstructor]
    ///     public StartPoint(IObjectsRepository repository, IObjectModifier modifier)
    ///     {
    ///         _repository = repository;
    ///         _modifier = modifier;
    ///         StartupRunner.Run(this);          // последней строкой конструктора
    ///     }
    ///
    ///     public void Initialize()
    ///     {
    ///         // всё, что раньше было в конструкторе после сохранения сервисов
    ///     }
    /// }
    /// </code>
    /// Исключение из <see cref="Initialize"/> клиент не роняет: оно попадает в окно загрузки, лог Pilot.ClientSdk и Grafana.
    /// </summary>
    public interface IModule
    {
        /// <summary>Инициализация модуля. Вызывается через <see cref="StartupRunner.Run(IModule)"/> из конструктора.</summary>
        void Initialize();
    }

    /// <summary>
    /// То же, что <see cref="IModule"/>, для асинхронной инициализации (загрузка объектов, поиск, сеть).
    /// В конструкторе: <c>StartupRunner.RunAsync(this);</c> - без await, задача никогда не падает.
    /// Модуль считается запущенным, когда завершится задача <see cref="InitializeAsync"/>.
    /// </summary>
    public interface IModuleAsync
    {
        Task InitializeAsync();
    }
}
