using Serilog.Core;
using Serilog.Events;

namespace libCommon.Logging
{
    public class SuppressConsecutiveDuplicateFilter : ILogEventFilter
    {
        private string? _lastMessage;

        public bool IsEnabled(LogEvent logEvent)
        {
            var current = logEvent.RenderMessage();
            if (current == _lastMessage)
            {
                return false;
            }

            _lastMessage = current;
            return true;
        }
    }
}