using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Trysac.GrpcJsonTranscoding.Tests;

/// <summary>Acceptance tests covering every scenario from the specification and google/api/http.proto.</summary>
public class TranscoderAcceptanceTests
{
    private static MessageDescriptor CreateSubMessageDescriptor() =>
        new("SubMessage", new[]
        {
            new FieldDescriptor("subfield", FieldType.String),
        });

    private static MessageDescriptor CreateGetMessageRequestDescriptor() =>
        new("GetMessageRequest", new[]
        {
            new FieldDescriptor("name", FieldType.String),
            new FieldDescriptor("message_id", FieldType.String),
            new FieldDescriptor("revision", FieldType.Int64),
            new FieldDescriptor("sub", FieldType.Message, Message: CreateSubMessageDescriptor()),
            new FieldDescriptor("user_id", FieldType.String),
            new FieldDescriptor("tags", FieldType.String, Repeated: true),
        });

    private static MessageDescriptor CreateUpdateMessageRequestDescriptor() =>
        new("UpdateMessageRequest", new[]
        {
            new FieldDescriptor("message_id", FieldType.String),
            new FieldDescriptor("text", FieldType.String),
            new FieldDescriptor("message", FieldType.Message, Message: new MessageDescriptor("MessageBody", new[]
            {
                new FieldDescriptor("text", FieldType.String),
            })),
        });

    private static MessageDescriptor CreateEmptyDescriptor() =>
        new("Empty", Array.Empty<FieldDescriptor>());

    [Fact]
    public void Acceptance1FirstExampleOfHttpProtoBindsMultiSegmentResourceName()
    {
        var reqDesc = new MessageDescriptor("GetMessageRequest", new[]
        {
            new FieldDescriptor("name", FieldType.String),
        });
        var binding = new MethodBinding(
            "GetMessage",
            new HttpRule("GET", "/v1/{name=messages/*}"),
            reqDesc,
            CreateEmptyDescriptor());

        var transcoder = new Transcoder(new[] { binding });
        var result = transcoder.TranscodeRequest("GET", "/v1/messages/123456", null);

        Assert.Equal("GetMessage", result.GrpcMethod);
        Assert.Equal("{\"name\":\"messages/123456\"}", result.Message.ToJsonString());
    }

    [Fact]
    public void Acceptance2PathAndQueryParametersWithNestedSubfieldAndInt64AsString()
    {
        var reqDesc = CreateGetMessageRequestDescriptor();
        var binding = new MethodBinding(
            "GetMessage",
            new HttpRule("GET", "/v1/messages/{message_id}"),
            reqDesc,
            CreateEmptyDescriptor());

        var transcoder = new Transcoder(new[] { binding });
        var result = transcoder.TranscodeRequest(
            "GET",
            "/v1/messages/123456?revision=2&sub.subfield=foo",
            null);

        Assert.Equal("GetMessage", result.GrpcMethod);
        Assert.Equal(
            "{\"messageId\":\"123456\",\"revision\":\"2\",\"sub\":{\"subfield\":\"foo\"}}",
            result.Message.ToJsonString());
    }

    [Fact]
    public void Acceptance3PatchBodyFieldAndBodyStar()
    {
        var reqDesc = CreateUpdateMessageRequestDescriptor();

        var fieldBodyBinding = new MethodBinding(
            "UpdateMessageField",
            new HttpRule("PATCH", "/v1/messages/{message_id}", Body: "message"),
            reqDesc,
            CreateEmptyDescriptor());

        var transcoderField = new Transcoder(new[] { fieldBodyBinding });
        var resField = transcoderField.TranscodeRequest("PATCH", "/v1/messages/123456", "{\"text\": \"Hi!\"}");
        Assert.Equal(
            "{\"messageId\":\"123456\",\"message\":{\"text\":\"Hi!\"}}",
            resField.Message.ToJsonString());

        var starBodyBinding = new MethodBinding(
            "UpdateMessageStar",
            new HttpRule("PATCH", "/v1/messages/{message_id}", Body: "*"),
            reqDesc,
            CreateEmptyDescriptor());

        var transcoderStar = new Transcoder(new[] { starBodyBinding });
        var resStar = transcoderStar.TranscodeRequest("PATCH", "/v1/messages/123456", "{\"text\": \"Hi!\"}");
        Assert.Equal(
            "{\"messageId\":\"123456\",\"text\":\"Hi!\"}",
            resStar.Message.ToJsonString());
    }

