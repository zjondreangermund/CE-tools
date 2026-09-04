using System;

namespace CETools.Core
{
    /// <summary>
    /// CAD-independent scheduling gate used to prevent CE dynamic-refresh feedback
    /// loops while still allowing a real user geometry edit to force one refresh.
    /// </summary>
    public sealed class DynamicRefreshGate
    {
        private readonly TimeSpan _cooldown;

        public DynamicRefreshGate(TimeSpan cooldown)
        {
            if (cooldown < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cooldown));
            _cooldown = cooldown;
        }

        public bool Busy { get; private set; }
        public bool Pending { get; private set; }
        public DateTime SuppressUntilUtc { get; private set; } = DateTime.MinValue;

        public bool TryQueue(DateTime utcNow, bool forceGeometryEdit = false)
        {
            if (Busy) return false;
            if (!forceGeometryEdit && utcNow < SuppressUntilUtc) return false;
            bool wasPending = Pending;
            Pending = true;
            return !wasPending;
        }

        public bool TryBeginRefresh(DateTime utcNow)
        {
            if (Busy || !Pending) return false;
            Busy = true;
            Pending = false;
            return true;
        }

        public void EndRefresh(DateTime utcNow)
        {
            Busy = false;
            Pending = false;
            SuppressUntilUtc = utcNow + _cooldown;
        }

        public void CancelPending(DateTime utcNow)
        {
            Pending = false;
            SuppressUntilUtc = utcNow + _cooldown;
        }

        public static bool IsRefreshOnlyCommand(string command)
        {
            string value = Normalize(command);
            return value == "CE_DYNAMICREFRESHALL" ||
                   value == "CE_GRIDSETTINGOUTREFRESH" ||
                   value == "CE_SITEGRIDREFRESH" ||
                   value == "CE_REFRESHALL";
        }

        public static bool IsGeometryEditCommand(string command)
        {
            string value = Normalize(command);
            if (value.IndexOf("GRIP", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return value == "MOVE" ||
                   value == "STRETCH" ||
                   value == "PEDIT" ||
                   value == "SCALE" ||
                   value == "ROTATE" ||
                   value == "ALIGN";
        }

        private static string Normalize(string command)
        {
            return (command ?? string.Empty).Trim().TrimStart('_', '.').ToUpperInvariant();
        }
    }
}
