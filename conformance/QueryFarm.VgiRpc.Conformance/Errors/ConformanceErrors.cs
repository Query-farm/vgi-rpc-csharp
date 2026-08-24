namespace QueryFarm.VgiRpc.Conformance.Errors;

// The cross-language conformance suite asserts the wire's error_type token literally equals
// Python's built-in exception class names ("ValueError", "RuntimeError", "TypeError") — see
// vgi_rpc/conformance/_runner.py's error-propagation tests. LogMessage.FromException uses
// exception.GetType().Name, so these three names are load-bearing, not stylistic.

public sealed class ValueError(string message) : Exception(message);

public sealed class RuntimeError(string message) : Exception(message);

public sealed class TypeError(string message) : Exception(message);
