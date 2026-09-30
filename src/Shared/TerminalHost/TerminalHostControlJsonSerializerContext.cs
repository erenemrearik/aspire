// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
#if !ASPIRE_TERMINAL_HOST
using System.Text.Json.Serialization.Metadata;
#endif
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Aspire.Shared.TerminalHost;

/// <summary>
/// Source-generated serialization for the terminal host control protocol.
/// </summary>
[JsonSerializable(typeof(TerminalHostSessionInfo))]
[JsonSerializable(typeof(TerminalHostInfoResponse))]
[JsonSerializable(typeof(CommonErrorData))]
#if ASPIRE_TERMINAL_HOST
[JsonSerializable(typeof(RequestId))]
#endif
// StreamJsonRpc uses object for untyped results (including shutdown's null response) and error-data fallback.
[JsonSerializable(typeof(object))]
internal partial class TerminalHostControlJsonSerializerContext : JsonSerializerContext
{
    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode", Justification = "Using the Json source generator.")]
    [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode", Justification = "Using the Json source generator.")]
    internal static SystemTextJsonFormatter CreateRpcMessageFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions = new JsonSerializerOptions
        {
#if ASPIRE_TERMINAL_HOST
            TypeInfoResolver = Default
#else
            TypeInfoResolver = JsonTypeInfoResolver.Combine(Default, new RequestIdTypeInfoResolver())
#endif
        };
        return formatter;
    }

#if !ASPIRE_TERMINAL_HOST
    // Hosting uses StreamJsonRpc 2.25.29, unlike the CLI and TerminalHost's 2.23.32-alpha.
    // Only the stable version annotates RequestId with its internal STJ converter, which
    // the source generator cannot reference. Reuse it for $/cancelRequest until it is public.
    // https://github.com/microsoft/vs-streamjsonrpc/blob/v2.25.29/src/StreamJsonRpc/RequestIdSTJsonConverter.cs
    private sealed class RequestIdTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            return type == typeof(RequestId)
                ? JsonMetadataServices.CreateValueInfo<RequestId>(options, options.Converters.First(converter => converter.CanConvert(type)))
                : null;
        }
    }
#endif
}
