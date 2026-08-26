<!-- markdownlint-disable MD041 -->
# vgi-rpc-csharp

[![CI](https://github.com/Query-farm/vgi-rpc-csharp/actions/workflows/ci.yml/badge.svg)](https://github.com/Query-farm/vgi-rpc-csharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/QueryFarm.VgiRpc.svg)](https://www.nuget.org/packages/QueryFarm.VgiRpc)

A C# port of [vgi-rpc](https://github.com/Query-farm/vgi-rpc) — a transport-agnostic RPC framework
that uses Apache Arrow's IPC Streaming Format as its wire protocol, with no IDL/codegen step:
services are plain interfaces, and Arrow schemas are derived from them by reflection. This port
targets **.NET 10** and runs on **Windows and Linux**.

> **Status: feature-complete.** Every transport, auth mode, and optional subsystem is implemented
> and passing the real cross-language conformance suite, including real streaming
> (producer/exchange) calls over every transport (pipe, Unix domain socket, TCP, HTTP). Published
> to NuGet.org — see **Installation** below. (Implementation history and design rationale live in
> [`docs/roadmap.md`](docs/roadmap.md) for anyone digging deeper.)

## Wire compatibility

vgi-rpc-csharp implements the same byte-level wire protocol as the canonical Python implementation
and its other ports (Go, Rust, TypeScript, Java), so peers written in any of those languages can
interoperate over pipe/stdio, Unix domain socket, TCP, or HTTP. See
[`docs/wire-protocol.md`](docs/wire-protocol.md) for the protocol summary and the one C#-specific
implementation wrinkle: the stock `Apache.Arrow` NuGet package can't write or read per-batch
`custom_metadata` (every vgi-rpc protocol semantic — method name, versions, log/error info, stream
continuation tokens — rides on it), so this repo vendors a small, surgically patched copy of
`apache/arrow-dotnet` instead of hand-rolling the FlatBuffers framing itself — published as
`QueryFarm.Arrow`/`QueryFarm.Arrow.Scalars` (a `QueryFarm.VgiRpc` dependency, pulled in
automatically — no separate install step) rather than the real `Apache.Arrow`/
`Apache.Arrow.Scalars`, which stay the official, unpatched upstream packages. See
`third_party/apache-arrow-dotnet/README.md` for exactly what's patched, why, and why the fork
needed its own distinct package identity.

## Installation

```bash
dotnet add package QueryFarm.VgiRpc               # core: wire framing, dispatch, transports
dotnet add package QueryFarm.VgiRpc.Http           # HTTP transport, sticky sessions, proxy-proof, auth
dotnet add package QueryFarm.VgiRpc.Http.OAuth     # JWT/JWKS validation, OAuth2/PKCE
dotnet add package QueryFarm.VgiRpc.S3             # S3-backed external storage
dotnet add package QueryFarm.VgiRpc.Gcs            # GCS-backed external storage
dotnet add package QueryFarm.VgiRpc.OpenTelemetry  # tracing/metrics instrumentation
dotnet add package QueryFarm.VgiRpc.Sentry         # error-capture instrumentation
```

Requires the .NET 10 SDK. As of 0.4.0, `QueryFarm.VgiRpc` correctly resolves its patched-Arrow
dependency (`QueryFarm.Arrow`) from nuget.org on its own — versions before 0.4.0 declared a
broken transitive dependency on the real, unpatched, years-old official `Apache.Arrow` package and
would not build for a consumer without also vendoring this repo's own patched source (see
`third_party/apache-arrow-dotnet/README.md`'s "Published as QueryFarm.Arrow" section); upgrade if
you're on an earlier version.

## Quick Start

The quickest way to get started: define a service interface, implement it, and call it in-process
over an in-memory pipe — no subprocess or network needed.

```csharp
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;

// 1. Define the service interface. Methods must return Task/Task<T>. The wire
//    method name is derived from the C# name with "Async" stripped and
//    converted to snake_case (GreetAsync -> "greet"); override with
//    [RpcName("...")] when needed.
public interface IGreeter
{
    Task<string> GreetAsync(string name);
}

// 2. Implement it.
public sealed class Greeter : IGreeter
{
    public Task<string> GreetAsync(string name) => Task.FromResult($"Hello, {name}!");
}

// 3. Wire up a transport, start the server, and call methods through a
//    typed client proxy.
var (clientTransport, serverTransport) = PipeTransport.CreatePair();

var server = new RpcServer(typeof(IGreeter), new Greeter());
var serveTask = server.ServeAsync(serverTransport);

var connection = new RpcConnection<IGreeter>(clientTransport);
IGreeter client = connection.CreateProxy();

Console.WriteLine(await client.GreetAsync("World")); // Hello, World!

clientTransport.Output.Close();
await serveTask;
```

See [`examples/01-hello-world`](examples/01-hello-world) for the full runnable version, and the
**Examples** table below for more.

## Modules

| Package | Purpose |
|---|---|
| `QueryFarm.VgiRpc` | Core: wire framing, reflection-based dispatch, streaming, pipe/stdio/Unix/TCP/SHM transports, access log |
| `QueryFarm.VgiRpc.Http` | ASP.NET Core (Kestrel) HTTP transport, sticky sessions, proxy-proof, bearer/mTLS auth |
| `QueryFarm.VgiRpc.Http.OAuth` | JWT/JWKS validation, OAuth2/PKCE browser flow |
| `QueryFarm.VgiRpc.S3` | S3-backed external storage for large-payload offload |
| `QueryFarm.VgiRpc.Gcs` | GCS-backed external storage for large-payload offload |
| `QueryFarm.VgiRpc.OpenTelemetry` | Tracing/metrics instrumentation |
| `QueryFarm.VgiRpc.Sentry` | Error-capture instrumentation |

## Defining Services

A service is a plain C# interface. Methods must return `Task` or `Task<TResult>` — the idiomatic
async shape the client proxy (built on `System.Reflection.DispatchProxy`) requires; `ValueTask`
isn't supported client-side yet. A method may declare a trailing
[`ICallContext`](src/QueryFarm.VgiRpc/Server/ICallContext.cs) parameter (with a `= null` default,
so client call sites can omit it) to get server-injected access to client-directed logging and
(on HTTP) sticky sessions — it's excluded from the wire schema entirely:

```csharp
public interface IGreeter
{
    Task<string> EchoWithLogAsync(string value, ICallContext? ctx = null);
}

public sealed class Greeter : IGreeter
{
    public Task<string> EchoWithLogAsync(string value, ICallContext? ctx = null)
    {
        ctx!.EmitLog(VgiLogLevel.Info, "processing", new Dictionary<string, object?> { ["value"] = value });
        return Task.FromResult(value);
    }
}
```

Parameters and return types can be any plain C# class with a parameterless constructor and public
settable properties — its properties map to Arrow struct fields automatically, including nested
structs. See [`examples/02-structured-types`](examples/02-structured-types).

### Supported types

| C# type | Arrow type |
|---|---|
| `string` | `utf8` |
| `byte[]` | `binary` |
| `sbyte`/`short`/`int`/`long` | `int8`/`int16`/`int32`/`int64` (and unsigned counterparts) |
| `float`/`double` | `float32`/`float64` |
| `bool` | `bool_` |
| `List<T>` | `list<T>` |
| `Dictionary<K, V>` | `map<K, V>` |
| `HashSet<T>` | `list<T>` |
| `enum` | `dictionary(int16, utf8)` |
| `T?` / nullable reference type | nullable `T` |
| plain class (parameterless ctor + settable properties) | `struct` |
| `Apache.Arrow.RecordBatch` | `binary` (embedded Arrow IPC stream) |
| `[LargeWidth]`-annotated `string`/`byte[]` | `large_utf8`/`large_binary` (64-bit offsets) |

Wire names (method, parameter, and property names) default to a deterministic PascalCase/
camelCase → snake_case conversion; override any of them with
[`[RpcName("...")]`](src/QueryFarm.VgiRpc/Attributes/RpcNameAttribute.cs) when you need a specific
wire name. See `docs/wire-protocol.md`.

## Transports

| Transport | Server-side | Client-side |
|---|---|---|
| Pipe (in-process) | `PipeTransport.CreatePair()` | built-in |
| Subprocess (stdio) | `new StdioTransport()` | hand-rolled (no built-in helper yet — see below) |
| Unix domain socket | `SocketTransport.ServeUnixAsync(...)` | wrap a connected `Socket` in `SocketTransport` |
| TCP | `SocketTransport.ServeTcpAsync(...)` | wrap a connected `Socket` in `SocketTransport` |
| HTTP | `app.MapVgiRpc(server, ...)` (`QueryFarm.VgiRpc.Http`) | raw wire over `HttpClient` (no typed client yet — see below) |
| Shared memory | `System.IO.MemoryMappedFiles`-backed, rides alongside pipe/socket | same |

Every transport implements the small [`IRpcTransport`](src/QueryFarm.VgiRpc/Transport/IRpcTransport.cs)
interface (`Stream Input`/`Stream Output`), so writing a new one is straightforward.

**Two client-side gaps to know about before you build on this:** this port doesn't ship a
client-side subprocess-spawning transport, and doesn't ship a typed streaming or HTTP client
proxy yet. [`examples/03-subprocess`](examples/03-subprocess) and
[`examples/04-http`](examples/04-http) show the small amount of code needed to fill each gap
today (a ~30-line `IRpcTransport` wrapping `System.Diagnostics.Process`, and a direct
`WireWriter`/`WireReader` call over `HttpClient`, respectively) — both are natural things to
promote into the library later. A C# client isn't the only option, though: because every vgi-rpc
port speaks the same wire protocol, a **server built with this package can be driven by any other
port's client** (Python, Go, Rust, TypeScript, Java) with zero changes on the server side — that's
the actual point of a shared wire format, not a workaround. The cross-language conformance suite
this repo is held to (see `CLAUDE.md`) already drives every one of this port's transports,
including streaming, from the canonical Python client.

## Streaming

Streaming methods return `RpcStream<TState>`, where `TState` is a `ProducerState` or
`ExchangeState` subclass whose `ProduceAsync`/`ExchangeAsync` override is called once per
iteration:

```csharp
public sealed class CounterState(long count) : ProducerState
{
    private long _current;

    public override Task ProduceAsync(OutputCollector output, ICallContext? ctx, CancellationToken cancellationToken)
    {
        if (_current >= count)
        {
            output.Finish();
            return Task.CompletedTask;
        }

        output.Emit(ValueCodec.BuildRow(CounterSchema.Output, [_current]));
        _current++;
        return Task.CompletedTask;
    }
}

public interface ICounterService
{
    Task<RpcStream<CounterState>> CountToAsync(long count);
}
```

Server-side streaming dispatch (`RpcServer.ServeStreamAsync`) is fully implemented and
conformance-tested across every transport (pipe, Unix domain socket, TCP, HTTP). What this port
doesn't have yet is a typed C# **client** for consuming a stream — `RpcClientProxy<T>`
only supports unary calls today. A C# client is one option among several here, not the only path:
a stream server built with this package is already fully consumable today by any other vgi-rpc
port's client (the conformance suite drives exactly this, over every transport, via the Python
reference client). From C# itself, a stream is reachable at the wire level in the meantime
(`WireWriter`/`WireReader`, the same primitives `examples/04-http/Client` uses for its unary
call).

## Error Handling

Server exceptions are propagated to the client as `RpcException`:

```csharp
using QueryFarm.VgiRpc.Errors;

try
{
    await client.FailingMethodAsync();
}
catch (RpcException e)
{
    Console.WriteLine(e.ErrorType);        // e.g. "InvalidOperationException"
    Console.WriteLine(e.ErrorMessage);     // e.g. "something went wrong"
    Console.WriteLine(e.RemoteTraceback);  // full server-side traceback
}
```

Errors are transmitted as zero-row batches carrying `EXCEPTION`-level log metadata. The transport
remains usable afterward — a single failed call does not poison the connection. Well-known error
conditions surface as typed subclasses (`MethodNotImplementedException`,
`ProtocolVersionException`, `SessionLostException`, `ServerDrainingException`,
`PayloadTooLargeException`), each with a stable `ErrorKind` token.

## Authentication

`QueryFarm.VgiRpc.Http`'s `MapVgiRpc(...)` accepts an `AuthenticateDelegate` (`Task
Authenticate(HttpContext context)`) — throw an `RpcException` (or let ASP.NET Core's own
short-circuiting apply) to reject a request before it reaches the service implementation. Bearer
tokens and mTLS client certificates are both driven through this seam; `QueryFarm.VgiRpc.Http.OAuth`
layers JWT/JWKS validation and an OAuth2/PKCE browser flow on top. See
`test/QueryFarm.VgiRpc.Http.Tests` and `test/QueryFarm.VgiRpc.Http.OAuth.Tests` for real usage.

## External Storage

When a response batch exceeds a configurable size threshold, it can be transparently uploaded to
S3 or GCS and replaced with a lightweight pointer batch that the client resolves automatically via
parallel range-request fetching:

```csharp
using QueryFarm.VgiRpc.Http;
using QueryFarm.VgiRpc.S3;

var storage = S3Storage.CreateBuilder("my-bucket")
    .WithKeyPrefix("rpc-data/")
    .WithRegion(Amazon.RegionEndpoint.USEast1)
    .Build();

var externalization = new ExternalizationOptions
{
    External = new ServerExternalConfig
    {
        Storage = storage,
        ExternalizeThresholdBytes = 1_048_576, // 1 MiB (default)
        Compression = new Compression(),        // zstd level 3 by default
    },
};

app.MapVgiRpc(server, externalization: externalization);
```

`QueryFarm.VgiRpc.Gcs`'s `GcsStorage` has the equivalent builder for Google Cloud Storage. See
`test/QueryFarm.VgiRpc.S3.Tests`/`.Gcs.Tests` for real integration tests (run against real
S3/GCS-compatible servers via Testcontainers, not mocks).

## Examples

The [`examples/`](examples/) directory contains runnable projects demonstrating key features:

| Example | Description |
|---|---|
| [`01-hello-world`](examples/01-hello-world) | Minimal quickstart with in-process pipe transport |
| [`02-structured-types`](examples/02-structured-types) | POCO parameters with enums, lists, and maps |
| [`03-subprocess`](examples/03-subprocess) | Worker + client over stdio, including a hand-rolled subprocess transport and remote-error handling |
| [`04-http`](examples/04-http) | HTTP server (ASP.NET Core / Kestrel) + a raw-wire HTTP client |

Run any of them with `dotnet run --project examples/<name>` (for `03-subprocess` and `04-http`,
run the `Worker`/`Server` sub-project first — each example's own `Program.cs` has the exact
commands).

## Development

See [`CLAUDE.md`](CLAUDE.md) for build/test commands, the conformance-testing workflow, and
cross-language wire-alignment notes.

## License

Apache License 2.0 — see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).
