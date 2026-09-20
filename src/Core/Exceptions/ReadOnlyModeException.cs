using System;

namespace Core.Exceptions
{
    public class ReadOnlyModeException : Exception
    {
        public ReadOnlyModeException(string message) : base(message)
        {
        }

        public ReadOnlyModeException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
