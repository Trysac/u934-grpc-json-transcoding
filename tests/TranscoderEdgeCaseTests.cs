using Xunit;

namespace Trysac.GrpcJsonTranscoding.Tests;

/// <summary>Edge-case, adversarial, and boundary tests for PathTemplate and Transcoder.</summary>
public class TranscoderEdgeCaseTests
{
    private static readonly string[] StatusEnumValues = ["UNKNOWN", "ACTIVE", "ARCHIVED"];

    private static MessageDescriptor CreateComprehensiveDescriptor() =>
        new("ComprehensiveMessage", new[]
        {
            new FieldDescriptor("id", FieldType.String),
            new FieldDescriptor("flag", FieldType.Bool),
            new FieldDescriptor("count32", FieldType.Int32),
            new FieldDescriptor("count64", FieldType.Int64),
            new FieldDescriptor("ucount32", FieldType.UInt32),
            new FieldDescriptor("ucount64", FieldType.UInt64),
            new FieldDescriptor("ratio64", FieldType.Double),
            new FieldDescriptor("ratio32", FieldType.Float),
            new FieldDescriptor("payload", FieldType.Bytes),
            new FieldDescriptor("status", FieldType.Enum, EnumValues: StatusEnumValues),
            new FieldDescriptor("unconstrained_enum", FieldType.Enum),
            new FieldDescriptor("items", FieldType.Int32, Repeated: true),
            new FieldDescriptor("nested", FieldType.Message, Message: new MessageDescriptor("Nested", new[]
            {
                new FieldDescriptor("leaf", FieldType.String),
                new FieldDescriptor("num", FieldType.Int32),
            })),
            new FieldDescriptor("nested_list", FieldType.Message, Repeated: true, Message: new MessageDescriptor("NestedItem", new[]
            {
                new FieldDescriptor("title", FieldType.String),
            })),
        });

    [Fact]
    public void RoutePriorityLiteralsWinOverWildcardsAndFirstInDeclarationOrderWinsTies()
    {
        var desc = new MessageDescriptor("Req", new[]
        {
            new FieldDescriptor("id", FieldType.String),
        });

        var wildcardBinding = new MethodBinding(
            "WildcardMethod",
            new HttpRule("GET", "/v1/messages/{id}"),
            desc,
            desc);
        var literalBinding = new MethodBinding(
            "LiteralMethod",
            new HttpRule("GET", "/v1/messages/special"),
            desc,
            desc);
        var duplicateWildcardBinding = new MethodBinding(
            "SecondWildcardMethod",
            new HttpRule("GET", "/v1/messages/{id=*}"),
            desc,
            desc);

        var transcoder = new Transcoder(new[] { wildcardBinding, literalBinding, duplicateWildcardBinding });

        var specialResult = transcoder.TranscodeRequest("GET", "/v1/messages/special", null);
        Assert.Equal("LiteralMethod", specialResult.GrpcMethod);

        var wildcardResult = transcoder.TranscodeRequest("GET", "/v1/messages/other", null);
        Assert.Equal("WildcardMethod", wildcardResult.GrpcMethod);
    }

    [Fact]
    public void ConflictingBindingPathVariableSetAgainInQueryOrBodyThrows400()
    {
        var desc = CreateComprehensiveDescriptor();
        var starBinding = new MethodBinding(
            "UpdateWithStar",
            new HttpRule("PUT", "/v1/items/{id}", Body: "*"),
            desc,
            desc);
        var queryBinding = new MethodBinding(
            "GetItem",
            new HttpRule("GET", "/v1/items/{id}"),
            desc,
            desc);
        var nestedBodyBinding = new MethodBinding(
            "UpdateNested",
            new HttpRule("POST", "/v1/nested/{nested.leaf}", Body: "nested"),
            desc,
            desc);

        var transcoder = new Transcoder(new[] { starBinding, queryBinding, nestedBodyBinding });

        var errBodyConflict = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("PUT", "/v1/items/abc", "{\"id\": \"override\"}"));
        Assert.Equal("conflicting_binding", errBodyConflict.Code);
        Assert.Equal(400, errBodyConflict.HttpStatus);

