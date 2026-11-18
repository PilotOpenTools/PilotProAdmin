using Ascon.Pilot.DataClasses;
using Ascon.Pilot.DataModifier;
using Ascon.Pilot.ServerExtensions.SDK;
using System;
using System.ComponentModel.Composition;
using System.Threading.Tasks;

namespace TestServerAction
{
    [Export(typeof(IServerActivity))]
    public class TestServerAction : IServerActivity
    {
        public string Name => "TestServerAction";
        public string VERSION => typeof(TestServerAction).Assembly.GetName().Version.ToString(3);

        public async Task RunAsync(
            IModifierBase modifier, IModifierBackend backend,
            IServerActivityContext ctx, IAutomationEventContext ev)
        {
            INObject source = ev.Source;
            if (source == null) return;
            // Ошибки не глушим: хост запишет их в журнал и статистику модуля.
            modifier.EditObject(source).SetAttribute("documentVersion", DateTime.UtcNow);
        }
    }
}
