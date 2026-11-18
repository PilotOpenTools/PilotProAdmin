using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using Ascon.Pilot.SDK;
using Ascon.Pilot.SDK.Menu;
using Pilot.ClientSdk;
using Pilot.ClientSdk.Telemetry;

namespace TestModule.Pilot
{
    /// <summary>
    /// Контекстное меню документа / «Тестовое меню» с тремя пунктами для проверки статистики в Grafana:
    /// 1) без ошибки - в дашборде только успех;
    /// 2) деление на ноль без try - ошибка попадает в статистику через Stats.Run, исключение уходит дальше в Pilot;
    /// 3) деление на ноль с try - ошибка ловится и пишется через Stats.Error.
    /// </summary>
    [Export(typeof(IMenu<ObjectsViewContext>))]
    public sealed class TestMenu : IMenu<ObjectsViewContext>
    {
        private const string DocumentType = "document";
        private const string Root = "Test";
        private const string OkItem = "Test_Ok";
        private const string NoTryItem = "Test_DivideNoTry";
        private const string WithTryItem = "Test_DivideWithTry";

        private const string OkAction = "Тест: всё хорошо";
        private const string NoTryAction = "Тест: деление на ноль без try";
        private const string WithTryAction = "Тест: деление на ноль с try";

        private static readonly ModuleStats Stats = ModuleStats.For<TestMenu>();
        private static readonly ModuleLogger Log = new ModuleLogger("TestModule");

        public void Build(IMenuBuilder builder, ObjectsViewContext context)
        {
            try
            {
                var selected = context.SelectedObjects?.ToList();
                if (selected == null || selected.Count != 1 || selected[0].Type?.Name != DocumentType)
                    return;
                if (builder.ItemNames.Contains(Root))
                    return;

                var sub = builder.AddItem(Root, builder.Count).WithHeader("Тестовое меню").WithSubmenu();
                sub.AddItem(OkItem, 0).WithHeader("1. Всё хорошо");
                sub.AddItem(NoTryItem, 1).WithHeader("2. Деление на ноль без try");
                sub.AddItem(WithTryItem, 2).WithHeader("3. Деление на ноль с try");
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось построить тестовое меню", ex);
            }
        }

        public void OnMenuItemClick(string name, ObjectsViewContext context)
        {
            switch (name)
            {
                case OkItem:
                    Stats.Run(OkAction, () => { });
                    MessageBox.Show("Всё хорошо. В Grafana ошибки не появится.", "Тестовое меню");
                    break;

                case NoTryItem:
                    // Без try: Stats.Run запишет ошибку в статистику и пробросит исключение дальше, в Pilot.
                    Stats.Run(NoTryAction, () => Divide(10, Zero()));
                    break;

                case WithTryItem:
                    try
                    {
                        Divide(10, Zero());
                    }
                    catch (Exception ex)
                    {
                        Stats.Error(WithTryAction, ex);
                        Log.Error(WithTryAction, ex);
                        MessageBox.Show("Поймали исключение: " + ex.Message + "\nОшибка записана в Grafana.", "Тестовое меню");
                    }
                    break;
            }
        }

        // Ноль берётся из метода, чтобы компилятор не заметил деление на константу.
        private static int Zero() => (int)Math.Floor(0.5);

        private static int Divide(int a, int b) => a / b;
    }
}
