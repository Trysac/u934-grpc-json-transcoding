using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trysac.GrpcJsonTranscoding;

/// <summary>Transcodes REST HTTP/JSON requests into gRPC method invocations and proto3 JSON messages, and formats responses.</summary>
public sealed class Transcoder
{
    /// <summary>Maximum allowed character length for <c>pathAndQuery</c>.</summary>
    public const int MaxPathAndQueryLength = 8_192;

    /// <summary>Maximum allowed UTF-8 byte length for a request JSON body (4 MiB).</summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    /// <summary>Maximum allowed number of query parameters in a single request.</summary>
    public const int MaxQueryParameters = 1_000;

    /// <summary>Maximum allowed field path nesting depth.</summary>
    public const int MaxFieldPathDepth = 32;

    /// <summary>Maximum allowed JSON nesting depth.</summary>
    public const int MaxJsonDepth = 64;

    private readonly List<CompiledRoute> _routes;
    private readonly Dictionary<string, MethodBinding> _methodsByName;

    /// <summary>Initializes a new <see cref="Transcoder"/> with the specified method bindings.</summary>
    /// <param name="bindings">The gRPC method bindings to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is null.</exception>
    /// <exception cref="TranscodingException">Any rule, template, or descriptor binding is invalid.</exception>
    public Transcoder(IEnumerable<MethodBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        var compiled = new List<CompiledRoute>();
        var methodsByName = new Dictionary<string, MethodBinding>(StringComparer.Ordinal);
        int declarationOrder = 0;

        foreach (var binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            methodsByName.TryAdd(binding.GrpcMethod, binding);
            CompileRuleRecursive(binding, binding.Rule, isAdditional: false, ref declarationOrder, compiled);
        }

        _routes = compiled;
        _methodsByName = methodsByName;
    }