        var errQueryConflict = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("GET", "/v1/items/abc?id=override", null));
        Assert.Equal("conflicting_binding", errQueryConflict.Code);
        Assert.Equal(400, errQueryConflict.HttpStatus);

        var errDuplicateScalarQuery = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("GET", "/v1/items/abc?count32=1&count32=2", null));
        Assert.Equal("conflicting_binding", errDuplicateScalarQuery.Code);

        var errNestedBodyConflict = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("POST", "/v1/nested/val", "{\"num\": 10}"));
        Assert.Equal("conflicting_binding", errNestedBodyConflict.Code);
    }

    [Fact]
    public void AllScalarTypesConvertFromQueryAndBodyAndRejectInvalidValues()
    {
        var desc = CreateComprehensiveDescriptor();
        var getBinding = new MethodBinding("GetComp", new HttpRule("GET", "/v1/comp/{id}"), desc, desc);
        var postBinding = new MethodBinding("PostComp", new HttpRule("POST", "/v1/comp/{id}", Body: "*"), desc, desc);
        var transcoder = new Transcoder(new[] { getBinding, postBinding });

        var queryRes = transcoder.TranscodeRequest(
            "GET",
            "/v1/comp/1?flag=true&count32=-10&count64=-9007199254740993&ucount32=42&ucount64=18446744073709551615&ratio64=3.14&ratio32=2.5&payload=AQID&status=ACTIVE&unconstrained_enum=CUSTOM",
            null);

        Assert.True(queryRes.Message["flag"]!.GetValue<bool>());
        Assert.Equal(-10, queryRes.Message["count32"]!.GetValue<int>());
        Assert.Equal("-9007199254740993", queryRes.Message["count64"]!.GetValue<string>());
        Assert.Equal(42u, queryRes.Message["ucount32"]!.GetValue<uint>());
        Assert.Equal("18446744073709551615", queryRes.Message["ucount64"]!.GetValue<string>());
        Assert.Equal(3.14, queryRes.Message["ratio64"]!.GetValue<double>());
        Assert.Equal(2.5f, queryRes.Message["ratio32"]!.GetValue<float>());
        Assert.Equal("AQID", queryRes.Message["payload"]!.GetValue<string>());
        Assert.Equal("ACTIVE", queryRes.Message["status"]!.GetValue<string>());
        Assert.Equal("CUSTOM", queryRes.Message["unconstrainedEnum"]!.GetValue<string>());

        var bodyRes = transcoder.TranscodeRequest(
            "POST",
            "/v1/comp/2",
            "{\"flag\":false,\"count32\":100,\"count64\":12345,\"ucount32\":\"200\",\"ucount64\":67890,\"ratio64\":1.25,\"ratio32\":0.5,\"payload\":\"_-8\",\"status\":\"ARCHIVED\",\"items\":[1,2],\"nestedList\":[{\"title\":\"t1\"}]}");

        Assert.False(bodyRes.Message["flag"]!.GetValue<bool>());
        Assert.Equal("12345", bodyRes.Message["count64"]!.GetValue<string>());
        Assert.Equal("67890", bodyRes.Message["ucount64"]!.GetValue<string>());
        Assert.Equal(200u, bodyRes.Message["ucount32"]!.GetValue<uint>());

        string responseJson = transcoder.TranscodeResponse("PostComp", bodyRes.Message);
        Assert.Contains("\"nestedList\":[{\"title\":\"t1\"}]", responseJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flag=notabool")]
    [InlineData("count32=notanint")]
    [InlineData("ucount32=-1")]
    [InlineData("ucount64=-5")]
    [InlineData("ratio64=NaN")]
    [InlineData("ratio32=Infinity")]
    [InlineData("payload=!!!notbase64!!!")]
    [InlineData("status=INVALID_ENUM")]
    [InlineData("unconstrained_enum=")]
    [InlineData("nested=direct_message_assign")]
    public void InvalidQueryScalarValuesThrowInvalidValue400(string badQuery)
    {
        var desc = CreateComprehensiveDescriptor();
        var binding = new MethodBinding("GetComp", new HttpRule("GET", "/v1/comp/{id}"), desc, desc);
        var transcoder = new Transcoder(new[] { binding });

        var ex = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("GET", $"/v1/comp/1?{badQuery}", null));
        Assert.Equal("invalid_value", ex.Code);
        Assert.Equal(400, ex.HttpStatus);
    }

    [Fact]
    public void InvalidRuleConfigurationsThrowInvalidRule500()
    {
        var desc = CreateComprehensiveDescriptor();

        // Unknown body field
        var exUnknownBody = Assert.Throws<TranscodingException>(() =>
            new Transcoder(new[]
            {
                new MethodBinding("M1", new HttpRule("POST", "/v1/a", Body: "missing"), desc, desc),
            }));
        Assert.Equal("invalid_rule", exUnknownBody.Code);
        Assert.Equal(500, exUnknownBody.HttpStatus);

        // Repeated body field
        var exRepeatedBody = Assert.Throws<TranscodingException>(() =>
            new Transcoder(new[]
            {
                new MethodBinding("M2", new HttpRule("POST", "/v1/a", Body: "items"), desc, desc),
            }));
        Assert.Equal("invalid_rule", exRepeatedBody.Code);

        // Unknown response_body field
        var exUnknownResp = Assert.Throws<TranscodingException>(() =>
            new Transcoder(new[]
            {
                new MethodBinding("M3", new HttpRule("POST", "/v1/a", ResponseBody: "missing"), desc, desc),
            }));
        Assert.Equal("invalid_rule", exUnknownResp.Code);

        // Path variable referencing unknown field
        var exUnknownPathVar = Assert.Throws<TranscodingException>(() =>
            new Transcoder(new[]
            {
                new MethodBinding("M4", new HttpRule("GET", "/v1/{missing}"), desc, desc),
            }));
        Assert.Equal("invalid_rule", exUnknownPathVar.Code);

        // Path variable referencing repeated field
        var exRepeatedPathVar = Assert.Throws<TranscodingException>(() =>
            new Transcoder(new[]
            {
                new MethodBinding("M5", new HttpRule("GET", "/v1/{items}"), desc, desc),
            }));
        Assert.Equal("invalid_rule", exRepeatedPathVar.Code);

        // Nested additional_bindings
        var exNestedAdditional = Assert.Throws<TranscodingException>(() =>
            new Transcoder(new[]
            {
                new MethodBinding(
                    "M6",
                    new HttpRule("GET", "/v1/a", AdditionalBindings: new[]
                    {
                        new HttpRule("GET", "/v1/b", AdditionalBindings: new[]
                        {
                            new HttpRule("GET", "/v1/c"),
                        }),
                    }),
                    desc,
                    desc),
            }));
        Assert.Equal("invalid_rule", exNestedAdditional.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1/messages")]
    [InlineData("/")]
    [InlineData("/v1/")]
    [InlineData("/v1//messages")]
    [InlineData("/v1/{unclosed")]
    [InlineData("/v1/{a={b}}")]
    [InlineData("/v1/{=*}")]
    [InlineData("/v1/{a=}")]
    [InlineData("/v1/{a=x/}")]
    [InlineData("/v1/{1bad}")]
    [InlineData("/v1/prefix{a}")]
    [InlineData("/v1/{a}suffix")]
    [InlineData("/v1/messages/:")]
    public void InvalidPathTemplatesThrowInvalidTemplate500(string badTemplate)
    {
        var ex = Assert.Throws<TranscodingException>(() => PathTemplate.Parse(badTemplate));
        Assert.Equal("invalid_template", ex.Code);
        Assert.Equal(500, ex.HttpStatus);
    }

    [Fact]
    public void SizeAndCountLimitsAreEnforced()
    {
        var desc = CreateComprehensiveDescriptor();
        var binding = new MethodBinding("PostComp", new HttpRule("POST", "/v1/comp/{id}", Body: "*"), desc, desc);
        var getBinding = new MethodBinding("GetComp", new HttpRule("GET", "/v1/comp/{id}"), desc, desc);
        var transcoder = new Transcoder(new[] { binding, getBinding });

        // PathAndQuery > 8192
        string hugePath = "/v1/comp/" + new string('x', 8_190);
        var exPath = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("GET", hugePath, null));
        Assert.Equal("input_too_large", exPath.Code);
        Assert.Equal(413, exPath.HttpStatus);

        // Body > 4 MiB
        string hugeBody = "{\"id\":\"" + new string('a', (4 * 1024 * 1024) + 10) + "\"}";
        var exBody = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("POST", "/v1/comp/1", hugeBody));
        Assert.Equal("input_too_large", exBody.Code);
        Assert.Equal(413, exBody.HttpStatus);

        // > 1000 query parameters
        var manyParams = Enumerable.Repeat("items=1", 1_001);
        string manyQuery = "/v1/comp/1?" + string.Join('&', manyParams);
        var exQueryCount = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("GET", manyQuery, null));
        Assert.Equal("input_too_large", exQueryCount.Code);
        Assert.Equal(400, exQueryCount.HttpStatus);

        // JSON depth > 64
        string deepJson = string.Concat(Enumerable.Repeat("{\"a\":", 70)) + "1" + new string('}', 70);
        var exDepth = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("POST", "/v1/comp/1", deepJson));
        Assert.Equal("invalid_body", exDepth.Code);
        Assert.Equal(400, exDepth.HttpStatus);

        // Field path depth > 32
        string deepFieldPath = string.Join('.', Enumerable.Repeat("a", 33));
        var exFieldDepth = Assert.Throws<TranscodingException>(() =>
            PathTemplate.Parse($"/v1/{{{deepFieldPath}}}"));
        Assert.Equal("invalid_template", exFieldDepth.Code);
    }

    [Fact]
    public void ScalarBodyMappingAndErrorMessageTruncation()
    {
        var desc = CreateComprehensiveDescriptor();
        var scalarBodyBinding = new MethodBinding(
            "UpdatePayload",
            new HttpRule("PUT", "/v1/comp/{id}/flag", Body: "flag"),
            desc,
            desc);
        var transcoder = new Transcoder(new[] { scalarBodyBinding });

        var res = transcoder.TranscodeRequest("PUT", "/v1/comp/123/flag", "true");
        Assert.Equal("{\"id\":\"123\",\"flag\":true}", res.Message.ToJsonString());

        string veryLongError = new string('x', 300);
        var customEx = new TranscodingException("custom", 400, veryLongError);
        Assert.Equal(200, customEx.Message.Length);
        Assert.EndsWith("...", customEx.Message, StringComparison.Ordinal);
    }
}
