using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Data.Templates;
using CampaignVault.Middleware;
using CampaignVault.Models;
using CampaignVault.Models.Converters;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>Regression coverage for the adversarial review remediations.</summary>
public class AdversarialRemediationTests
{
    private static JsonElement DisposedElement()
    {
        using var doc = JsonDocument.Parse("{\"a\":1}");
        return doc.RootElement;
    }

    [Fact]
    public void SanitizeDictionary_DeadElement_NeverThrowsNorLeaks()
    {
        var dict = new Dictionary<string, object>
        {
            ["gone"] = DisposedElement(),
            ["fine"] = "ok",
        };

        var healed = JsonSanitizer.SanitizeDictionary(dict);

        Assert.Equal("ok", healed!["fine"]);
        if (healed!.TryGetValue("gone", out var value))
        {
            // Healed to renderable text (raw JSON or ToString fallback): never a live element,
            // and never a placeholder.
            Assert.IsNotType<JsonElement>(value);
            Assert.NotEqual("unsanitizable", value as string);
        }
        // else: truly unrecoverable elements are dropped with a debug log -- also acceptable.
    }

    [Fact]
    public void SanitizeValue_DeadElement_NeverThrowsNorLeaks()
    {
        var healed = JsonSanitizer.SanitizeValue(DisposedElement());

        Assert.True(healed is null || healed is string,
            $"Expected null or healed string, got {healed?.GetType().Name}");
    }

    private static WorldChange[] ParseChanges(string json)
    {
        var options = new JsonSerializerOptions { Converters = { new WorldChangeArrayJsonConverter() } };
        return JsonSerializer.Deserialize<WorldChange[]>(json, options)!;
    }

    [Fact]
    public void NormalizeChangesArray_StatusRemoveAlias_StillResolves()
    {
        var parsed = ParseChanges("""[{"$type":"statusremove","characterId":"chars/x","status":"Blessed"}]""");

        var remove = Assert.Single(parsed);
        var statusRemove = Assert.IsType<StatusRemove>(remove);
        Assert.Equal("Blessed", statusRemove.Status);
    }

    [Fact]
    public void NormalizeChangesArray_StatusRemoveWordInValue_IsPreserved()
    {
        var parsed = ParseChanges("""[{"$type":"status_remove","characterId":"chars/x","status":"statusremove"}]""");

        var remove = Assert.Single(parsed);
        Assert.Equal("statusremove", Assert.IsType<StatusRemove>(remove).Status);
    }

    [Fact]
    public void HomebrewId_SlugWithNoLettersOrDigits_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => HomebrewTemplates.Id("campaign", "power", "dnd5e", "!!!"));
    }

    [Fact]
    public void HomebrewId_OrdinaryName_BuildsKey()
    {
        Assert.Equal("homebrew/campaign/power/dnd5e/fireball", HomebrewTemplates.Id("campaign", "power", "dnd5e", "Fireball!"));
    }

    private static DefaultHttpContext ContextWithQueryToken(string token)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/";
        ctx.Request.QueryString = new QueryString("?token=" + token);
        return ctx;
    }

    [Fact]
    public async Task AuthMiddleware_QueryToken_RejectedByDefault()
    {
        var invoked = false;
        var middleware = new AuthMiddleware(_ => { invoked = true; return Task.CompletedTask; }, "secret");

        var ctx = ContextWithQueryToken("secret");
        await middleware.InvokeAsync(ctx);

        Assert.False(invoked);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task AuthMiddleware_QueryToken_AllowedWhenOptedIn()
    {
        var invoked = false;
        var middleware = new AuthMiddleware(_ => { invoked = true; return Task.CompletedTask; }, "secret", allowQueryToken: true);

        var ctx = ContextWithQueryToken("secret");
        await middleware.InvokeAsync(ctx);

        Assert.True(invoked);
    }

    [Fact]
    public async Task AuthMiddleware_HeaderAuth_WorksWithoutQueryOptIn()
    {
        var invoked = false;
        var middleware = new AuthMiddleware(_ => { invoked = true; return Task.CompletedTask; }, "secret");

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/";
        ctx.Request.Headers["Authorization"] = "Bearer secret";
        await middleware.InvokeAsync(ctx);

        Assert.True(invoked);
    }
}
