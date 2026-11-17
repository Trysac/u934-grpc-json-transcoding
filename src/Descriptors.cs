using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;

namespace Trysac.GrpcJsonTranscoding;

/// <summary>Supported protobuf scalar, enum, and message field types.</summary>
[SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "Enum members match protobuf scalar type names.")]
public enum FieldType
{
    /// <summary>UTF-8 string field (proto3 string) or string-encoded well-known types (Timestamp, Duration).</summary>
    String,

    /// <summary>Base64-encoded binary data field (proto3 bytes).</summary>
    Bytes,

    /// <summary>Boolean field (proto3 bool).</summary>
    Bool,

    /// <summary>32-bit signed integer field (proto3 int32, sint32, sfixed32).</summary>
    Int32,

    /// <summary>64-bit signed integer field serialized as a JSON string (proto3 int64, sint64, sfixed64).</summary>
    Int64,

    /// <summary>32-bit unsigned integer field (proto3 uint32, fixed32).</summary>
    UInt32,

    /// <summary>64-bit unsigned integer field serialized as a JSON string (proto3 uint64, fixed64).</summary>
    UInt64,

    /// <summary>64-bit floating-point field (proto3 double).</summary>
    Double,

    /// <summary>32-bit floating-point field (proto3 float).</summary>
    Float,

    /// <summary>Enumerated value serialized by name (proto3 enum).</summary>
    Enum,

    /// <summary>Nested protobuf message field.</summary>
    Message,
}

/// <summary>Describes a single field in a protobuf message descriptor.</summary>
/// <param name="Name">The proto field name (typically snake_case).</param>
/// <param name="Type">The protobuf type of the field.</param>
/// <param name="Repeated">Whether the field is a repeated list.</param>
/// <param name="Message">The nested message descriptor when <paramref name="Type"/> is <see cref="FieldType.Message"/>.</param>
/// <param name="EnumValues">The allowed enum names when <paramref name="Type"/> is <see cref="FieldType.Enum"/>.</param>
public sealed record FieldDescriptor(
    string Name,
    FieldType Type,
    bool Repeated = false,
    MessageDescriptor? Message = null,
    IReadOnlyList<string>? EnumValues = null)
{
    /// <summary>Gets the proto3 lowerCamelCase JSON property name derived from <see cref="Name"/>.</summary>
    public string JsonName { get; } = ToLowerCamelCase(Name);

    internal static string ToLowerCamelCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length);
        bool capNext = false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '_')
            {
                capNext = true;
            }
            else if (capNext)
            {
                sb.Append(char.ToUpperInvariant(c));
                capNext = false;
            }
            else if (sb.Length == 0)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

/// <summary>Describes a protobuf message and its fields.</summary>
/// <param name="Name">The message type name.</param>
/// <param name="Fields">The ordered list of fields in the message.</param>
public sealed record MessageDescriptor(string Name, IReadOnlyList<FieldDescriptor> Fields)
{
    internal FieldDescriptor? FindField(string nameOrJsonName)
    {
        for (int i = 0; i < Fields.Count; i++)
        {
            var field = Fields[i];
            if (string.Equals(field.Name, nameOrJsonName, StringComparison.Ordinal) ||
                string.Equals(field.JsonName, nameOrJsonName, StringComparison.Ordinal))
            {
                return field;
            }
        }
        return null;
    }

    internal FieldDescriptor? FindFieldByProtoName(string protoName)
    {
        for (int i = 0; i < Fields.Count; i++)
        {
            var field = Fields[i];
            if (string.Equals(field.Name, protoName, StringComparison.Ordinal))
            {
                return field;
            }
        }
        return null;
    }
}

/// <summary>Configures an HTTP-to-gRPC mapping rule following <c>google.api.HttpRule</c>.</summary>
/// <param name="Method">The HTTP method (<c>GET</c>, <c>PUT</c>, <c>POST</c>, <c>DELETE</c>, <c>PATCH</c>, or a custom verb).</param>
/// <param name="Pattern">The URL path template starting with <c>/</c>.</param>
/// <param name="Body">Optional request body field selector (<c>*</c> or a top-level field name).</param>
/// <param name="ResponseBody">Optional top-level response field name to project as the HTTP response body.</param>
/// <param name="AdditionalBindings">Optional additional HTTP bindings for the same RPC method.</param>
public sealed record HttpRule(
    string Method,
    string Pattern,
    string? Body = null,
    string? ResponseBody = null,
    IReadOnlyList<HttpRule>? AdditionalBindings = null);

/// <summary>Binds a gRPC method name and its request/response descriptors to an <see cref="HttpRule"/>.</summary>
/// <param name="GrpcMethod">The gRPC method identifier.</param>
/// <param name="Rule">The HTTP transcoding rule.</param>
/// <param name="Request">The request message descriptor.</param>
/// <param name="Response">The response message descriptor.</param>
public sealed record MethodBinding(
    string GrpcMethod,
    HttpRule Rule,
    MessageDescriptor Request,
    MessageDescriptor Response);

/// <summary>Represents the result of transcoding an HTTP request into a gRPC method call and proto3 JSON payload.</summary>
/// <param name="GrpcMethod">The matched gRPC method identifier.</param>
/// <param name="Message">The transcoded request message in proto3 JSON representation.</param>
public sealed record TranscodedRequest(string GrpcMethod, JsonObject Message);