    [Fact]
    public void Acceptance4AdditionalBindingsAndCustomVerbCancel()
    {
        var reqDesc = CreateGetMessageRequestDescriptor();
        var ruleWithAdditional = new HttpRule(
            "GET",
            "/v1/messages/{message_id}",
            AdditionalBindings: new[]
            {
                new HttpRule("GET", "/v1/users/{user_id}/messages/{message_id}"),
            });

        var cancelRule = new HttpRule("POST", "/v1/{name=operations/*}:cancel", Body: "*");
        var transcoder = new Transcoder(new[]
        {
            new MethodBinding("GetMessage", ruleWithAdditional, reqDesc, CreateEmptyDescriptor()),
            new MethodBinding("CancelOperation", cancelRule, reqDesc, CreateEmptyDescriptor()),
        });

        var resAdditional = transcoder.TranscodeRequest("GET", "/v1/users/me/messages/123456", null);
        Assert.Equal("GetMessage", resAdditional.GrpcMethod);
        Assert.Equal(
            "{\"messageId\":\"123456\",\"userId\":\"me\"}",
            resAdditional.Message.ToJsonString());

        var resVerb = transcoder.TranscodeRequest("POST", "/v1/operations/op1:cancel", null);
        Assert.Equal("CancelOperation", resVerb.GrpcMethod);
        Assert.Equal("{\"name\":\"operations/op1\"}", resVerb.Message.ToJsonString());
    }

    [Fact]
    public void Acceptance5PercentEncodingSingleVsMultiSegmentAndRepeatedQueryParams()
    {
        var singleTemplate = PathTemplate.Parse("/{name}");
        Assert.True(singleTemplate.TryMatch("/a%2Fb", out var singleBindings));
        Assert.Equal("a/b", singleBindings["name"]);

        var multiTemplate = PathTemplate.Parse("/{name=shelves/**}");
        Assert.True(multiTemplate.TryMatch("/shelves/a%2Fb/c", out var multiBindings));
        Assert.Equal("shelves/a%2Fb/c", multiBindings["name"]);

        var reqDesc = CreateGetMessageRequestDescriptor();
        var binding = new MethodBinding(
            "GetMessage",
            new HttpRule("GET", "/v1/messages/{message_id}"),
            reqDesc,
            CreateEmptyDescriptor());
        var transcoder = new Transcoder(new[] { binding });
        var res = transcoder.TranscodeRequest("GET", "/v1/messages/123456?tags=a&tags=b", null);
        Assert.Equal(
            "{\"messageId\":\"123456\",\"tags\":[\"a\",\"b\"]}",
            res.Message.ToJsonString());
    }

    [Fact]
    public void Acceptance6ErrorCodesAndHttpStatuses()
    {
        var reqDesc = CreateGetMessageRequestDescriptor();
        var binding = new MethodBinding(
            "GetMessage",
            new HttpRule("PATCH", "/v1/messages/{message_id}", Body: "*"),
            reqDesc,
            CreateEmptyDescriptor());
        var getBinding = new MethodBinding(
            "GetMessageById",
            new HttpRule("GET", "/v1/items/{message_id}"),
            reqDesc,
            CreateEmptyDescriptor());

        var transcoder = new Transcoder(new[] { binding, getBinding });

        var notFound = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("GET", "/v1/unknown/path", null));
        Assert.Equal("not_found", notFound.Code);
        Assert.Equal(404, notFound.HttpStatus);

