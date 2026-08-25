// [vgi-rpc-csharp patch] Not part of apache/arrow-dotnet#283 (the custom_metadata patch this
// vendoring exists for — see ../../README.md) — a second, separate, self-authored addition. See
// that README's "What's patched" table for the custom_metadata patch; this file and the small
// change in ArrowStreamReaderImplementation.cs that throws it are additional to it.
//
// Licensed to the Apache Software Foundation (ASF) under one or more
// contributor license agreements. See the NOTICE file distributed with
// this work for additional information regarding copyright ownership.
// The ASF licenses this file to You under the Apache License, Version 2.0
// (the "License"); you may not use this file except in compliance with
// the License.  You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;

namespace Apache.Arrow.Ipc
{
    /// <summary>
    /// Thrown by <see cref="ArrowStreamReaderImplementation"/> when an incoming IPC message's
    /// declared body length exceeds <see cref="int.MaxValue"/> bytes — the ceiling every
    /// downstream buffer/array in this reader is bound by, since <c>AllocateMessageBodyBuffer</c>
    /// takes an <see langword="int"/>.
    ///
    /// The message's small, length-prefixed HEADER (metadata) has already been fully read from
    /// the stream by the time this is thrown; the body itself has not been touched. A caller that
    /// wants to keep the connection usable after refusing a message this large — rather than
    /// leaving the stream desynced with an unread body still sitting in it — can use
    /// <see cref="DeclaredBodyLength"/> to drain exactly that many bytes before replying.
    /// </summary>
    public sealed class ArrowIpcBodyTooLargeException : Exception
    {
        public long DeclaredBodyLength { get; }

        public ArrowIpcBodyTooLargeException(long declaredBodyLength)
            : base($"Message body length {declaredBodyLength} exceeds the maximum this reader can materialize ({int.MaxValue} bytes).")
        {
            DeclaredBodyLength = declaredBodyLength;
        }
    }
}
