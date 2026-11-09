namespace CreatFlow.Application.Exceptions;

/// <summary>资源不存在 → 404</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
    public NotFoundException(string message, Exception inner) : base(message, inner) { }
}
