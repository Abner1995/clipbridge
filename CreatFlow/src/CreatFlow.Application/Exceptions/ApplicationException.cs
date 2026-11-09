namespace CreatFlow.Application.Exceptions;

public class ApplicationException : Exception
{
    public ApplicationException(string message) : base($"Application Exception: \"{message}\" throws from Application Layer.")
    {

    }
}
