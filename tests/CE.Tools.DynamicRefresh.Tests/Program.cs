using System;
using CETools.Core;

namespace CETools.DynamicRefresh.Tests
{
    internal static class Program
    {
        private static int _tests;

        private static int Main()
        {
            try
            {
                RefreshOnlyCommandsNeverScheduleWork();
                GeometryEditCommandsAreRecognised();
                BusyRefreshRejectsObjectFeedback();
                CooldownRejectsPostRegenFeedback();
                BoundaryMoveForcesExactlyOneRefresh();
                ManualRefreshCancelClearsPendingState();
                Console.WriteLine($"CE Tools dynamic-refresh tests passed: {_tests}");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("CE Tools dynamic-refresh test failure:");
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static void RefreshOnlyCommandsNeverScheduleWork()
        {
            True(DynamicRefreshGate.IsRefreshOnlyCommand("CE_DYNAMICREFRESHALL"));
            True(DynamicRefreshGate.IsRefreshOnlyCommand("_CE_GRIDSETTINGOUTREFRESH"));
            True(DynamicRefreshGate.IsRefreshOnlyCommand("CE_SITEGRIDREFRESH"));
            True(DynamicRefreshGate.IsRefreshOnlyCommand("CE_REFRESHALL"));
            True(!DynamicRefreshGate.IsRefreshOnlyCommand("MOVE"));
            Pass();
        }

        private static void GeometryEditCommandsAreRecognised()
        {
            foreach (string command in new[] { "MOVE", "STRETCH", "PEDIT", "SCALE", "ROTATE", "ALIGN", "GRIP_STRETCH" })
                True(DynamicRefreshGate.IsGeometryEditCommand(command));
            True(!DynamicRefreshGate.IsGeometryEditCommand("CE_DYNAMICREFRESHALL"));
            Pass();
        }

        private static void BusyRefreshRejectsObjectFeedback()
        {
            DateTime now = new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc);
            var gate = new DynamicRefreshGate(TimeSpan.FromMilliseconds(750));
            True(gate.TryQueue(now));
            True(gate.TryBeginRefresh(now));
            True(gate.Busy);
            True(!gate.TryQueue(now.AddMilliseconds(10)));
            True(!gate.Pending);
            gate.EndRefresh(now.AddMilliseconds(20));
            Pass();
        }

        private static void CooldownRejectsPostRegenFeedback()
        {
            DateTime now = new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc);
            var gate = new DynamicRefreshGate(TimeSpan.FromMilliseconds(750));
            True(gate.TryQueue(now));
            True(gate.TryBeginRefresh(now));
            gate.EndRefresh(now.AddMilliseconds(20));
            True(!gate.TryQueue(now.AddMilliseconds(100)));
            True(!gate.TryQueue(now.AddMilliseconds(500)));
            True(!gate.Pending);
            True(gate.TryQueue(now.AddMilliseconds(800)));
            Pass();
        }

        private static void BoundaryMoveForcesExactlyOneRefresh()
        {
            DateTime now = new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc);
            var gate = new DynamicRefreshGate(TimeSpan.FromMilliseconds(750));
            gate.CancelPending(now);

            // ObjectModified can still be inside the anti-feedback cooldown, but
            // MOVE command-end is a real user edit and must force one pending refresh.
            True(!gate.TryQueue(now.AddMilliseconds(100), false));
            True(gate.TryQueue(now.AddMilliseconds(120), true));
            True(gate.Pending);
            True(!gate.TryQueue(now.AddMilliseconds(125), true));
            True(gate.TryBeginRefresh(now.AddMilliseconds(130)));
            True(!gate.Pending);
            gate.EndRefresh(now.AddMilliseconds(140));
            True(!gate.Pending);
            Pass();
        }

        private static void ManualRefreshCancelClearsPendingState()
        {
            DateTime now = new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc);
            var gate = new DynamicRefreshGate(TimeSpan.FromMilliseconds(750));
            True(gate.TryQueue(now));
            gate.CancelPending(now.AddMilliseconds(5));
            True(!gate.Pending);
            True(!gate.TryQueue(now.AddMilliseconds(100)));
            Pass();
        }

        private static void Pass() => _tests++;

        private static void True(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Expected condition to be true.");
        }
    }
}
