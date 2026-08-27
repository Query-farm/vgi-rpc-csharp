<p align="center">
  <img src="https://raw.githubusercontent.com/Query-farm/vgi-rpc-csharp/main/assets/vgi-logo.png" alt="Vector Gateway Interface logo" width="320">
</p>

<h1 align="center">vgi-rpc for .NET</h1>

<p align="center">
  Transport-agnostic RPC framework built on <a href="https://arrow.apache.org/">Apache Arrow</a> IPC serialization.<br>
  Built by <a href="https://query.farm">🚜 Query.Farm</a>
</p>

<p align="center">
  <a href="https://github.com/Query-farm/vgi-rpc-csharp/actions/workflows/ci.yml"><img src="https://github.com/Query-farm/vgi-rpc-csharp/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://www.nuget.org/packages/QueryFarm.VgiRpc"><img src="https://img.shields.io/nuget/v/QueryFarm.VgiRpc" alt="NuGet"></a>
  <a href="https://www.nuget.org/packages/QueryFarm.VgiRpc"><img src="https://img.shields.io/nuget/dt/QueryFarm.VgiRpc" alt="NuGet downloads"></a>
  <a href="https://github.com/Query-farm/vgi-rpc-csharp/blob/main/LICENSE"><img src="https://img.shields.io/github/license/Query-farm/vgi-rpc-csharp" alt="License"></a>
</p>

Define RPC contracts as ordinary C# interfaces. vgi-rpc derives Apache Arrow schemas from those
interfaces and provides reflection-based server dispatch and typed unary client proxies. There
are no `.proto` files or code-generation steps, and structured data remains in Arrow's columnar
format instead of being converted to JSON.

This implementation is wire-compatible with the canonical Python implementation and the other
vgi-rpc ports, allowing clients and servers written in different supported languages to
interoperate.

**Key features:**

- **Interface-based contracts** — define services with standard C# interfaces and async methods
- **Apache Arrow IPC wire format** — efficient serialization for structured and batch-oriented data
- **Cross-language interoperability** — compatible with the Python, Go, Rust, TypeScript, and Java implementations
- **Unary and streaming dispatch** — producer and exchange streaming patterns are supported server-side
- **Multiple transports** — in-process pipes, stdio, Unix domain sockets, TCP, shared memory, and HTTP
- **Automatic schema inference** — CLR primitives, collections, enums, POCOs, and Arrow record batches map to Arrow types
- **HTTP security** — bearer authentication, mTLS, JWT/JWKS validation, OAuth 2.0 PKCE, CORS, and proxy proof
- **Large-payload offload** — transparent externalization to Amazon S3, S3-compatible stores, or Google Cloud Storage
- **Observability** — access logs, OpenTelemetry-compatible tracing and metrics, and Sentry instrumentation

## Installation

Install the core package:

```bash
dotnet add package QueryFarm.VgiRpc
```

Add integrations as needed:

