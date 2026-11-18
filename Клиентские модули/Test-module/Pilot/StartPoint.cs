using System.ComponentModel.Composition;
using Ascon.Pilot.SDK;
using Pilot.ClientSdk;

namespace TestModule.Pilot
{
    /// <summary>
    /// Точка входа модуля: получает сервисы Pilot и отдаёт их пунктам меню.
    /// IModule - по регламенту: окно «Загрузка модулей» показывает, запустился ли модуль и за сколько.
    /// </summary>
    [Export(typeof(IDataPlugin))]
    public sealed class StartPoint : IDataPlugin, IModule
    {
        private readonly IObjectsRepository _objectsRepository;
        private readonly IObjectModifier _modifier;

        [ImportingConstructor]
        public StartPoint(IObjectsRepository objectsRepository, IObjectModifier modifier)
        {
            _objectsRepository = objectsRepository;
            _modifier = modifier;
            StartupRunner.Run(this);
        }

        public static StartPoint Instance { get; private set; }

        public IObjectsRepository Repository => _objectsRepository;

        public IObjectModifier Modifier => _modifier;

        /// <summary>Инициализация модуля: всё, что раньше было в конструкторе после сохранения сервисов.</summary>
        public void Initialize()
        {
            Instance = this;
        }
    }
}
