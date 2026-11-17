namespace Trysac.GrpcJsonTranscoding;

/// <summary>Thrown when a required text input is empty or whitespace only.</summary>
public sealed class BlankInputException : ArgumentException
{
    /// <summary>Creates a new <see cref="BlankInputException"/> with the default message.</summary>
    public BlankInputException()
        : base("input is blank")
    {
    }

    /// <summary>Creates a new <see cref="BlankInputException"/> with a custom message.</summary>
    /// <param name="message">The error message.</param>
    public BlankInputException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a new <see cref="BlankInputException"/> with a custom message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public BlankInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
