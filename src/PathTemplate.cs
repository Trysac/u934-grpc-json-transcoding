using System.Text;

namespace Trysac.GrpcJsonTranscoding;

/// <summary>Parses and matches URL path templates according to the <c>google/api/http.proto</c> path template grammar.</summary>
public sealed class PathTemplate
{
    /// <summary>Maximum allowed character length for path templates and request paths.</summary>
    public const int MaxPathLength = 8_192;

    /// <summary>Maximum allowed nesting depth for field paths.</summary>
    public const int MaxFieldPathDepth = 32;

    private readonly List<TemplateToken> _tokens;
    private readonly List<VariableCapture> _captures;
    private readonly string? _verb;

    private PathTemplate(
        List<TemplateToken> tokens,
        List<VariableCapture> captures,
        IReadOnlyList<string> variables,
        string? verb)
    {
        _tokens = tokens;
        _captures = captures;
        Variables = variables;
        _verb = verb;
    }

    /// <summary>Gets the ordered list of bound field paths declared in the template.</summary>
    public IReadOnlyList<string> Variables { get; }

    internal IReadOnlyList<TemplateToken> Tokens => _tokens;

    internal string? Verb => _verb;

    /// <summary>Parses a <c>google/api/http.proto</c> path template string.</summary>
    /// <param name="template">The path template to parse.</param>
    /// <returns>The compiled <see cref="PathTemplate"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is null.</exception>
    /// <exception cref="TranscodingException">The template exceeds limits, violates syntax rules, or declares duplicate variables.</exception>
    public static PathTemplate Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Length > MaxPathLength)
        {
            throw new TranscodingException("input_too_large", 400, "Path template exceeds the maximum length of 8192 characters.");
        }
        if (template.Length == 0 || template[0] != '/')
        {
            throw new TranscodingException("invalid_template", 500, "Path template must start with '/'.");
        }

        var (pathPart, verb) = ExtractTemplateVerb(template);
        var tokens = new List<TemplateToken>();
        var captures = new List<VariableCapture>();
        var variables = new List<string>();
        var variableSet = new HashSet<string>(StringComparer.Ordinal);

        if (pathPart.Length == 1)
        {
            // Root path "/" with no segments is invalid per Segments = Segment { "/" Segment }
            throw new TranscodingException("invalid_template", 500, "Path template must contain at least one segment.");
        }

        ParseTopLevelSegments(pathPart.AsSpan(1), tokens, captures, variables, variableSet);

        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.DoubleStar && i != tokens.Count - 1)
            {
                throw new TranscodingException("invalid_template", 500, "Wildcard '**' is only allowed as the final segment of a template.");
            }
        }

        return new PathTemplate(tokens, captures, variables, verb);
    }

    /// <summary>Attempts to match a request path against this template and extract variable bindings.</summary>
    /// <param name="path">The URL path (without query string) to match.</param>
    /// <param name="bindings">When this method returns <c>true</c>, contains the bound variable values keyed by field path.</param>
    /// <returns><c>true</c> if the path matches the template; otherwise, <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="TranscodingException">The path exceeds the maximum length of 8,192 characters.</exception>
    public bool TryMatch(string path, out IReadOnlyDictionary<string, string> bindings)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > MaxPathLength)
        {
            throw new TranscodingException("input_too_large", 413, "Request path exceeds the maximum length of 8192 characters.");
        }

        bindings = new Dictionary<string, string>();
        if (path.Length == 0 || path[0] != '/')
        {
            return false;
        }

        string pathWithoutVerb;
        if (_verb is not null)
        {
            string suffix = ":" + _verb;
            if (!path.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }
            pathWithoutVerb = path[..^suffix.Length];
        }
        else
        {
            // If the path has a custom verb suffix on the last segment, a template without a verb must not match
            // if the verb was meant as a verb delimiter. Wait: what if a literal or variable contains ':'? In http.proto,
            // custom verbs are delineated by the last ':' in the last segment.
            int lastSlash = path.LastIndexOf('/');
            int colonInLast = path.IndexOf(':', lastSlash + 1);
            if (colonInLast >= 0)
            {
                return false;
            }
            pathWithoutVerb = path;
        }

        if (pathWithoutVerb.Length <= 1)
        {
            return false;
        }

        var rawSegments = pathWithoutVerb[1..].Split('/');
        for (int i = 0; i < rawSegments.Length; i++)
        {
            if (rawSegments[i].Length == 0)
            {
                return false;
            }
        }

        int tokenCount = _tokens.Count;
        bool hasTrailingDoubleStar = tokenCount > 0 && _tokens[tokenCount - 1].Kind == TokenKind.DoubleStar;

        if (!hasTrailingDoubleStar)
        {
            if (rawSegments.Length != tokenCount)
            {
                return false;
            }
        }
        else
        {
            // '**' in http.proto matches one or more segments
            if (rawSegments.Length < tokenCount)
            {
                return false;
            }
        }

        var decodedSingleSegments = new string[rawSegments.Length];
        var decodedMultiSegments = new string[rawSegments.Length];
        for (int i = 0; i < rawSegments.Length; i++)
        {
            if (!TryPercentDecode(rawSegments[i], preserveSlashes: false, out decodedSingleSegments[i]) ||
                !TryPercentDecode(rawSegments[i], preserveSlashes: true, out decodedMultiSegments[i]))
            {
                return false;
            }
        }

        int fixedCheckCount = hasTrailingDoubleStar ? tokenCount - 1 : tokenCount;
        for (int i = 0; i < fixedCheckCount; i++)
        {
            var token = _tokens[i];
            if (token.Kind == TokenKind.Literal)
            {
                if (!string.Equals(decodedSingleSegments[i], token.Literal, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        var result = new Dictionary<string, string>(_captures.Count, StringComparer.Ordinal);
        foreach (var capture in _captures)
        {
            int start = capture.StartTokenIndex;
            int end = capture.IncludesDoubleStar
                ? rawSegments.Length - 1
                : start + capture.TokenCount - 1;

            if (!capture.IsMultiSegment)
            {
                result[capture.FieldPath] = decodedSingleSegments[start];
            }
            else
            {
                var sb = new StringBuilder();
                for (int i = start; i <= end; i++)
                {
                    if (i > start)
                    {
                        sb.Append('/');
                    }
                    sb.Append(decodedMultiSegments[i]);
                }
                result[capture.FieldPath] = sb.ToString();
            }
        }

        bindings = result;
        return true;
    }

    internal static bool IsMoreSpecific(PathTemplate left, PathTemplate right)
    {
        int minCount = Math.Min(left._tokens.Count, right._tokens.Count);
        for (int i = 0; i < minCount; i++)
        {
            int leftRank = Rank(left._tokens[i].Kind);
            int rightRank = Rank(right._tokens[i].Kind);
            if (leftRank != rightRank)
            {
                return leftRank > rightRank;
            }
        }
        if (left._tokens.Count != right._tokens.Count)
        {
            return left._tokens.Count > right._tokens.Count;
        }
        return false;

        static int Rank(TokenKind kind) => kind switch
        {
            TokenKind.Literal => 3,
            TokenKind.Star => 2,
            TokenKind.DoubleStar => 1,
            _ => 0,
        };
    }

    private static (string PathPart, string? Verb) ExtractTemplateVerb(string template)
    {
        int lastBrace = template.LastIndexOf('}');
        int lastSlash = template.LastIndexOf('/');
        int searchStart = Math.Max(lastBrace + 1, lastSlash + 1);
        int colonIndex = template.IndexOf(':', searchStart);
        if (colonIndex < 0)
        {
            return (template, null);
        }

        string verb = template[(colonIndex + 1)..];
        if (!IsValidLiteral(verb))
        {
            throw new TranscodingException("invalid_template", 500, "Path template custom verb must be a valid non-empty literal.");
        }

        string pathPart = template[..colonIndex];
        if (pathPart.EndsWith('/'))
        {
            throw new TranscodingException("invalid_template", 500, "Custom verb cannot immediately follow '/'.");
        }
        return (pathPart, verb);
    }

    private static void ParseTopLevelSegments(
        ReadOnlySpan<char> span,
        List<TemplateToken> tokens,
        List<VariableCapture> captures,
        List<string> variables,
        HashSet<string> variableSet)
    {
        int index = 0;
        while (index < span.Length)
        {
            if (span[index] == '{')
            {
                int closeBrace = span[index..].IndexOf('}');
                if (closeBrace < 0)
                {
                    throw new TranscodingException("invalid_template", 500, "Unclosed '{' in path template.");
                }
                closeBrace += index;
                if (closeBrace + 1 < span.Length && span[closeBrace + 1] != '/')
                {
                    throw new TranscodingException("invalid_template", 500, "Variable in path template must occupy whole segments.");
                }

                var varContent = span[(index + 1)..closeBrace];
                ParseVariable(varContent, tokens, captures, variables, variableSet);

                index = closeBrace + 1;
                if (index < span.Length)
                {
                    if (index == span.Length - 1)
                    {
                        throw new TranscodingException("invalid_template", 500, "Trailing '/' is not allowed in path template.");
                    }
                    index++; // skip '/'
                }
            }
            else
            {
                int nextSlash = span[index..].IndexOf('/');
                ReadOnlySpan<char> segmentSpan;
                if (nextSlash < 0)
                {
                    segmentSpan = span[index..];
                    index = span.Length;
                }
                else
                {
                    if (index + nextSlash == span.Length - 1)
                    {
                        throw new TranscodingException("invalid_template", 500, "Trailing '/' is not allowed in path template.");
                    }
                    segmentSpan = span.Slice(index, nextSlash);
                    index += nextSlash + 1;
                }

                tokens.Add(ParseSimpleSegment(segmentSpan));
            }
        }
    }

    private static void ParseVariable(
        ReadOnlySpan<char> varContent,
        List<TemplateToken> tokens,
        List<VariableCapture> captures,
        List<string> variables,
        HashSet<string> variableSet)
    {
        if (varContent.IndexOf('{') >= 0 || varContent.IndexOf('}') >= 0)
        {
            throw new TranscodingException("invalid_template", 500, "Nested variables are not allowed in path templates.");
        }

        int eqIndex = varContent.IndexOf('=');
        string fieldPath;
        ReadOnlySpan<char> subPattern;
        if (eqIndex < 0)
        {
            fieldPath = varContent.ToString();
            subPattern = "*".AsSpan();
        }
        else
        {
            fieldPath = varContent[..eqIndex].ToString();
            subPattern = varContent[(eqIndex + 1)..];
            if (subPattern.Length == 0)
            {
                throw new TranscodingException("invalid_template", 500, "Variable pattern after '=' cannot be empty.");
            }
        }

        ValidateFieldPath(fieldPath);
        if (!variableSet.Add(fieldPath))
        {
            throw new TranscodingException("duplicate_variable", 500, $"Variable '{fieldPath}' is bound more than once in the template.");
        }
        variables.Add(fieldPath);

        int startTokenIndex = tokens.Count;
        int tokenCount = 0;
        bool includesDoubleStar = false;

        int subIndex = 0;
        while (subIndex < subPattern.Length)
        {
            int slash = subPattern[subIndex..].IndexOf('/');
            ReadOnlySpan<char> seg;
            if (slash < 0)
            {
                seg = subPattern[subIndex..];
                subIndex = subPattern.Length;
            }
            else
            {
                if (subIndex + slash == subPattern.Length - 1)
                {
                    throw new TranscodingException("invalid_template", 500, "Variable pattern cannot end with '/'.");
                }
                seg = subPattern.Slice(subIndex, slash);
                subIndex += slash + 1;
            }

            var token = ParseSimpleSegment(seg);
            if (token.Kind == TokenKind.DoubleStar)
            {
                includesDoubleStar = true;
            }
            tokens.Add(token);
            tokenCount++;
        }

        bool isMultiSegment = tokenCount > 1 || includesDoubleStar;
        captures.Add(new VariableCapture(fieldPath, startTokenIndex, tokenCount, includesDoubleStar, isMultiSegment));
    }

    private static TemplateToken ParseSimpleSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length == 0)
        {
            throw new TranscodingException("invalid_template", 500, "Empty segment in path template.");
        }
        if (segment.SequenceEqual("*".AsSpan()))
        {
            return new TemplateToken(TokenKind.Star, null);
        }
        if (segment.SequenceEqual("**".AsSpan()))
        {
            return new TemplateToken(TokenKind.DoubleStar, null);
        }
        string literal = segment.ToString();
        if (!IsValidLiteral(literal))
        {
            throw new TranscodingException("invalid_template", 500, $"Invalid literal segment '{literal}' in path template.");
        }
        return new TemplateToken(TokenKind.Literal, literal);
    }

    internal static void ValidateFieldPath(string fieldPath)
    {
        if (string.IsNullOrEmpty(fieldPath))
        {
            throw new TranscodingException("invalid_template", 500, "Variable field path cannot be empty.");
        }
        var parts = fieldPath.Split('.');
        if (parts.Length > MaxFieldPathDepth)
        {
            throw new TranscodingException("invalid_template", 500, "Field path exceeds maximum depth of 32.");
        }
        foreach (string part in parts)
        {
            if (!IsValidIdent(part))
            {
                throw new TranscodingException("invalid_template", 500, $"Invalid identifier '{part}' in field path.");
            }
        }
    }

    internal static bool IsValidIdent(string ident)
    {
        if (string.IsNullOrEmpty(ident))
        {
            return false;
        }
        char first = ident[0];
        if (!char.IsAsciiLetter(first) && first != '_')
        {
            return false;
        }
        for (int i = 1; i < ident.Length; i++)
        {
            char c = ident[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsValidLiteral(string literal)
    {
        if (string.IsNullOrEmpty(literal))
        {
            return false;
        }
        for (int i = 0; i < literal.Length; i++)
        {
            char c = literal[i];
            if (c is '/' or '{' or '}' or '*' or ':' or '?' or '#' || char.IsControl(c) || c == ' ')
            {
                return false;
            }
        }
        return true;
    }

    internal static bool TryPercentDecode(string input, bool preserveSlashes, out string decoded)
    {
        decoded = string.Empty;
        if (!input.Contains('%', StringComparison.Ordinal))
        {
            decoded = input;
            return true;
        }

        var sb = new StringBuilder(input.Length);
        Span<byte> byteBuffer = stackalloc byte[input.Length];
        int byteCount = 0;

        int i = 0;
        while (i < input.Length)
        {
            if (input[i] == '%')
            {
                if (i + 2 >= input.Length)
                {
                    return false;
                }
                int hi = HexValue(input[i + 1]);
                int lo = HexValue(input[i + 2]);
                if (hi < 0 || lo < 0)
                {
                    return false;
                }
                byte b = (byte)((hi << 4) | lo);
                if (preserveSlashes && b == 0x2F)
                {
                    FlushBytes(sb, byteBuffer, ref byteCount);
                    sb.Append('%');
                    sb.Append(input[i + 1]);
                    sb.Append(input[i + 2]);
                }
                else
                {
                    byteBuffer[byteCount++] = b;
                }
                i += 3;
            }
            else
            {
                FlushBytes(sb, byteBuffer, ref byteCount);
                sb.Append(input[i]);
                i++;
            }
        }

        FlushBytes(sb, byteBuffer, ref byteCount);
        decoded = sb.ToString();
        return true;

        static void FlushBytes(StringBuilder target, Span<byte> buffer, ref int count)
        {
            if (count > 0)
            {
                target.Append(Encoding.UTF8.GetString(buffer[..count]));
                count = 0;
            }
        }
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    internal enum TokenKind
    {
        Literal,
        Star,
        DoubleStar,
    }

    internal readonly record struct TemplateToken(TokenKind Kind, string? Literal);

    private readonly record struct VariableCapture(
        string FieldPath,
        int StartTokenIndex,
        int TokenCount,
        bool IncludesDoubleStar,
        bool IsMultiSegment);
}
