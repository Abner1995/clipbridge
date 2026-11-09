namespace CreatFlow.Application.Exceptions;

/// <summary>参数校验失败 → 400</summary>
public class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
    public ValidationException(string message, Exception inner) : base(message, inner) { }
}
