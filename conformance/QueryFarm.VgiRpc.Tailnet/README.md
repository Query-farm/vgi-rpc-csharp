# C# live-Tailnet qualification adapter

This executable exercises only transport and identity surfaces the C# port currently implements:

- `client-tcp`: direct TCP or explicit credential-free `socks5h://` dialing.
- `client-http`: direct HTTP or explicit credential-free `socks5h://` dialing. `--spoof-login <login>`
  deliberately injects a `Tailscale-User-Login` request header for the reverse-Serve spoof test.
- `server-http`: a capability-only worker behind Tailscale Serve. It accepts `--host`, `--port`,
  `--issuer`, and `--expected-capability`; `--trusted-proxy-ipv4`/`--trusted-proxy-ipv6` default
  to the exact loopback addresses. The physical-peer snapshot
  middleware runs before any address-rewriting middleware, Serve headers are trusted only from
  exact configured proxy IP literals, and each RPC call requires verified capability evidence.

Client qualification validates the provider status, issuer, evidence source, assurance, subject
kind and stability, capability, optional tag and capability-target kind, proxy topology signal,
authentication state, principal-to-identity match, and a non-empty evidence binding. It calls the
snapshot method twice and requires byte-identical evidence to exercise stable connection reuse.

The HTTP worker accepts capability-only evidence and therefore intentionally remains anonymous;
its service method rejects a user subject, including a spoofed login that reaches the worker
without being stripped and replaced by the trusted Serve proxy.

Raw-TCP server qualification is intentionally absent. `SocketTransport.ServeTcpAsync` supplies a
plain `IRpcTransport`, while the persistent `RpcServer` call contexts currently expose anonymous
authentication and empty peer evidence. `TailscaleLocalApiProvider` exists for ASP.NET request
composition, but attaching it outside that request pipeline would require a new connection-level
identity snapshot seam in core. Advertising raw LocalAPI coverage here would therefore be false.
