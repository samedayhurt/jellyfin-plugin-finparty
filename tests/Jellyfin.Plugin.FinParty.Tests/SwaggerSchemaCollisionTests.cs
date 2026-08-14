using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.FinParty;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.FinParty.Tests;

/// <summary>
/// Guards against the failure that took a live server down.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin builds an OpenAPI document during startup, and Swashbuckle derives each
/// <c>schemaId</c> from a type's <b>short name</b>. Two types called <c>PlayRequest</c> —
/// one of ours, one of Jellyfin's — make schema generation throw. That happens while the
/// host is starting, so Jellyfin never binds its port: the whole server fails to boot, and
/// the only clue is a stack trace in a log file.
/// </para>
/// <para>
/// It is invisible to ordinary testing. The plugin compiles, loads, and passes every unit
/// test; the collision only exists once a real Jellyfin enumerates the controllers. This
/// test reproduces that enumeration so the failure surfaces here instead of in production.
/// </para>
/// </remarks>
public class SwaggerSchemaCollisionTests
{
    [Fact]
    public void NoTypeInTheApiSurfaceCollidesWithAJellyfinType()
    {
        var jellyfinNames = new[]
            {
                typeof(PlayRequest).Assembly,        // MediaBrowser.Model
                typeof(SessionInfo).Assembly,        // MediaBrowser.Controller
            }
            .SelectMany(SafeExportedTypes)
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var apiTypes = CollectApiSurface(typeof(Plugin).Assembly);

        // If this is empty the test is silently passing for the wrong reason.
        Assert.NotEmpty(apiTypes);

        var collisions = apiTypes
            .Where(type => jellyfinNames.Contains(type.Name))
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            collisions.Count == 0,
            "These types share a short name with a Jellyfin type. Swagger derives schemaId from "
            + "the short name, so Jellyfin will throw while generating its OpenAPI document and "
            + "fail to start. Rename them:\n  "
            + string.Join("\n  ", collisions));
    }

    [Fact]
    public void TheApiSurfaceActuallyIncludesTheRequestBodies()
    {
        // Proves the collector reaches the types that matter, so the guard above is meaningful.
        var apiTypes = CollectApiSurface(typeof(Plugin).Assembly).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("FinPartyCreateRequest", apiTypes);
        Assert.Contains("FinPartyPlayRequest", apiTypes);
        Assert.Contains("FinPartySeekRequest", apiTypes);
        Assert.Contains("FinPartyInviteRequest", apiTypes);

        // Reached only by walking into a returned type's properties.
        Assert.Contains("FinPartyMemberDto", apiTypes);
        Assert.Contains("FinPartyTuningDto", apiTypes);
    }

    /// <summary>
    /// Jellyfin sets the OpenAPI operationId to the bare action method name
    /// (<c>ApiServiceCollectionExtensions</c>: "Use method name as operationId"), and that name
    /// is shared with every controller in the process — Jellyfin's own and every other plugin's.
    /// A method called <c>GetDevices</c> would silently duplicate Jellyfin's
    /// <c>DevicesController.GetDevices</c>, producing an ambiguous document and broken generated
    /// clients. Keeping the plugin name in every action makes that impossible.
    /// </summary>
    [Fact]
    public void EveryActionNameIsNamespacedToThePlugin()
    {
        var actions = typeof(Plugin).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToList();

        Assert.NotEmpty(actions);

        var generic = actions
            .Select(method => method.Name)
            .Where(name => !name.Contains("FinParty", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            generic.Count == 0,
            "Jellyfin uses the action method name as the OpenAPI operationId, which is global "
            + "across every controller in the process. These names are too generic and will "
            + "collide:\n  " + string.Join("\n  ", generic));

        var duplicates = actions
            .GroupBy(method => method.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "Duplicate action names: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// Collects every type Swashbuckle would need a schema for: the parameters and return
    /// types of each action, plus anything reachable through their properties.
    /// </summary>
    /// <param name="assembly">The plugin assembly.</param>
    /// <returns>The types forming the API surface.</returns>
    private static HashSet<Type> CollectApiSurface(Assembly assembly)
    {
        var found = new HashSet<Type>();

        var controllers = assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

        foreach (var controller in controllers)
        {
            var actions = controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any());

            foreach (var action in actions)
            {
                foreach (var parameter in action.GetParameters())
                {
                    Walk(parameter.ParameterType, found);
                }

                Walk(action.ReturnType, found);
            }
        }

        return found;
    }

    private static void Walk(Type type, HashSet<Type> found)
    {
        type = Unwrap(type);

        if (!IsSchematised(type) || !found.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Walk(property.PropertyType, found);
        }
    }

    /// <summary>
    /// Peels off the wrappers Swagger looks through: Task, ActionResult, nullable and collections.
    /// </summary>
    /// <param name="type">The declared type.</param>
    /// <returns>The underlying payload type.</returns>
    private static Type Unwrap(Type type)
    {
        while (true)
        {
            if (type.IsArray)
            {
                type = type.GetElementType()!;
                continue;
            }

            if (!type.IsGenericType)
            {
                return type;
            }

            var definition = type.GetGenericTypeDefinition();
            var arguments = type.GetGenericArguments();

            if (definition == typeof(Nullable<>)
                || definition == typeof(System.Threading.Tasks.Task<>)
                || definition == typeof(ActionResult<>))
            {
                type = arguments[0];
                continue;
            }

            // Dictionaries schematise their value type; other collections their element type.
            if (typeof(IEnumerable).IsAssignableFrom(type) && arguments.Length >= 1)
            {
                type = arguments[^1];
                continue;
            }

            return type;
        }
    }

    private static bool IsSchematised(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(object)
            || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(decimal)
            || type == typeof(void) || type == typeof(System.Threading.CancellationToken))
        {
            return false;
        }

        // Only our own types can be renamed, so only our own types are worth reporting.
        return type.Assembly == typeof(Plugin).Assembly;
    }

    private static IEnumerable<Type> SafeExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }
}
