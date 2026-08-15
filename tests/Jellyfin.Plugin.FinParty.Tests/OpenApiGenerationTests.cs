using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.FinParty.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;
using Xunit;

namespace Jellyfin.Plugin.FinParty.Tests;

/// <summary>
/// Actually generates an OpenAPI document containing FinParty's controller, using the same
/// Swashbuckle version and the same operationId convention Jellyfin uses.
/// </summary>
/// <remarks>
/// <para>
/// This reproduces the precise step that took a live server down. Jellyfin builds its OpenAPI
/// document while starting; if generation throws, the host never binds its port and the whole
/// server fails to boot with only a log line to show for it.
/// </para>
/// <para>
/// The name-comparison guard in <see cref="SwaggerSchemaCollisionTests"/> catches the specific
/// case of colliding with a Jellyfin type. This catches the general case: anything at all that
/// makes schema or operation generation throw — an unsupported parameter shape, an ambiguous
/// route, a type Swashbuckle cannot describe. It is the difference between checking for one
/// known mistake and checking that the step actually succeeds.
/// </para>
/// </remarks>
public class OpenApiGenerationTests
{
    [Fact]
    public void TheOpenApiDocumentGeneratesWithoutThrowing()
    {
        var document = GenerateDocument();

        Assert.NotNull(document);
        Assert.NotEmpty(document.Paths);
    }

    [Fact]
    public void EveryFinPartyRouteAppearsExactlyOnce()
    {
        var document = GenerateDocument();

        var finPartyPaths = document.Paths.Keys
            .Where(path => path.Contains("FinParty", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(finPartyPaths);

        // A duplicate path would mean two actions claim the same route, which Swashbuckle
        // only tolerates with an explicit conflict resolver — Jellyfin does not configure one.
        Assert.Equal(finPartyPaths.Count, finPartyPaths.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryOperationIdIsUniqueWithinTheDocument()
    {
        var document = GenerateDocument();

        var operationIds = document.Paths
            .SelectMany(path => path.Value.Operations.Values)
            .Select(operation => operation.OperationId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        var duplicates = operationIds
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "Duplicate operationIds: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// Builds the document the way Jellyfin does.
    /// </summary>
    /// <returns>The generated document.</returns>
    private static OpenApiDocument GenerateDocument()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        // Swashbuckle's options configuration takes a dependency on the hosting environment,
        // which a bare ServiceCollection does not provide.
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());

        services.AddControllers()
            .AddApplicationPart(typeof(FinPartyController).Assembly);

        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("api-docs", new OpenApiInfo { Title = "FinParty", Version = "test" });

            // Jellyfin: "Use method name as operationId".
            options.CustomOperationIds(description =>
            {
                description.TryGetMethodInfo(out MethodInfo methodInfo);
                return description?.ActionDescriptor.AttributeRouteInfo?.Name
                       ?? methodInfo?.Name;
            });
        });

        using var provider = services.BuildServiceProvider();

        // Touching the API explorer first surfaces route problems with a clearer error.
        var descriptions = provider.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        Assert.NotEmpty(descriptions.ApiDescriptionGroups.Items);

        return provider.GetRequiredService<ISwaggerProvider>().GetSwagger("api-docs");
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        // MVC resolves default application parts by loading the assembly named here,
        // so it has to be a real, loadable assembly name.
        public string ApplicationName { get; set; } =
            typeof(OpenApiGenerationTests).Assembly.GetName().Name!;

        public string EnvironmentName { get; set; } = "Development";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
