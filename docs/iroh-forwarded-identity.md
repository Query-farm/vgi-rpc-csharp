# Forwarded Iroh identity

An Iroh-to-VGI bridge authenticates the remote Iroh peer cryptographically, then forwards the
verified 32-byte EndpointId to a worker inside an operator-controlled proxy boundary. The worker
does not accept an issuer, address, capability, or credential from the bridge. It obtains the
issuer locally and exposes the lowercase 64-digit hexadecimal EndpointId as the stable subject.

## Raw TCP

The bridge sends PROXY protocol v2 command `PROXY`, family/protocol `UNSPEC` (`0x00`), and one
experimental VGI TLV (`0xE0`). The TLV value is version byte `1` followed by the raw 32-byte
EndpointId. Enable that form explicitly:

```csharp
var options = new TcpServerOptions
{
    ProxyProtocolV2Required = true,
    TrustedProxyAddresses = ["127.0.0.1"],
    IrohProxyIssuer = "production-mesh",
    PeerAuthenticationPolicy = PeerAuthenticationPolicies.Primary("iroh"),
};
```

The ordinary `ProxyProtocolV2.Parse` and `ReadAsync` APIs remain IP-only and reject
`PROXY/UNSPEC`. The dedicated `ParseIrohIdentity` and `ReadIrohIdentityAsync` APIs accept only the
Iroh form. Missing, duplicate, wrong-version, wrong-sized, or IP-family Iroh identity TLVs fail
closed. Structurally valid bounded unknown TLVs remain ignorable.

## HTTP

The bridge must remove every client-supplied `VGI-Forwarded-Iroh-Endpoint` field and set exactly
one lowercase 64-digit EndpointId. Configure the provider with the same local issuer and exact
bridge allowlist:

```csharp
var provider = IrohPeerIdentityProviders.Forwarded(
    "production-mesh",
    ["127.0.0.1"]);
```

Use it with `PeerIdentityAuthentication.Compose` and install
`UseVgiRpcPhysicalPeerSnapshot()` before any forwarded-address rewriting middleware, as described
in the main README. Missing headers produce `NoMatch`; untrusted bridges, duplicate fields,
controls, uppercase, whitespace, and malformed EndpointIds do not produce identity.

Both adapters mark delivery assurance as `ConfiguredProxy` and preserve
`original_assurance=cryptographic_peer`. This means the bridge performed the original
cryptographic verification, while the worker trusts that claim only because the immediate sender
matches its exact operator-configured proxy boundary. Prevent direct access to the worker listener.
