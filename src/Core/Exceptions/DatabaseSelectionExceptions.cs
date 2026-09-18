using System;

namespace Core.Exceptions
{
    public class InvalidDatabaseSelectionException : Exception
    {
        public InvalidDatabaseSelectionException(string message) : base(message)
        {
        }
    }

    public class DatabaseConfigurationException : Exception
    {
        public DatabaseConfigurationException(string message) : base(message)
        {
        }
    }
}
