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

    public class BootstrapNotVerifiedException : Exception
    {
        public string DatabaseId { get; }

        public BootstrapNotVerifiedException(string databaseId, string message) : base(message)
        {
            DatabaseId = databaseId;
        }

        public BootstrapNotVerifiedException(string message) : base(message)
        {
        }
    }

    public class PhysicalDatabaseMismatchException : Exception
    {
        public PhysicalDatabaseMismatchException(string message) : base(message)
        {
        }
    }
}