    /// <summary>Transcodes an incoming HTTP request into the target gRPC method and proto3 JSON request object.</summary>
    /// <param name="httpMethod">The HTTP verb (for example, <c>GET</c>, <c>POST</c>, <c>PATCH</c>).</param>
    /// <param name="pathAndQuery">The request URL path and optional query string.</param>
    /// <param name="jsonBody">The optional raw JSON request body.</param>
    /// <returns>The matched <see cref="TranscodedRequest"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="httpMethod"/> or <paramref name="pathAndQuery"/> is null.</exception>
    /// <exception cref="TranscodingException">The request fails routing, validation, or transcoding limits.</exception>
    public TranscodedRequest TranscodeRequest(string httpMethod, string pathAndQuery, string? jsonBody)
    {
        ArgumentNullException.ThrowIfNull(httpMethod);
        ArgumentNullException.ThrowIfNull(pathAndQuery);

        if (pathAndQuery.Length > MaxPathAndQueryLength)
        {
            throw new TranscodingException("input_too_large", 413, "Request path and query exceeds the maximum length of 8192 characters.");
        }

        if (jsonBody is not null && Encoding.UTF8.GetByteCount(jsonBody) > MaxBodyBytes)
        {
            throw new TranscodingException("input_too_large", 413, "Request JSON body exceeds the maximum size of 4 MiB.");
        }

        string normalizedMethod = httpMethod.Trim().ToUpperInvariant();
        if (normalizedMethod.Length == 0)
        {
            throw new TranscodingException("method_not_allowed", 405, "HTTP method cannot be empty.");
        }

        string path;
        string? queryString;
        int qIndex = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        if (qIndex < 0)
        {
            path = pathAndQuery;
            queryString = null;
        }
        else
        {
            path = pathAndQuery[..qIndex];
            queryString = pathAndQuery[(qIndex + 1)..];
        }

        CompiledRoute? matchedRoute = null;
        IReadOnlyDictionary<string, string>? matchedPathBindings = null;
        bool anyPathMatched = false;

        foreach (var route in _routes)
        {
            if (route.Template.TryMatch(path, out var candidateBindings))
            {
                anyPathMatched = true;
                if (string.Equals(route.HttpMethod, normalizedMethod, StringComparison.Ordinal))
                {
                    if (matchedRoute is null || IsBetterMatch(route, matchedRoute))
                    {
                        matchedRoute = route;
                        matchedPathBindings = candidateBindings;
                    }
                }
            }
        }

        if (matchedRoute is null)
        {
            if (anyPathMatched)
            {
                throw new TranscodingException("method_not_allowed", 405, $"HTTP method '{normalizedMethod}' is not allowed for path '{path}'.");
            }
            throw new TranscodingException("not_found", 404, $"No matching route found for path '{path}'.");
        }

        var rootMessage = new JsonObject();
        var boundPaths = new HashSet<string>(StringComparer.Ordinal);

        // 1. Bind path variables
        foreach (var kvp in matchedPathBindings!)
        {
            BindPathVariable(rootMessage, matchedRoute.RequestDescriptor, kvp.Key, kvp.Value, boundPaths);
        }

        // 2. Bind body
        string? bodySpec = matchedRoute.Rule.Body;
        bool hasStarBody = string.Equals(bodySpec, "*", StringComparison.Ordinal);
        string? bodyBoundTopFieldProtoName = null;

        if (!string.IsNullOrEmpty(bodySpec))
        {
            if (hasStarBody)
            {
                if (!string.IsNullOrWhiteSpace(jsonBody))
                {
                    var parsed = ParseJsonNode(jsonBody);
                    if (parsed is not JsonObject bodyObj)
                    {
                        throw new TranscodingException("invalid_body", 400, "Request body must be a JSON object when body rule is '*'.");
                    }
                    MergeBodyObjectIntoMessage(rootMessage, matchedRoute.RequestDescriptor, bodyObj, prefixProtoPath: string.Empty, boundPaths, depth: 1);
                }
            }
            else
            {
                var bodyField = matchedRoute.RequestDescriptor.FindField(bodySpec)!;
                bodyBoundTopFieldProtoName = bodyField.Name;
                if (!string.IsNullOrWhiteSpace(jsonBody))
                {
                    var parsed = ParseJsonNode(jsonBody);
                    BindBodyToField(rootMessage, bodyField, parsed, boundPaths);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(jsonBody))
        {
            throw new TranscodingException("invalid_body", 400, "Request body is not permitted when the rule does not specify a body mapping.");
        }

        // 3. Bind query parameters
        if (!string.IsNullOrEmpty(queryString))
        {
            var queryParams = ParseQueryString(queryString);
            if (queryParams.Count > 0 && hasStarBody)
            {
                throw new TranscodingException("unknown_field", 400, "Query parameters are not allowed when the HTTP rule body is '*'.");
            }

            BindQueryParameters(
                rootMessage,
                matchedRoute.RequestDescriptor,
                queryParams,
                boundPaths,
                bodyBoundTopFieldProtoName);
        }

        var orderedMessage = OrderMessageFields(rootMessage, matchedRoute.RequestDescriptor);
        return new TranscodedRequest(matchedRoute.GrpcMethod, orderedMessage);
    }

    /// <summary>Transcodes a gRPC response <see cref="JsonObject"/> into an HTTP JSON response payload according to the method's <c>response_body</c> rule.</summary>
    /// <param name="grpcMethod">The gRPC method identifier.</param>
    /// <param name="response">The response message represented as a <see cref="JsonObject"/>.</param>
    /// <returns>The serialized JSON response string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="grpcMethod"/> or <paramref name="response"/> is null.</exception>
    /// <exception cref="TranscodingException">The gRPC method is unknown or the response fails validation.</exception>
    public string TranscodeResponse(string grpcMethod, JsonObject response)
    {
        ArgumentNullException.ThrowIfNull(grpcMethod);
        ArgumentNullException.ThrowIfNull(response);

        if (!_methodsByName.TryGetValue(grpcMethod, out var binding))
        {
            throw new TranscodingException("not_found", 404, $"Unknown gRPC method '{grpcMethod}'.");
        }

        var normalizedResponse = NormalizeAndOrderResponseObject(response, binding.Response, depth: 1);
        string? responseBodyField = binding.Rule.ResponseBody;

        if (!string.IsNullOrEmpty(responseBodyField) && !string.Equals(responseBodyField, "*", StringComparison.Ordinal))
        {
            var field = binding.Response.FindField(responseBodyField)!;
            if (normalizedResponse.TryGetPropertyValue(field.JsonName, out JsonNode? fieldNode) && fieldNode is not null)
            {
                return fieldNode.ToJsonString();
            }
            return "null";
        }

        return normalizedResponse.ToJsonString();
    }

    private static void CompileRuleRecursive(
        MethodBinding binding,
        HttpRule rule,
        bool isAdditional,
        ref int declarationOrder,
        List<CompiledRoute> output)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.Method))
        {
            throw new TranscodingException("invalid_rule", 500, "HttpRule Method cannot be empty.");
        }
        if (rule.Pattern is null)
        {
            throw new TranscodingException("invalid_template", 500, "HttpRule Pattern cannot be null.");
        }

