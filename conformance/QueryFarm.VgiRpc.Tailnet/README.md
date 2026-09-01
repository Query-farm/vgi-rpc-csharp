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
- `server-tcp`: a raw TCP worker that snapshots LocalAPI identity once per connection. Optional
  `--proxy-protocol-v2 --trusted-proxy-address <exact-ip>` requires a bounded PROXY v2 preamble
  from that immediate peer before resolving the asserted source; `--service-name` selects a
  destination-scoped Tailscale Service capability target.

Client qualification validates the provider status, issuer, evidence source, assurance, subject
kind and stability, capability, optional tag and capability-target kind, proxy topology signal,
authentication state, principal-to-identity match, and a non-empty evidence binding. It calls the
snapshot method twice and requires byte-identical evidence to exercise stable connection reuse.

The HTTP worker accepts capability-only evidence and therefore intentionally remains anonymous;
its service method rejects a user subject, including a spoofed login that reaches the worker
without being stripped and replaced by the trusted Serve proxy.
