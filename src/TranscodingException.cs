namespace Trysac.GrpcJsonTranscoding;

/// <summary>Represents an error that occurs during HTTP/JSON to gRPC transcoding or rule configuration.</summary>
public sealed class TranscodingException : Exception
{
    private const int MaxMessageLength = 200;

    /// <summary>Stable machine-readable error code.</summary>
    public string Code { get; }

    /// <summary>HTTP status code corresponding to the transcoding error.</summary>
    public int HttpStatus { get; }

    /// <summary>Initializes a new instance of the <see cref="TranscodingException"/> class.</summary>
    /// <param name="code">The machine-readable error code.</param>
    /// <param name="httpStatus">The HTTP status code.</param>
    /// <param name="message">The human-readable error message.</param>
    public TranscodingException(string code, int httpStatus, string message)
        : base(Truncate(message))
    {
        ArgumentNullException.ThrowIfNull(code);
        Code = code;
        HttpStatus = httpStatus;
    }

    /// <summary>Initializes a new instance of the <see cref="TranscodingException"/> class with an inner exception.</summary>
    /// <param name="code">The machine-readable error code.</param>
    /// <param name="httpStatus">The HTTP status code.</param>
    /// <param name="message">The human-readable error message.</param>
    /// <param name="innerException">The inner exception that caused this error.</param>
    public TranscodingException(string code, int httpStatus, string message, Exception innerException)
        : base(Truncate(message), innerException)
    {
        ArgumentNullException.ThrowIfNull(code);
        Code = code;
        HttpStatus = httpStatus;
    }

    private static string Truncate(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "Transcoding error.";
        }
        return message.Length <= MaxMessageLength ? message : string.Concat(message.AsSpan(0, MaxMessageLength - 3), "...");
    }
}
