using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Pilot.ClientSdk;
using Pilot.ClientSdk.Infrastructure;
using Xunit;

namespace Pilot.ClientSdk.Tests
{
    public class ApiTests
    {
        [Theory]
        [InlineData("a\"b", "\"a\\\"b\"")]
        [InlineData("a\\b", "\"a\\\\b\"")]
        [InlineData("1\n2\r3\t4", "\"1\\n2\\r3\\t4\"")]
        [InlineData("\u0001", "\"\\u0001\"")]
        [InlineData("Привет", "\"Привет\"")]
        public void Json_escapes_string(string value, string expected)
        {
            var sb = new StringBuilder();
            Json.Escape(sb, value);
            Assert.Equal(expected, sb.ToString());
        }

        [Fact]
        public void Json_skips_null_properties()
        {
            var sb = new StringBuilder();
            Json.Prop(sb, "a", (string)null);
            Json.Prop(sb, "b", (long?)null);
            Json.Prop(sb, "c", 5L);
            Assert.Equal("\"c\":5,", sb.ToString());
        }

        [Fact]
        public void RelayCommand_executes_with_parameter_and_checks_can_execute()
        {
            object got = null;
            var enabled = false;
            var command = new RelayCommand(p => got = p, _ => enabled);

            Assert.False(command.CanExecute(null));
            enabled = true;
            Assert.True(command.CanExecute(null));
            command.Execute("x");
            Assert.Equal("x", got);
            Assert.Throws<ArgumentNullException>(() => new RelayCommand(null));
        }

        [Fact]
        public async Task AsyncRelayCommand_blocks_reentry_and_reports_error()
        {
            var gate = new TaskCompletionSource<bool>();
            var runs = 0;
            Exception reported = null;
            var command = new AsyncRelayCommand(async _ => { Interlocked.Increment(ref runs); await gate.Task; throw new InvalidOperationException("boom"); },
                onError: ex => reported = ex);

            command.Execute(null);
            Assert.True(command.IsRunning);
            Assert.False(command.CanExecute(null));
            command.Execute(null); // второй запуск, пока идёт первый, игнорируется
            Assert.Equal(1, runs);

            gate.SetResult(true);
            for (var i = 0; i < 200 && command.IsRunning; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);

            Assert.False(command.IsRunning);
            Assert.True(command.CanExecute(null));
            Assert.IsType<InvalidOperationException>(reported);
        }
    }
}
