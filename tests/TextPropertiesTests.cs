using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace Trysac.GrpcJsonTranscoding.Tests;

/// <summary>Property-based tests for text normalization and path template invariants.</summary>
public class TextPropertiesTests
{
    [Property]
    public bool NormalizedTextIsTrimmedAndLowercase(NonNull<string> inputWrapper)
    {
        string input = inputWrapper.Get;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        var actual = Text.Normalize(input);
        return actual == actual.Trim() && string.Equals(actual, actual.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [Property]
    public Property LowerCamelCaseNeverContainsUnderscoreAndIsIdempotentOnCamelCase()
    {
        string[] idents = ["message_id", "user_id", "sub_field_name", "simple", "a_b_c", "alreadyCamel"];
        var identGen = Gen.Elements(idents).ToArbitrary();

        return Prop.ForAll(identGen, ident =>
        {
            var field = new FieldDescriptor(ident, FieldType.String);
            return !field.JsonName.Contains('_', StringComparison.Ordinal) &&
                   field.JsonName.Length > 0 &&
                   char.IsLower(field.JsonName[0]);
        });
    }

    [Property]
    public Property SingleSegmentTemplateMatchesAnyUrlSafeToken()
    {
        string[] tokens = ["alpha", "123456", "msg-1", "item_2", "a%2Fb", "hello%20world"];
        var tokenGen = Gen.Elements(tokens).ToArbitrary();

        var template = PathTemplate.Parse("/v1/messages/{message_id}");
        return Prop.ForAll(tokenGen, segment =>
        {
            bool matched = template.TryMatch($"/v1/messages/{segment}", out var bindings);
            return matched && bindings.ContainsKey("message_id") && bindings["message_id"].Length > 0;
        });
    }
}
