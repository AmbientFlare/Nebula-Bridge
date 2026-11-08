using System.Net;
using System.Text.Json.Nodes;
using NebulaBridge.NativeSources;

namespace NebulaBridge.Tests;

public sealed class NativeSourceSecurityTests
{
    [Fact]
    public async Task BlocksLoopbackTargets()
    {
        var validator = new NetworkTargetValidator();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateAsync(new Uri("http://127.0.0.1/private"), CancellationToken.None)
        );

        Assert.Contains("private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsRedirectsOutsideDeclaredHosts()
    {
        var client = CreateClient(
            _ =>
                new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://outside.example/search") },
                }
        );

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SearchAsync(BuildDefinition(), new NativeMediaQuery("test"), CancellationToken.None)
        );

        Assert.Contains(
            "declared hosts",
            exception.InnerException?.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Theory]
    [InlineData("https://outside.example/search")]
    [InlineData("//outside.example/search")]
    public async Task RejectsSearchPathOutsideDeclaredHostsBeforeRequest(string path)
    {
        var requestSent = false;
        var client = CreateClient(_ =>
        {
            requestSent = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html></html>"),
            };
        });
        var definition = BuildDefinition();
        definition.Document["search"]!["paths"]![0]!["path"] = path;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SearchAsync(definition, new NativeMediaQuery("test"), CancellationToken.None)
        );

        Assert.False(requestSent);
        Assert.Contains(
            "declared hosts",
            exception.InnerException?.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public async Task RejectsResponsesLargerThanTwoMebibytes()
    {
        var client = CreateClient(
            _ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[(2 * 1024 * 1024) + 1]),
                }
        );

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SearchAsync(BuildDefinition(), new NativeMediaQuery("test"), CancellationToken.None)
        );

        Assert.Contains("2 MiB", exception.InnerException?.Message, StringComparison.Ordinal);
    }

    private static NativeIndexerClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory
    ) => CardigannTestSupport.CreateClient(responseFactory);

    private static IndexerDefinition BuildDefinition() =>
        CardigannTestSupport.BuildDefinition(
            "security-fixture",
            "html",
            ".result",
            JsonNode.Parse(
                """{"title":{"selector":".title"},"download":{"selector":".download","attribute":"href"}}"""
            )!.AsObject()
        );
}
