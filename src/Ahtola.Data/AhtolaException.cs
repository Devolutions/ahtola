using Ahtola.Core;

namespace Ahtola;

public class AhtolaException : Exception
{
    public AhtolaException(string message) : base(message)
    {
    }

    internal AhtolaException(string message, Exception innerException) : base(message, innerException)
    {
    }

    internal static AhtolaException FromCore(EmbeddedSqlException exception)
        => new(exception.Message, exception);

    internal static AhtolaException FromCorePreparation(EmbeddedSqlException exception)
        => new($"Unable to prepare statement: Parse error: {exception.Message}", exception);
}