        var methodNotAllowed = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("DELETE", "/v1/messages/123456", null));
        Assert.Equal("method_not_allowed", methodNotAllowed.Code);
        Assert.Equal(405, methodNotAllowed.HttpStatus);

        var invalidBody = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("PATCH", "/v1/messages/123456", "{not-valid-json"));
        Assert.Equal("invalid_body", invalidBody.Code);
        Assert.Equal(400, invalidBody.HttpStatus);

        var unknownField = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("GET", "/v1/items/123456?nonexistent=1", null));
        Assert.Equal("unknown_field", unknownField.Code);
        Assert.Equal(400, unknownField.HttpStatus);

        var invalidValue = Assert.Throws<TranscodingException>(() =>
            transcoder.TranscodeRequest("GET", "/v1/items/123456?revision=abc", null));
        Assert.Equal("invalid_value", invalidValue.Code);
        Assert.Equal(400, invalidValue.HttpStatus);
    }

    [Fact]
    public void Acceptance7TemplateErrorsAndInputTooLarge()
    {
        var invalidTemplate = Assert.Throws<TranscodingException>(() =>
            PathTemplate.Parse("/v1/{name=**}/x"));
        Assert.Equal("invalid_template", invalidTemplate.Code);
        Assert.Equal(500, invalidTemplate.HttpStatus);

        var duplicateVar = Assert.Throws<TranscodingException>(() =>
            PathTemplate.Parse("/v1/{a}/{a}"));
        Assert.Equal("duplicate_variable", duplicateVar.Code);
        Assert.Equal(500, duplicateVar.HttpStatus);

        var template = PathTemplate.Parse("/v1/{name=**}");
        string longPath = "/" + new string('a', 8_192);
        Assert.Equal(8_193, longPath.Length);

        var pathTooLarge = Assert.Throws<TranscodingException>(() =>
            template.TryMatch(longPath, out _));
        Assert.Equal("input_too_large", pathTooLarge.Code);
        Assert.Equal(413, pathTooLarge.HttpStatus);

        var templateTooLarge = Assert.Throws<TranscodingException>(() =>
            PathTemplate.Parse(longPath));
        Assert.Equal("input_too_large", templateTooLarge.Code);
        Assert.Equal(400, templateTooLarge.HttpStatus);
    }

    [Fact]
    public void Acceptance8TranscodeResponseWithResponseBodyAndWholeMessageInDescriptorOrder()
    {
        var responseDesc = new MessageDescriptor("GetMessageResponse", new[]
        {
            new FieldDescriptor("first_field", FieldType.String),
            new FieldDescriptor("message", FieldType.Message, Message: new MessageDescriptor("InnerMessage", new[]
            {
                new FieldDescriptor("text_content", FieldType.String),
                new FieldDescriptor("created_at", FieldType.Int64),
            })),
            new FieldDescriptor("last_field", FieldType.Int32),
        });

        var reqDesc = CreateGetMessageRequestDescriptor();
        var projectedBinding = new MethodBinding(
            "GetProjected",
            new HttpRule("GET", "/v1/projected/{message_id}", ResponseBody: "message"),
            reqDesc,
            responseDesc);
        var fullBinding = new MethodBinding(
            "GetFull",
            new HttpRule("GET", "/v1/full/{message_id}"),
            reqDesc,
            responseDesc);

        var transcoder = new Transcoder(new[] { projectedBinding, fullBinding });

        var responseObj = new JsonObject
        {
            ["last_field"] = 42,
            ["message"] = new JsonObject
            {
                ["created_at"] = "1700000000",
                ["text_content"] = "Hello",
            },
            ["first_field"] = "alpha",
        };

        string projectedJson = transcoder.TranscodeResponse("GetProjected", responseObj);
        Assert.Equal("{\"textContent\":\"Hello\",\"createdAt\":\"1700000000\"}", projectedJson);

        string fullJson = transcoder.TranscodeResponse("GetFull", responseObj);
        Assert.Equal(
            "{\"firstField\":\"alpha\",\"message\":{\"textContent\":\"Hello\",\"createdAt\":\"1700000000\"},\"lastField\":42}",
            fullJson);
    }

    [Fact]
    public void RunsAllHttpProtoFixtureExamples()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "http-proto-examples.json");
        string json = File.ReadAllText(fixturePath);
        using var doc = JsonDocument.Parse(json);

        var requestDesc = new MessageDescriptor("FixtureRequest", new[]
        {
            new FieldDescriptor("name", FieldType.String),
            new FieldDescriptor("message_id", FieldType.String),
            new FieldDescriptor("revision", FieldType.Int64),
            new FieldDescriptor("sub", FieldType.Message, Message: CreateSubMessageDescriptor()),
            new FieldDescriptor("user_id", FieldType.String),
            new FieldDescriptor("text", FieldType.String),
            new FieldDescriptor("message", FieldType.Message, Message: new MessageDescriptor("InnerMsg", new[]
            {
                new FieldDescriptor("text", FieldType.String),
            })),
        });

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string grpcMethod = item.GetProperty("grpc_method").GetString()!;
            var ruleEl = item.GetProperty("rule");
            var rule = ParseRuleFromJson(ruleEl);

            var binding = new MethodBinding(grpcMethod, rule, requestDesc, CreateEmptyDescriptor());
            var transcoder = new Transcoder(new[] { binding });

            var reqEl = item.GetProperty("request");
            string httpMethod = reqEl.GetProperty("http_method").GetString()!;
            string pathAndQuery = reqEl.GetProperty("path_and_query").GetString()!;
            string? body = reqEl.GetProperty("body").ValueKind == JsonValueKind.Null
                ? null
                : reqEl.GetProperty("body").GetString();

            var transcoded = transcoder.TranscodeRequest(httpMethod, pathAndQuery, body);
            Assert.Equal(grpcMethod, transcoded.GrpcMethod);

            string expectedJson = item.GetProperty("expected_message").GetRawText();
            var expectedNode = JsonNode.Parse(expectedJson)!;
            Assert.Equal(expectedNode.ToJsonString(), transcoded.Message.ToJsonString());
        }
    }

    private static HttpRule ParseRuleFromJson(JsonElement el)
    {
        string method = el.GetProperty("method").GetString()!;
        string pattern = el.GetProperty("pattern").GetString()!;
        string? body = el.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        string? responseBody = el.TryGetProperty("response_body", out var rb) && rb.ValueKind == JsonValueKind.String ? rb.GetString() : null;
        List<HttpRule>? additional = null;
        if (el.TryGetProperty("additional_bindings", out var addEl) && addEl.ValueKind == JsonValueKind.Array)
        {
            additional = new List<HttpRule>();
            foreach (var child in addEl.EnumerateArray())
            {
                additional.Add(ParseRuleFromJson(child));
            }
        }
        return new HttpRule(method, pattern, body, responseBody, additional);
    }
}
