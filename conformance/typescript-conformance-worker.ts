#!/usr/bin/env bun

// The TypeScript RPC implementation has separate native-stream and HTTP
// conformance entry points. The shared client-role matrix expects one worker
// executable whose --http flag selects the latter, so keep that adaptation at
// the test boundary instead of teaching the C# client about worker-specific
// launch commands.
import { join } from "node:path";
import { pathToFileURL } from "node:url";

const root = process.env.VGI_TYPESCRIPT_RPC_ROOT;
if (!root) {
  process.stderr.write("VGI_TYPESCRIPT_RPC_ROOT must point to a vgi-rpc-typescript checkout\n");
  process.exit(2);
}

const args = process.argv.slice(2);
const httpIndex = args.indexOf("--http");
const entryPoint = httpIndex === -1 ? "conformance.ts" : "conformance-http.ts";
if (httpIndex !== -1) args.splice(httpIndex, 1);

// The selected entry point parses process.argv itself.
process.argv = [process.argv[0], process.argv[1], ...args];
await import(pathToFileURL(join(root, "examples", entryPoint)).href);