| Package | Purpose |
|---|---|
| [`QueryFarm.VgiRpc`](https://www.nuget.org/packages/QueryFarm.VgiRpc) | Core wire protocol, reflection-based dispatch, streaming, and pipe, stdio, Unix socket, TCP, and shared-memory transports |
| [`QueryFarm.VgiRpc.Http`](https://www.nuget.org/packages/QueryFarm.VgiRpc.Http) | ASP.NET Core HTTP transport, authentication, sticky sessions, proxy proof, compression, and external payload support |
| [`QueryFarm.VgiRpc.Http.OAuth`](https://www.nuget.org/packages/QueryFarm.VgiRpc.Http.OAuth) | JWT/JWKS validation and OAuth 2.0 PKCE authentication |
| [`QueryFarm.VgiRpc.S3`](https://www.nuget.org/packages/QueryFarm.VgiRpc.S3) | Amazon S3 and S3-compatible external storage with presigned URLs |
| [`QueryFarm.VgiRpc.Gcs`](https://www.nuget.org/packages/QueryFarm.VgiRpc.Gcs) | Google Cloud Storage external storage with V4 signed URLs |
| [`QueryFarm.VgiRpc.OpenTelemetry`](https://www.nuget.org/packages/QueryFarm.VgiRpc.OpenTelemetry) | OpenTelemetry-compatible server tracing and metrics |
| [`QueryFarm.VgiRpc.Sentry`](https://www.nuget.org/packages/QueryFarm.VgiRpc.Sentry) | Sentry error reporting and optional performance transactions |

The packages target .NET 10 and require the .NET 10 SDK to build from source.

## Quick start

Define a service, implement it, and connect a typed client to the server over an in-process pipe:

```csharp
using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;

public interface IGreeter
{
    Task<string> GreetAsync(string name);
}

public sealed class Greeter : IGreeter
{
    public Task<string> GreetAsync(string name) =>
        Task.FromResult($"Hello, {name}!");
}

var (clientTransport, serverTransport) = PipeTransport.CreatePair();

var server = new RpcServer(typeof(IGreeter), new Greeter());
var serveTask = server.ServeAsync(serverTransport);

var connection = new RpcConnection<IGreeter>(clientTransport);
IGreeter client = connection.CreateProxy();

Console.WriteLine(await client.GreetAsync("World")); // Hello, World!

clientTransport.Output.Close();
await serveTask;
```

Methods must return `Task` or `Task<T>`. By default, method names are converted to `snake_case`
on the wire and a trailing `Async` suffix is removed, so `GreetAsync` becomes `greet`. Use
`[RpcName("...")]` to override a method, parameter, or property name.

See the complete
[`01-hello-world`](https://github.com/Query-farm/vgi-rpc-csharp/tree/main/examples/01-hello-world)
example for a runnable project.

## Service contracts

Parameters and return values may use CLR primitives, common generic collections, enums, Arrow
record batches, or POCOs with a parameterless constructor and public settable properties. Nested
POCOs map to nested Arrow structs.

| C# type | Arrow type |
|---|---|
| `string` | `utf8` |
| `byte[]` | `binary` |
| `sbyte` / `short` / `int` / `long` | `int8` / `int16` / `int32` / `int64` |
| `byte` / `ushort` / `uint` / `ulong` | `uint8` / `uint16` / `uint32` / `uint64` |
| `float` / `double` | `float32` / `float64` |
| `bool` | `bool` |
| `List<T>` | `list<T>` |
| `Dictionary<K, V>` | `map<K, V>` |
| `HashSet<T>` | `list<T>` |
| `enum` | `dictionary(int16, utf8)` |
| `T?` | nullable `T` |
| POCO | `struct` |
| `Apache.Arrow.RecordBatch` | `binary` containing an Arrow IPC stream |
| `[LargeWidth] string` / `[LargeWidth] byte[]` | `large_utf8` / `large_binary` |

A service method may also declare a trailing optional `ICallContext` parameter. The server
injects it for access to request-scoped logging and HTTP sticky-session state; it is excluded from
the wire schema.

## Transports

| Transport | Server API | C# client API |
|---|---|---|
| In-process pipe | `PipeTransport.CreatePair()` | Typed unary proxy |
| Standard input/output | `StdioTransport` | Custom `IRpcTransport` wrapper |
| Unix domain socket | `SocketTransport.ServeUnixAsync(...)` | Connected `SocketTransport` |
| TCP | `SocketTransport.ServeTcpAsync(...)` | Connected `SocketTransport` |
| HTTP | `MapVgiRpc(...)` from `QueryFarm.VgiRpc.Http` | `WireReader` / `WireWriter` over `HttpClient` |
| Shared memory | Negotiated alongside pipe or socket transport | Built in |

The typed C# proxy currently supports unary calls over an already-connected `IRpcTransport`.
Typed streaming consumption, subprocess launching, and a typed HTTP client are not yet part of
the public client API. The
[`03-subprocess`](https://github.com/Query-farm/vgi-rpc-csharp/tree/main/examples/03-subprocess)
and [`04-http`](https://github.com/Query-farm/vgi-rpc-csharp/tree/main/examples/04-http) examples
show the corresponding lower-level client integrations. Servers remain interoperable with typed
clients from the other vgi-rpc implementations.

## Streaming

Streaming service methods return `RpcStream<TState>`, where `TState` derives from
`ProducerState` or `ExchangeState`. The server invokes `ProduceAsync` or `ExchangeAsync` for each
stream iteration:

```csharp
public sealed class CounterState(long count) : ProducerState
{
    private long _current;

    public override Task ProduceAsync(
        OutputCollector output,
        ICallContext? context,
        CancellationToken cancellationToken)
    {
        if (_current >= count)
        {
            output.Finish();
            return Task.CompletedTask;
        }

        output.Emit(ValueCodec.BuildRow(CounterSchema.Output, [_current++]));
        return Task.CompletedTask;
    }
}

public interface ICounterService
{
    Task<RpcStream<CounterState>> CountToAsync(long count);
}
```

Producer and exchange streaming are conformance-tested over pipe, Unix socket, TCP, and HTTP
transports.

## HTTP and authentication

`QueryFarm.VgiRpc.Http` integrates with ASP.NET Core through `MapVgiRpc(...)`. Its authentication
delegate can validate bearer tokens, client certificates, or application-specific credentials
before dispatch. `QueryFarm.VgiRpc.Http.OAuth` adds JWT/JWKS validation, protected-resource
metadata, and an OAuth 2.0 PKCE browser flow.

The HTTP package also includes CORS handling, request and response size limits, zstd content
encoding, sticky sessions, token introspection, and proxy-proof validation.

## External storage

Large Arrow batches can be uploaded to object storage and replaced on the wire with an external
location descriptor. The receiving peer resolves the descriptor with parallel range requests.

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
        ExternalizeThresholdBytes = 1_048_576,
        Compression = new Compression(),
    },
};

app.MapVgiRpc(server, externalization: externalization);
```

`S3Storage` supports Amazon S3 and configurable S3-compatible endpoints. `GcsStorage` provides
the equivalent integration for Google Cloud Storage. Both implementations support server-managed
uploads and signed upload/download URL pairs.

## Error handling

Remote errors surface as `RpcException` and include a stable error kind, the remote exception
type, message, and traceback. A failed call does not invalidate an otherwise healthy persistent
connection. Common protocol conditions have typed exceptions, including
`MethodNotImplementedException`, `ProtocolVersionException`, `SessionLostException`,
`ServerDrainingException`, and `PayloadTooLargeException`.

## Examples

| Example | Description |
|---|---|
| [`01-hello-world`](https://github.com/Query-farm/vgi-rpc-csharp/tree/main/examples/01-hello-world) | Minimal typed unary call over an in-process pipe |
| [`02-structured-types`](https://github.com/Query-farm/vgi-rpc-csharp/tree/main/examples/02-structured-types) | POCO parameters with enums, lists, and maps |
| [`03-subprocess`](https://github.com/Query-farm/vgi-rpc-csharp/tree/main/examples/03-subprocess) | Worker and client over stdio, including remote error handling |
| [`04-http`](https://github.com/Query-farm/vgi-rpc-csharp/tree/main/examples/04-http) | ASP.NET Core server and a wire-level `HttpClient` client |

## Development

The SDK version is pinned in `global.json`.

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet format --verify-no-changes --exclude third_party
```

Run the cross-language conformance suite with:

```bash
./run_tests.sh
```

The suite uses the canonical Python implementation and covers all supported transports and wire
features. See
[`docs/wire-protocol.md`](https://github.com/Query-farm/vgi-rpc-csharp/blob/main/docs/wire-protocol.md)
for the wire format and
[`third_party/apache-arrow-dotnet/README.md`](https://github.com/Query-farm/vgi-rpc-csharp/blob/main/third_party/apache-arrow-dotnet/README.md)
for details about the narrowly patched Arrow dependency.

## Related projects

- [`vgi-rpc`](https://github.com/Query-farm/vgi-rpc) — canonical Python implementation and conformance suite
- [`vgi-rpc-go`](https://github.com/Query-farm/vgi-rpc-go) — Go implementation
- [`vgi-rpc-rust`](https://github.com/Query-farm/vgi-rpc-rust) — Rust implementation
- [`vgi-rpc-typescript`](https://github.com/Query-farm/vgi-rpc-typescript) — TypeScript implementation
- [`vgi-rpc-java`](https://github.com/Query-farm/vgi-rpc-java) — Java implementation

## License

Apache License 2.0 — see
[`LICENSE`](https://github.com/Query-farm/vgi-rpc-csharp/blob/main/LICENSE) and
[`NOTICE`](https://github.com/Query-farm/vgi-rpc-csharp/blob/main/NOTICE).