        if (isAdditional && rule.AdditionalBindings is { Count: > 0 })
        {
            throw new TranscodingException("invalid_rule", 500, "Additional bindings cannot contain nested additional_bindings.");
        }

        var template = PathTemplate.Parse(rule.Pattern);

        // Validate path variables against Request descriptor
        foreach (string varPath in template.Variables)
        {
            ValidatePathVariableDescriptor(binding.Request, varPath);
        }

        // Validate body against Request descriptor
        if (!string.IsNullOrEmpty(rule.Body) && !string.Equals(rule.Body, "*", StringComparison.Ordinal))
        {
            var bodyField = binding.Request.FindField(rule.Body);
            if (bodyField is null)
            {
                throw new TranscodingException("invalid_rule", 500, $"HttpRule body references unknown field '{rule.Body}'.");
            }
            if (bodyField.Repeated)
            {
                throw new TranscodingException("invalid_rule", 500, $"HttpRule body cannot reference repeated field '{rule.Body}'.");
            }
        }

        // Validate response_body against Response descriptor
        if (!string.IsNullOrEmpty(rule.ResponseBody) && !string.Equals(rule.ResponseBody, "*", StringComparison.Ordinal))
        {
            var respField = binding.Response.FindField(rule.ResponseBody);
            if (respField is null)
            {
                throw new TranscodingException("invalid_rule", 500, $"HttpRule response_body references unknown field '{rule.ResponseBody}'.");
            }
        }

        output.Add(new CompiledRoute(
            binding.GrpcMethod,
            rule.Method.Trim().ToUpperInvariant(),
            template,
            rule,
            binding.Request,
            binding.Response,
            declarationOrder++));

