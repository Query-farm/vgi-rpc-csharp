# Cloud and reverse-proxy SPIFFE identity

`SpiffePeerIdentityProviders` converts verified HTTP ingress evidence into the
provider-neutral `PeerIdentity` contract. All factories require an exact set of
immediate proxy addresses and produce `ConfiguredProxy` assurance. Deployments
must prevent direct backend access and configure the proxy to replace every
identity header; forwarded certificate names, hashes, and source addresses are
attributes, never principals.

- `Nginx(...)` requires `$ssl_client_verify=SUCCESS` and validates the escaped
  leaf certificate as an X.509-SVID.
- `AwsAlb(...)` is only for ALB mTLS verify mode. ALB supplies the encoded leaf
  but no per-request verified boolean, so listener mode, trust store, header
  replacement, and backend isolation are operator-enforced parts of the boundary.
- `GcpLoadBalancer(...)` requires `client_cert_present=true`,
  `client_cert_chain_verified=true`, no validation error, and one allowed
  canonical `client_cert_spiffe_id`.
- `AzureApplicationGateway(...)` requires strict-mode server variables rewritten
  to the configured certificate and `SUCCESS` verification headers.
- `EnvoyXfcc(...)` requires an adjacent mTLS Envoy using
  `forward_client_cert_details: SANITIZE_SET`. Exactly one XFCC element, URI, and
  SHA-256 hash are accepted; append chains and ambiguous fields fail closed.

The certificate profiles require a valid non-CA leaf, exactly one URI SAN,
critical digital-signature key usage without certificate-signing permissions,
and both client/server EKUs when EKU is present. SPIFFE IDs must be ASCII,
unescaped canonical IDs in an explicitly allowed trust domain.

ASP.NET Core may expose repeated HTTP fields as one framework-normalized value.
VGI rejects multiple values and case-varied keys when the framework preserves
them, but deployment configuration must also strip and set these headers at the
final trusted hop.
