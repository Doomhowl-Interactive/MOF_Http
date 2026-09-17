using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mof.Http.Tests;

public sealed class AuthTests
{
    private const string Password = "correct-horse-test-password";

    private static GatewayFactory Protected() => new(new() { ["ApiPassword"] = Password });

    private static void Basic(HttpClient client, string user, string password) => client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    [Fact]
    public async Task WithoutPasswordEverythingStaysAnonymous()
    {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task PasswordProtectsFrontendApiAndSpecButNotHealth()
    {
        using var factory = Protected();
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/", "/api/unwrap", "/openapi/v1.json", "/swagger/index.html" })
        {
            using var response = path == "/api/unwrap"
                ? await client.PostAsync(path, GatewayTests.Form())
                : await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        var challenge = (await client.GetAsync("/")).Headers.WwwAuthenticate;
        Assert.Contains(challenge, header => header.Scheme == "Basic");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var error = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("password", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowserNavigationReceivesInAppLoginScreen()
    {
        using var factory = Protected();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(response.Headers, header => header.Key.Equals("WWW-Authenticate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Welcome back", html);
        Assert.Contains("/login", html);
    }

    [Fact]
    public async Task BrowserLoginCreatesSessionForUploader()
    {
        using var factory = Protected();
        using var client = factory.CreateClient();
        var content = new StringContent("{\"password\":\"" + Password + "\"}", Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/login", new StringContent("{\"password\":\"wrong\"}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/login", content)).StatusCode);

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Unwrap an OBJ mesh", html);
    }

    [Theory]
    [InlineData("Basic", "d3JvbmdwYXNzd29yZA==")] // "wrongpassword" without a colon
    [InlineData("Basic", "dXNlcjp3cm9uZw==")] // "user:wrong"
    [InlineData("Bearer", "wrong")]
    public async Task WrongCredentialsAreRejected(string scheme, string credential)
    {
        using var factory = Protected();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, credential);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/")).StatusCode);
        using var keyClient = factory.CreateClient();
        keyClient.DefaultRequestHeaders.Add("X-API-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await keyClient.PostAsync("/api/unwrap", GatewayTests.Form())).StatusCode);
    }

    [Fact]
    public async Task MalformedBasicCredentialsAreRejected()
    {
        using var factory = Protected();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "not-base64!!");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task EachCredentialStyleGrantsAccess()
    {
        using var factory = Protected();
        using var colonless = factory.CreateClient();
        colonless.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Password)));
        Assert.Equal(HttpStatusCode.OK, (await colonless.GetAsync("/")).StatusCode);

        using var basic = factory.CreateClient();
        Basic(basic, "anyone", Password);
        Assert.Equal(HttpStatusCode.OK, (await basic.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await basic.GetAsync("/openapi/v1.json")).StatusCode);
        // Missing File fails model validation with 400, proving the request passed authentication.
        using var invalid = GatewayTests.Form("", "cube.obj");
        Assert.Equal(HttpStatusCode.BadRequest, (await basic.PostAsync("/api/unwrap", invalid)).StatusCode);

        using var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Password);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/")).StatusCode);

        using var key = factory.CreateClient();
        key.DefaultRequestHeaders.Add("X-API-Key", Password);
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task RepeatedWrongPasswordsAreThrottledWith429()
    {
        using var factory = Protected();
        using var client = factory.CreateClient();
        var last = HttpStatusCode.Unauthorized;
        string? retryAfter = null;
        for (var i = 0; i < 30; i++)
        {
            using var response = await client.GetAsync("/");
            last = response.StatusCode;
            if (last == HttpStatusCode.TooManyRequests)
            {
                retryAfter = response.Headers.RetryAfter?.Delta?.ToString()
                    ?? (response.Headers.TryGetValues("Retry-After", out var values) ? string.Join(",", values) : null);
                break;
            }
            Assert.Equal(HttpStatusCode.Unauthorized, last);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.NotNull(retryAfter);
    }

    [Fact]
    public async Task SuccessfulPasswordResetsThrottle()
    {
        using var factory = Protected();
        using var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/")).StatusCode);
        Basic(client, "anyone", Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task ProtectedSpecAdvertisesAuthentication()
    {
        using var factory = Protected();
        using var client = factory.CreateClient();
        Basic(client, "anyone", Password);
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var components = document.RootElement.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("http", components.GetProperty("basic").GetProperty("type").GetString());
        Assert.Equal("basic", components.GetProperty("basic").GetProperty("scheme").GetString());
        Assert.Equal("apiKey", components.GetProperty("apiKey").GetProperty("type").GetString());
        Assert.Equal("X-API-Key", components.GetProperty("apiKey").GetProperty("name").GetString());
        Assert.NotEmpty(document.RootElement.GetProperty("security").EnumerateArray());
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/unwrap").GetProperty("post");
        Assert.True(operation.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(operation.GetProperty("responses").TryGetProperty("429", out _));
    }

    [Fact]
    public async Task UnprotectedSpecHasNoAuthentication()
    {
        using var factory = new GatewayFactory();
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        Assert.False(document.RootElement.TryGetProperty("security", out _));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("secret", false)]
    [InlineData("   ", true)]
    [InlineData("x", false)]
    public void PasswordValidation(string? password, bool invalid)
    {
        var settings = new MofSettings { ApiPassword = password };
        if (invalid) Assert.Throws<InvalidOperationException>(settings.Validate);
        else settings.Validate();
    }

    [Fact]
    public void OverlyLongPasswordIsInvalid()
    {
        var action = () => new MofSettings { ApiPassword = new string('a', 257) }.Validate();
        Assert.Throws<InvalidOperationException>(action);
    }
}
