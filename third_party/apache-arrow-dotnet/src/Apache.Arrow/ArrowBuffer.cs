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
using System.Buffers;
using System.Runtime.CompilerServices;
using Apache.Arrow.Memory;

namespace Apache.Arrow
{
    public readonly partial struct ArrowBuffer : IEquatable<ArrowBuffer>, IDisposable
    {
        private readonly SharedMemoryHandle _handle;
        private readonly ReadOnlyMemory<byte> _memory;

        public static ArrowBuffer Empty => new ArrowBuffer(Memory<byte>.Empty);

        public ArrowBuffer(ReadOnlyMemory<byte> data)
        {
            _handle = null;
            _memory = data;
        }

        internal ArrowBuffer(IMemoryOwner<byte> memoryOwner)
        {
            _handle = new SharedMemoryHandle(new SharedMemoryOwner(memoryOwner));
            _memory = _handle.Memory;
        }

        private ArrowBuffer(SharedMemoryHandle handle, ReadOnlyMemory<byte> memory)
        {
            _handle = handle;
            _memory = memory;
        }

        public ReadOnlyMemory<byte> Memory => _memory;

        public bool IsEmpty => Memory.IsEmpty;

        public int Length => Memory.Length;

        public ReadOnlySpan<byte> Span
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Memory.Span;
        }

        /// <summary>
        /// Adds another reference to the memory used by this buffer. Allows a single buffer
        /// to be shared by multiple arrays.
        /// </summary>
        public ArrowBuffer Retain()
        {
            if (_handle != null)
            {
                return new ArrowBuffer(_handle.Retain(), _memory);
            }

            return new ArrowBuffer(_memory);
        }

        /// <summary>
        /// Returns an independently-owned view over a range of this buffer. The returned view
        /// keeps pooled/native storage alive even after the source array or record batch is
        /// disposed.
        /// </summary>
        public ArrowBuffer Slice(int start, int length)
        {
            return Share(_memory.Slice(start, length));
        }

        /// <summary>
        /// Creates another owner for a memory view known to refer to this buffer's allocation.
        /// </summary>
        internal ArrowBuffer Share(ReadOnlyMemory<byte> memory)
        {
            return _handle != null
                ? new ArrowBuffer(_handle.Retain(), memory)
                : new ArrowBuffer(memory);
        }

        public ArrowBuffer Clone(MemoryAllocator allocator = default)
        {
            return Span.Length == 0 ? Empty : new Builder<byte>(Span.Length)
                .Append(Span)
                .Build(allocator);
        }

        public bool Equals(ArrowBuffer other)
        {
            return Span.SequenceEqual(other.Span);
        }

        public void Dispose()
        {
            _handle?.Dispose();
        }

        internal bool TryExport(ExportedAllocationOwner newOwner, out IntPtr ptr)
        {
            if (IsEmpty)
            {
                ptr = IntPtr.Zero;
                return true;
            }

            if (_handle != null)
            {
                ptr = newOwner.Acquire(_handle.Retain());
                return true;
            }

            var pinHandle = _memory.Pin();
            ptr = newOwner.Reference(pinHandle);
            return true;
        }
    }
}