        if (rule.AdditionalBindings is not null)
        {
            foreach (var additional in rule.AdditionalBindings)
            {
                CompileRuleRecursive(binding, additional, isAdditional: true, ref declarationOrder, output);
            }
        }
    }

    private static void ValidatePathVariableDescriptor(MessageDescriptor rootDescriptor, string fieldPath)
    {
        var parts = fieldPath.Split('.');
        if (parts.Length > MaxFieldPathDepth)
        {
            throw new TranscodingException("invalid_template", 500, "Field path exceeds maximum depth of 32.");
        }

        MessageDescriptor current = rootDescriptor;
        for (int i = 0; i < parts.Length; i++)
        {
            var field = current.FindField(parts[i]);
            if (field is null)
            {
                throw new TranscodingException("invalid_rule", 500, $"Path variable '{fieldPath}' references unknown field '{parts[i]}'.");
            }
            if (field.Repeated)
            {
                throw new TranscodingException("invalid_rule", 500, $"Path variable '{fieldPath}' cannot reference repeated field '{parts[i]}'.");
            }
            if (i < parts.Length - 1)
            {
                if (field.Type != FieldType.Message || field.Message is null)
                {
                    throw new TranscodingException("invalid_rule", 500, $"Intermediate field '{parts[i]}' in path variable '{fieldPath}' is not a message.");
                }
                current = field.Message;
            }
            else
            {
                if (field.Type == FieldType.Message)
                {
                    throw new TranscodingException("invalid_rule", 500, $"Path variable '{fieldPath}' cannot bind directly to a message field.");
                }
            }
        }
    }

    private static bool IsBetterMatch(CompiledRoute candidate, CompiledRoute currentBest)
    {
        if (PathTemplate.IsMoreSpecific(candidate.Template, currentBest.Template))
        {
            return true;
        }
        if (PathTemplate.IsMoreSpecific(currentBest.Template, candidate.Template))
        {
            return false;
        }
        return candidate.DeclarationOrder < currentBest.DeclarationOrder;
    }

    private static void BindPathVariable(
        JsonObject root,
        MessageDescriptor descriptor,
        string fieldPath,
        string rawValue,
        HashSet<string> boundPaths)
    {
        var parts = fieldPath.Split('.');
        var canonicalParts = new string[parts.Length];
        JsonObject currentObj = root;
        MessageDescriptor currentDesc = descriptor;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var field = currentDesc.FindField(parts[i])!;
            canonicalParts[i] = field.Name;
            if (currentObj[field.JsonName] is not JsonObject nextObj)
            {
                nextObj = new JsonObject();
                currentObj[field.JsonName] = nextObj;
            }
            currentObj = nextObj;
            currentDesc = field.Message!;
        }

        var leafField = currentDesc.FindField(parts[^1])!;
        canonicalParts[^1] = leafField.Name;
        string canonicalPath = string.Join('.', canonicalParts);

        if (!boundPaths.Add(canonicalPath))
        {
            throw new TranscodingException("conflicting_binding", 400, $"Field '{canonicalPath}' is bound multiple times.");
        }

        currentObj[leafField.JsonName] = ConvertScalarFromString(leafField, rawValue);
    }

    private static JsonNode ParseJsonNode(string jsonBody)
    {
        try
        {
            var docOptions = new JsonDocumentOptions
            {
                MaxDepth = MaxJsonDepth,
            };
            var node = JsonNode.Parse(jsonBody, nodeOptions: null, documentOptions: docOptions);
            if (node is null)
            {
                throw new TranscodingException("invalid_body", 400, "Request JSON body cannot be null.");
            }
            return node;
        }
        catch (TranscodingException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new TranscodingException("invalid_body", 400, "Malformed JSON in request body.", ex);
        }
    }

    private static void BindBodyToField(
        JsonObject rootMessage,
        FieldDescriptor bodyField,
        JsonNode parsedBody,
        HashSet<string> boundPaths)
    {
        if (HasConflict(boundPaths, bodyField.Name))
        {
            throw new TranscodingException("conflicting_binding", 400, $"Field '{bodyField.Name}' conflicts with an existing path binding.");
        }

        if (bodyField.Type == FieldType.Message)
        {
            if (parsedBody is not JsonObject bodyObj)
            {
                throw new TranscodingException("invalid_body", 400, $"Expected JSON object for message field '{bodyField.Name}'.");
            }
            if (bodyField.Message is null)
            {
                throw new TranscodingException("invalid_rule", 500, $"Message field '{bodyField.Name}' is missing its MessageDescriptor.");
            }

            var targetObj = rootMessage[bodyField.JsonName] as JsonObject ?? new JsonObject();
            rootMessage[bodyField.JsonName] = targetObj;
            boundPaths.Add(bodyField.Name);
            var innerBoundPaths = new HashSet<string>(StringComparer.Ordinal);
            MergeBodyObjectIntoMessage(targetObj, bodyField.Message, bodyObj, bodyField.Name, innerBoundPaths, depth: 1);
        }
        else
        {
            boundPaths.Add(bodyField.Name);
            rootMessage[bodyField.JsonName] = ConvertScalarFromJsonNode(bodyField, parsedBody);
        }
    }

    private static void MergeBodyObjectIntoMessage(
        JsonObject targetObj,
        MessageDescriptor descriptor,
        JsonObject sourceObj,
        string prefixProtoPath,
        HashSet<string> boundPaths,
        int depth)
    {
        if (depth > MaxFieldPathDepth)
        {
            throw new TranscodingException("invalid_body", 400, "JSON object nesting exceeds maximum field path depth of 32.");
        }

        foreach (var kvp in sourceObj)
        {
            var field = descriptor.FindField(kvp.Key);
            if (field is null)
            {
                throw new TranscodingException("unknown_field", 400, $"Unknown field '{kvp.Key}' in request JSON body.");
            }

            string canonicalPath = string.IsNullOrEmpty(prefixProtoPath)
                ? field.Name
                : string.Concat(prefixProtoPath, ".", field.Name);

            if (kvp.Value is null)
            {
                continue;
            }

            if (field.Repeated)
            {
                if (HasConflict(boundPaths, canonicalPath))
                {
                    throw new TranscodingException("conflicting_binding", 400, $"Field '{canonicalPath}' was already bound.");
                }
                if (kvp.Value is not JsonArray arr)
                {
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' is repeated and requires a JSON array.");
                }
                var convertedArray = new JsonArray();
                foreach (var item in arr)
                {
                    if (item is null)
                    {
                        throw new TranscodingException("invalid_value", 400, $"Repeated field '{field.Name}' cannot contain null elements.");
                    }
                    if (field.Type == FieldType.Message)
                    {
                        if (item is not JsonObject itemObj || field.Message is null)
                        {
                            throw new TranscodingException("invalid_value", 400, $"Repeated message field '{field.Name}' requires JSON object elements.");
                        }
                        var nestedTarget = new JsonObject();
                        MergeBodyObjectIntoMessage(nestedTarget, field.Message, itemObj, canonicalPath, new HashSet<string>(StringComparer.Ordinal), depth + 1);
                        convertedArray.Add(OrderMessageFields(nestedTarget, field.Message));
                    }
                    else
                    {
                        convertedArray.Add(ConvertScalarFromJsonNode(field, item));
                    }
                }
                boundPaths.Add(canonicalPath);
                targetObj[field.JsonName] = convertedArray;
            }
            else if (field.Type == FieldType.Message)
            {
                if (kvp.Value is not JsonObject nestedSource || field.Message is null)
                {
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' is a message and requires a JSON object.");
                }
                if (boundPaths.Contains(canonicalPath))
                {
                    throw new TranscodingException("conflicting_binding", 400, $"Field '{canonicalPath}' was already bound.");
                }
                var nestedTarget = targetObj[field.JsonName] as JsonObject ?? new JsonObject();
                targetObj[field.JsonName] = nestedTarget;
                MergeBodyObjectIntoMessage(nestedTarget, field.Message, nestedSource, canonicalPath, boundPaths, depth + 1);
            }
            else
            {
                if (HasConflict(boundPaths, canonicalPath))
                {
                    throw new TranscodingException("conflicting_binding", 400, $"Field '{canonicalPath}' was already bound.");
                }
                boundPaths.Add(canonicalPath);
                targetObj[field.JsonName] = ConvertScalarFromJsonNode(field, kvp.Value);
            }
        }
    }

    private static List<(string Key, string Value)> ParseQueryString(string queryString)
    {
        var result = new List<(string Key, string Value)>();
        var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (pairs.Length > MaxQueryParameters)
        {
            throw new TranscodingException("input_too_large", 400, "Query string exceeds the maximum of 1000 parameters.");
        }

        foreach (string pair in pairs)
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string rawKey;
            string rawVal;
            if (eq < 0)
            {
                rawKey = pair;
                rawVal = string.Empty;
            }
            else
            {
                rawKey = pair[..eq];
                rawVal = pair[(eq + 1)..];
            }

            string key = DecodeQueryComponent(rawKey);
            string val = DecodeQueryComponent(rawVal);
            if (key.Length == 0)
            {
                throw new TranscodingException("unknown_field", 400, "Query parameter key cannot be empty.");
            }
            result.Add((key, val));
        }

        return result;
    }

    private static string DecodeQueryComponent(string raw)
    {
        string plusConverted = raw.Replace('+', ' ');
        if (!PathTemplate.TryPercentDecode(plusConverted, preserveSlashes: false, out string decoded))
        {
            throw new TranscodingException("invalid_value", 400, "Invalid percent-encoding in query string.");
        }
        return decoded;
    }

    private static void BindQueryParameters(
        JsonObject root,
        MessageDescriptor descriptor,
        List<(string Key, string Value)> queryParams,
        HashSet<string> boundPaths,
        string? bodyBoundTopFieldProtoName)
    {
        var repeatedQueryPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, value) in queryParams)
        {
            var parts = key.Split('.');
            if (parts.Length > MaxFieldPathDepth)
            {
                throw new TranscodingException("unknown_field", 400, "Query parameter field path exceeds maximum depth of 32.");
            }

            var canonicalParts = new string[parts.Length];
            JsonObject currentObj = root;
            MessageDescriptor currentDesc = descriptor;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                var field = currentDesc.FindField(parts[i]);
                if (field is null)
                {
                    throw new TranscodingException("unknown_field", 400, $"Unknown query parameter field '{key}'.");
                }
                if (field.Repeated || field.Type != FieldType.Message || field.Message is null)
                {
                    throw new TranscodingException("unknown_field", 400, $"Intermediate field '{parts[i]}' in query parameter '{key}' is not a non-repeated message.");
                }
                canonicalParts[i] = field.Name;
                if (i == 0 && string.Equals(field.Name, bodyBoundTopFieldProtoName, StringComparison.Ordinal))
                {
                    throw new TranscodingException("conflicting_binding", 400, $"Query parameter '{key}' conflicts with body-bound field '{bodyBoundTopFieldProtoName}'.");
                }

                if (currentObj[field.JsonName] is not JsonObject nextObj)
                {
                    nextObj = new JsonObject();
                    currentObj[field.JsonName] = nextObj;
                }
                currentObj = nextObj;
                currentDesc = field.Message;
            }

            var leafField = currentDesc.FindField(parts[^1]);
            if (leafField is null)
            {
                throw new TranscodingException("unknown_field", 400, $"Unknown query parameter field '{key}'.");
            }
            if (leafField.Type == FieldType.Message)
            {
                throw new TranscodingException("invalid_value", 400, $"Query parameter '{key}' cannot bind directly to a message field.");
            }

            canonicalParts[^1] = leafField.Name;
            if (parts.Length == 1 && string.Equals(leafField.Name, bodyBoundTopFieldProtoName, StringComparison.Ordinal))
            {
                throw new TranscodingException("conflicting_binding", 400, $"Query parameter '{key}' conflicts with body-bound field '{bodyBoundTopFieldProtoName}'.");
            }

            string canonicalPath = string.Join('.', canonicalParts);

            if (leafField.Repeated)
            {
                if (boundPaths.Contains(canonicalPath) && !repeatedQueryPaths.Contains(canonicalPath))
                {
                    throw new TranscodingException("conflicting_binding", 400, $"Field '{canonicalPath}' was already bound.");
                }
                boundPaths.Add(canonicalPath);
                repeatedQueryPaths.Add(canonicalPath);

                if (currentObj[leafField.JsonName] is not JsonArray arr)
                {
                    arr = new JsonArray();
                    currentObj[leafField.JsonName] = arr;
                }
                arr.Add(ConvertScalarFromString(leafField, value));
            }
            else
            {
                if (HasConflict(boundPaths, canonicalPath))
                {
                    throw new TranscodingException("conflicting_binding", 400, $"Field '{canonicalPath}' is bound more than once.");
                }
                boundPaths.Add(canonicalPath);
                currentObj[leafField.JsonName] = ConvertScalarFromString(leafField, value);
            }
        }
    }

    private static bool HasConflict(HashSet<string> boundPaths, string canonicalPath)
    {
        if (boundPaths.Contains(canonicalPath))
        {
            return true;
        }
        string prefix = canonicalPath + ".";
        foreach (string existing in boundPaths)
        {
            if (existing.StartsWith(prefix, StringComparison.Ordinal) ||
                canonicalPath.StartsWith(existing + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static JsonValue ConvertScalarFromString(FieldDescriptor field, string rawValue)
    {
        switch (field.Type)
        {
            case FieldType.String:
                return JsonValue.Create(rawValue);

            case FieldType.Bytes:
                try
                {
                    string padded = NormalizeBase64(rawValue);
                    _ = Convert.FromBase64String(padded);
                    return JsonValue.Create(rawValue);
                }
                catch (FormatException ex)
                {
                    throw new TranscodingException("invalid_value", 400, $"Invalid base64 value for bytes field '{field.Name}'.", ex);
                }

            case FieldType.Bool:
                if (string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonValue.Create(true);
                }
                if (string.Equals(rawValue, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonValue.Create(false);
                }
                throw new TranscodingException("invalid_value", 400, $"Invalid boolean value '{rawValue}' for field '{field.Name}'.");

            case FieldType.Int32:
                if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i32))
                {
                    return JsonValue.Create(i32);
                }
                throw new TranscodingException("invalid_value", 400, $"Invalid int32 value '{rawValue}' for field '{field.Name}'.");

            case FieldType.Int64:
                if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i64))
                {
                    return JsonValue.Create(i64.ToString(CultureInfo.InvariantCulture));
                }
                throw new TranscodingException("invalid_value", 400, $"Invalid int64 value '{rawValue}' for field '{field.Name}'.");

            case FieldType.UInt32:
                if (uint.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out uint u32))
                {
                    return JsonValue.Create(u32);
                }
                throw new TranscodingException("invalid_value", 400, $"Invalid uint32 value '{rawValue}' for field '{field.Name}'.");

            case FieldType.UInt64:
                if (ulong.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u64))
                {
                    return JsonValue.Create(u64.ToString(CultureInfo.InvariantCulture));
                }
                throw new TranscodingException("invalid_value", 400, $"Invalid uint64 value '{rawValue}' for field '{field.Name}'.");

            case FieldType.Double:
                if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
                {
                    return JsonValue.Create(d);
                }
                throw new TranscodingException("invalid_value", 400, $"Invalid double value '{rawValue}' for field '{field.Name}'.");

            case FieldType.Float:
                if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && !float.IsNaN(f) && !float.IsInfinity(f))
                {
                    return JsonValue.Create(f);
                }
                throw new TranscodingException("invalid_value", 400, $"Invalid float value '{rawValue}' for field '{field.Name}'.");

            case FieldType.Enum:
                ValidateEnumString(field, rawValue);
                return JsonValue.Create(rawValue);

            default:
                throw new TranscodingException("invalid_value", 400, $"Unsupported scalar conversion for field '{field.Name}'.");
        }
    }

    private static JsonValue ConvertScalarFromJsonNode(FieldDescriptor field, JsonNode node)
    {
        if (node is not JsonValue val)
        {
            throw new TranscodingException("invalid_value", 400, $"Expected a scalar JSON value for field '{field.Name}'.");
        }

        if (val.TryGetValue<JsonElement>(out var element))
        {
            switch (field.Type)
            {
                case FieldType.String:
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        return JsonValue.Create(element.GetString()!);
                    }
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' requires a JSON string.");

                case FieldType.Bytes:
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        return ConvertScalarFromString(field, element.GetString()!);
                    }
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' requires a base64 JSON string.");

                case FieldType.Bool:
                    if (element.ValueKind == JsonValueKind.True)
                    {
                        return JsonValue.Create(true);
                    }
                    if (element.ValueKind == JsonValueKind.False)
                    {
                        return JsonValue.Create(false);
                    }
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' requires a JSON boolean.");

                case FieldType.Int32:
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int i32))
                    {
                        return JsonValue.Create(i32);
                    }
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        return ConvertScalarFromString(field, element.GetString()!);
                    }
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' requires a valid int32.");

                case FieldType.Int64:
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out long i64))
                    {
                        return JsonValue.Create(i64.ToString(CultureInfo.InvariantCulture));
                    }
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        return ConvertScalarFromString(field, element.GetString()!);
                    }
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' requires a valid int64.");

                case FieldType.UInt32:
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out uint u32))
                    {
                        return JsonValue.Create(u32);
                    }
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        return ConvertScalarFromString(field, element.GetString()!);
                    }
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' requires a valid uint32.");

                case FieldType.UInt64:
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out ulong u64))
                    {
                        return JsonValue.Create(u64.ToString(CultureInfo.InvariantCulture));
                    }
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        return ConvertScalarFromString(field, element.GetString()!);
                    }
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' requires a valid uint64.");

                case FieldType.Double:
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
                    {
                        return JsonValue.Create(d);
                    }
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        return ConvertScalarFromString(field, element.GetString()!);
                    }
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' requires a valid double.");

                case FieldType.Float:
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out float f) && !float.IsNaN(f) && !float.IsInfinity(f))
                    {
                        return JsonValue.Create(f);
                    }
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        return ConvertScalarFromString(field, element.GetString()!);
                    }
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' requires a valid float.");

                case FieldType.Enum:
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        string enumName = element.GetString()!;
                        ValidateEnumString(field, enumName);
                        return JsonValue.Create(enumName);
                    }
                    throw new TranscodingException("invalid_value", 400, $"Field '{field.Name}' requires an enum name string.");

                default:
                    throw new TranscodingException("invalid_value", 400, $"Unsupported scalar conversion for field '{field.Name}'.");
            }
        }

        // val was created from a CLR primitive (e.g. string, int, etc.)
        object? rawObj = val.GetValue<object>();
        if (rawObj is null)
        {
            throw new TranscodingException("invalid_value", 400, $"Value for field '{field.Name}' cannot be null.");
        }

        string rawStr = rawObj is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : rawObj.ToString() ?? string.Empty;

        return ConvertScalarFromString(field, rawStr);
    }

    private static void ValidateEnumString(FieldDescriptor field, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new TranscodingException("invalid_value", 400, $"Enum value for field '{field.Name}' cannot be empty.");
        }
        if (field.EnumValues is { Count: > 0 })
        {
            for (int i = 0; i < field.EnumValues.Count; i++)
            {
                if (string.Equals(field.EnumValues[i], value, StringComparison.Ordinal))
                {
                    return;
                }
            }
            throw new TranscodingException("invalid_value", 400, $"Unknown enum value '{value}' for field '{field.Name}'.");
        }
    }

    private static string NormalizeBase64(string raw)
    {
        string s = raw.Replace('-', '+').Replace('_', '/');
        int mod = s.Length % 4;
        return mod switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s,
        };
    }

    private static JsonObject OrderMessageFields(JsonObject obj, MessageDescriptor descriptor)
    {
        var ordered = new JsonObject();
        foreach (var field in descriptor.Fields)
        {
            if (obj.TryGetPropertyValue(field.JsonName, out JsonNode? val) && val is not null)
            {
                obj.Remove(field.JsonName);
                if (field.Type == FieldType.Message && !field.Repeated && val is JsonObject nestedObj && field.Message is not null)
                {
                    ordered[field.JsonName] = OrderMessageFields(nestedObj, field.Message);
                }
                else
                {
                    ordered[field.JsonName] = val.DeepClone();
                }
            }
        }
        return ordered;
    }

    private static JsonObject NormalizeAndOrderResponseObject(JsonObject source, MessageDescriptor descriptor, int depth)
    {
        if (depth > MaxFieldPathDepth)
        {
            throw new TranscodingException("invalid_value", 400, "Response object exceeds maximum nesting depth of 32.");
        }

        var keyedByJsonName = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var kvp in source)
        {
            var field = descriptor.FindField(kvp.Key);
            if (field is null)
            {
                throw new TranscodingException("unknown_field", 400, $"Unknown field '{kvp.Key}' in response message.");
            }
            keyedByJsonName[field.JsonName] = kvp.Value;
        }

        var ordered = new JsonObject();
        foreach (var field in descriptor.Fields)
        {
            if (!keyedByJsonName.TryGetValue(field.JsonName, out JsonNode? node) || node is null)
            {
                continue;
            }

            if (field.Repeated)
            {
                if (node is not JsonArray arr)
                {
                    throw new TranscodingException("invalid_value", 400, $"Response field '{field.Name}' is repeated and must be a JSON array.");
                }
                var outArr = new JsonArray();
                foreach (var item in arr)
                {
                    if (item is null)
                    {
                        throw new TranscodingException("invalid_value", 400, $"Repeated response field '{field.Name}' cannot contain null.");
                    }
                    if (field.Type == FieldType.Message)
                    {
                        if (item is not JsonObject itemObj || field.Message is null)
                        {
                            throw new TranscodingException("invalid_value", 400, $"Repeated message field '{field.Name}' requires JSON objects.");
                        }
                        outArr.Add(NormalizeAndOrderResponseObject(itemObj, field.Message, depth + 1));
                    }
                    else
                    {
                        outArr.Add(ConvertScalarFromJsonNode(field, item));
                    }
                }
                ordered[field.JsonName] = outArr;
            }
            else if (field.Type == FieldType.Message)
            {
                if (node is not JsonObject nestedObj || field.Message is null)
                {
                    throw new TranscodingException("invalid_value", 400, $"Response field '{field.Name}' is a message and must be a JSON object.");
                }
                ordered[field.JsonName] = NormalizeAndOrderResponseObject(nestedObj, field.Message, depth + 1);
            }
            else
            {
                ordered[field.JsonName] = ConvertScalarFromJsonNode(field, node);
            }
        }

        return ordered;
    }

    private sealed record CompiledRoute(
        string GrpcMethod,
        string HttpMethod,
        PathTemplate Template,
        HttpRule Rule,
        MessageDescriptor RequestDescriptor,
        MessageDescriptor ResponseDescriptor,
        int DeclarationOrder);
}
