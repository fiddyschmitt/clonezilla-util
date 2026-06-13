using Serilog.Core;
using Serilog.Events;

namespace libCommon.Logging
{
    public class SuppressConsecutiveDuplicateFilter : ILogEventFilter
    {
        private string? _lastMessage;
        private readonly object _lock = new();

        public bool IsEnabled(LogEvent logEvent)
        {
            var current = logEvent.RenderMessage();

            lock (_lock)
            {
                if (current == _lastMessage)
                {
                    return false;
                }

                _lastMessage = current;
                return true;
            }
        }
    }
}