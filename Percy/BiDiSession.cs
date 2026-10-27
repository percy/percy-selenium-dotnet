using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenQA.Selenium;

namespace PercyIO.Selenium
{
    public static partial class Percy
    {
        // Thin reflective wrapper over Selenium's WebDriver BiDi client. The .NET BiDi
        // API is experimental and changes shape between releases (for example
        // CallFunctionOptions.Arguments went from IEnumerable<LocalValue> to
        // ImmutableArray<LocalValue> by 4.50), so nothing here binds to it at compile
        // time. Any missing member throws, and callers fall back to the next strategy.
        internal sealed class BiDiSession : IDisposable
        {
            private const string Ns = "OpenQA.Selenium.BiDi.";
            internal static TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

            private readonly object _bidi;

            internal BiDiSession(object bidi)
            {
                _bidi = bidi;
            }

            // The SDK never asks for BiDi itself; it only uses a session the user
            // started with webSocketUrl: true.
            internal static bool HasWebSocketUrl(WebDriver driver) =>
                (driver as IHasCapabilities)?.Capabilities?.GetCapability("webSocketUrl") is string url && url.Length > 0;

            internal static BiDiSession Open(WebDriver driver)
            {
                MethodInfo asBiDi = RequireType("WebDriverExtensions").GetMethod("AsBiDiAsync")
                    ?? throw Missing("WebDriverExtensions.AsBiDiAsync");
                return new BiDiSession(Run(() => asBiDi.Invoke(null, WithDefaults(asBiDi, driver)))
                    ?? throw new WebDriverException("AsBiDiAsync returned null"));
            }

            // width == null resets the viewport and device pixel ratio to the window's own.
            internal void SetViewport(string contextId, int? width, int? height)
            {
                object context = Context(contextId);
                MethodInfo setViewport = context.GetType().GetMethod("SetViewportAsync")
                    ?? throw Missing("BrowsingContext.SetViewportAsync");
                object options = Activator.CreateInstance(setViewport.GetParameters()[0].ParameterType)!;
                object? viewport = width.HasValue
                    ? Activator.CreateInstance(RequireType("BrowsingContext.Viewport"), (long)width.Value, (long)height!.Value)
                    : null;
                SetOptional(options, "Viewport", viewport);
                SetOptional(options, "DevicePixelRatio", width.HasValue ? (object)1.0 : null);
                Run(() => setViewport.Invoke(context, WithDefaults(setViewport, options)));
            }

            // script.callFunction in the given context. Each sharedId is passed as a
            // node argument. With includeShadowTree "all" returned nodes carry their
            // shadow roots, closed ones included.
            internal object? CallFunction(string contextId, string functionDeclaration,
                IEnumerable<string> sharedIdArguments, bool includeShadowTree)
            {
                object context = Context(contextId);
                PropertyInfo scriptProperty = context.GetType().GetProperty("Script")
                    ?? throw Missing("BrowsingContext.Script");
                object script = scriptProperty.GetValue(context)!;
                MethodInfo callFunction = scriptProperty.PropertyType.GetMethods()
                    .FirstOrDefault(m => m.Name == "CallFunctionAsync" && !m.IsGenericMethodDefinition)
                    ?? throw Missing("Script.CallFunctionAsync");

                ParameterInfo[] parameters = callFunction.GetParameters();
                ParameterInfo optionsParameter = parameters.First(p => p.ParameterType.Name == "CallFunctionOptions");
                object options = Activator.CreateInstance(optionsParameter.ParameterType)!;

                PropertyInfo argumentsProperty = options.GetType().GetProperty("Arguments") ?? throw Missing("CallFunctionOptions.Arguments");
                Type sharedReferenceType = RequireType("Script.SharedReferenceLocalValue");
                Type localValueType = (Nullable.GetUnderlyingType(argumentsProperty.PropertyType) ?? argumentsProperty.PropertyType)
                    .GetGenericArguments()[0];
                object?[] sharedReferences = sharedIdArguments.Select(id => Activator.CreateInstance(sharedReferenceType, id)).ToArray();
                Array argumentArray = Array.CreateInstance(localValueType, sharedReferences.Length);
                Array.Copy(sharedReferences, argumentArray, sharedReferences.Length);
                argumentsProperty.SetValue(options, ToSequence(argumentsProperty.PropertyType, argumentArray, localValueType));

                if (includeShadowTree)
                {
                    PropertyInfo serializationProperty = options.GetType().GetProperty("SerializationOptions")
                        ?? throw Missing("CallFunctionOptions.SerializationOptions");
                    object serialization = Activator.CreateInstance(serializationProperty.PropertyType)!;
                    Type shadowTreeType = RequireType("Script.ShadowTree");
                    serialization.GetType().GetProperty("IncludeShadowTree")!.SetValue(serialization, Enum.Parse(shadowTreeType, "All"));
                    serialization.GetType().GetProperty("MaxDomDepth")!.SetValue(serialization, 0L);
                    serializationProperty.SetValue(options, serialization);
                }

                object?[] args = parameters.Select(p =>
                    p.ParameterType == typeof(string) ? functionDeclaration :
                    p.ParameterType == typeof(bool) ? false :
                    p == optionsParameter ? options :
                    DefaultValue(p)).ToArray();
                object? evaluateResult = Run(() => callFunction.Invoke(script, args));

                PropertyInfo? resultProperty = evaluateResult?.GetType().GetProperty("Result");
                if (resultProperty == null)
                    throw new WebDriverException($"BiDi script.callFunction did not succeed: {evaluateResult}");
                return resultProperty.GetValue(evaluateResult);
            }

