namespace CreatFlow.Application.Exceptions;

/// <summary>无权限 → 403</summary>
public class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message) { }
    public ForbiddenException(string message, Exception inner) : base(message, inner) { }
}
