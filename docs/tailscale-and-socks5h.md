# Tailscale evidence and SOCKS5h dialing

`TailscalePeerIdentityProviders.Serve(...)` accepts Serve headers only from an
exact configured immediate peer. The backend must be unreachable except through
that proxy, and Serve must replace the headers. Funnel requests are
`NotApplicable`. A Serve login is verified evidence within that proxy boundary,
but is deliberately a login-stability subject; capability-only requests remain
subjectless. Capabilities are bounded opaque JSON arrays and are never logged or
treated as a VGI role language.

`TailscaleLocalApiProvider` snapshots a raw TCP peer and performs one LocalAPI
WhoIs request per connection. It does not cache and never invokes the Tailscale
CLI. Untagged nodes use `user:<numeric-id>`; tagged nodes use
`node:<stable-node-id>` and do not use their `UserProfile` as the caller.
Destination- and service-scoped capability targets are retained in the evidence.
For raw TCP behind an L4 proxy, `TcpServerOptions.ProxyProtocolV2Required`
accepts only PROXY protocol v2 from exact IP literals in
`TrustedProxyAddresses`. The immediate peer is checked before reading, the
preamble has its own deadline and allocation limit, and only TCP over IPv4 or
IPv6 is accepted. `LOCAL`, `UNSPEC`, UDP, malformed/truncated frames, and
untrusted senders fail closed. Unknown TLVs are ignored within the total bound;
bytes after the preamble remain available to the VGI reader. The LocalAPI lookup
uses the asserted source while retaining the immediate proxy address as audit
evidence. Keep the backend unreachable except through the trusted proxy.
`TailscaleLocalApiHttpClient` supports:

- `ForUnixSocket(...)` on Unix platforms;
- `ForWindowsNamedPipe(...)` using .NET's native named-pipe stream;
- an explicitly configured local HTTP/token endpoint, including macOS userspace
  endpoints supplied by the operator.

No automatic platform discovery is claimed. Endpoint and token discovery remains
deployment configuration, and normal logs must not contain tokens, profiles, or
raw capabilities.

Raw clients can use `SocketTransport.ConnectTcpAsync(...)` or
`RpcClient.ConnectTcpAsync(...)` with an explicit credential-free
`socks5h://host:port` proxy. `HttpRpcClientOptions.TcpProxy` applies the same path
at the HTTP socket boundary. Target hostnames are converted to IDNA ASCII and
sent to the proxy without local target DNS; IPv4 and IPv6 literals use native
SOCKS address types. A single monotonic setup deadline covers proxy resolution,
connection, negotiation, and target connection. Cancellation reaches partial
reads and writes, proxy failures never fall back to direct TCP, process proxy
environment is ignored, and successful sockets use `TCP_NODELAY`.

The adversarial tests mirror the canonical transport identity vectors maintained
by the Python conformance repository; this assembly has no runtime dependency on
a sibling checkout.