            // (hostSharedId, shadowRootSharedId) for every node in an array result
            // whose shadow root is closed.
            internal static List<(string host, string root)> ClosedShadowRootPairs(object? arrayRemoteValue)
            {
                var pairs = new List<(string, string)>();
                foreach (object node in Items(arrayRemoteValue))
                {
                    object? shadowRoot = Property(Property(node, "Value"), "ShadowRoot");
                    string? mode = Property(Property(shadowRoot, "Value"), "Mode")?.ToString();
                    if (Property(node, "SharedId") is string hostId &&
                        Property(shadowRoot, "SharedId") is string rootId &&
                        "closed".Equals(mode, StringComparison.OrdinalIgnoreCase))
                        pairs.Add((hostId, rootId));
                }
                return pairs;
            }

            public void Dispose()
            {
                try
                {
                    MethodInfo dispose = _bidi.GetType().GetMethod("DisposeAsync", Type.EmptyTypes)
                        ?? throw Missing("BiDi.DisposeAsync");
                    Run(() =>
                    {
                        object? valueTask = dispose.Invoke(_bidi, null);
                        return valueTask!.GetType().GetMethod("AsTask")!.Invoke(valueTask, null);
                    });
                }
                catch (Exception e)
                {
                    Log($"Could not close BiDi connection: {UnwrapMessage(e)}", "debug");
                }
            }

            private object Context(string contextId) =>
                Activator.CreateInstance(RequireType("BrowsingContext.BrowsingContext"), _bidi, contextId)!;

            private static IEnumerable<object> Items(object? remoteValue) =>
                (Property(remoteValue, "Value") as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>();

            private static object? Property(object? target, string name) =>
                target?.GetType().GetProperties().FirstOrDefault(p => p.Name == name)?.GetValue(target);

            // Optional<T> distinguishes "omit" from an explicit null on the wire.
            private static void SetOptional(object target, string name, object? value)
            {
                PropertyInfo property = target.GetType().GetProperty(name) ?? throw Missing(name);
                Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                property.SetValue(target, type.IsGenericType && type.GetGenericTypeDefinition().Name == "Optional`1"
                    ? Activator.CreateInstance(type, new object?[] { value })
                    : value);
            }

            private static object ToSequence(Type targetType, Array items, Type elementType)
            {
                Type type = Nullable.GetUnderlyingType(targetType) ?? targetType;
                return type.IsAssignableFrom(items.GetType())
                    ? items
                    : type.Assembly.GetType("System.Collections.Immutable.ImmutableArray")!.GetMethods()
                        .First(m => m.Name == "CreateRange" && m.GetParameters().Length == 1 && m.GetGenericArguments().Length == 1)
                        .MakeGenericMethod(elementType).Invoke(null, new object[] { items })!;
            }

            private static object?[] WithDefaults(MethodInfo method, object first) =>
                method.GetParameters().Select((p, i) => i == 0 ? first : DefaultValue(p)).ToArray();

            private static object? DefaultValue(ParameterInfo parameter) =>
                parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;

            private static Type RequireType(string name) =>
                typeof(WebDriver).Assembly.GetType(Ns + name) ?? throw Missing(Ns + name);

            private static MissingMemberException Missing(string name) =>
                new MissingMemberException($"Selenium BiDi API not available: {name}");

            // Run on the thread pool so a caller's SynchronizationContext cannot
            // deadlock the blocking wait.
            private static object? Run(Func<object?> start)
            {
                var task = Task.Run(async () =>
                {
                    var inner = (Task)start()!;
                    await inner.ConfigureAwait(false);
                    return inner.GetType().GetProperty("Result")?.GetValue(inner);
                });
                if (!task.Wait(CommandTimeout))
                    throw new WebDriverTimeoutException("Timed out waiting for a BiDi command");
                return task.Result;
            }
        }
    }
}
