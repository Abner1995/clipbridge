namespace CreatFlow.Application.Exceptions;

/// <summary>未登录 → 401</summary>
public class UnauthorizedException : Exception
{
    public UnauthorizedException(string message) : base(message) { }
    public UnauthorizedException(string message, Exception inner) : base(message, inner) { }
}
