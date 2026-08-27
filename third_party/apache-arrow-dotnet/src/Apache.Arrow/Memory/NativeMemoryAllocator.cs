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
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Apache.Arrow.Memory
{
    public class NativeMemoryAllocator : MemoryAllocator
    {
        // GC.AddMemoryPressure is intended for significant unmanaged allocations. Calling it for
        // every tiny Arrow validity/value/offset buffer causes excessive full-GC scheduling in
        // high-rate RPC workloads. Small buffers are short-lived and deterministically disposed;
        // continue tracking large allocations so the GC remains informed about material native use.
        private const int MemoryPressureThreshold = 64 * 1024;
        private const int MaximumPooledAllocationBytes = MemoryPressureThreshold / 2;
        private const long MaximumPooledBytes = 32L * 1024 * 1024;

        internal static readonly INativeAllocationOwner ExclusiveOwner = new NativeAllocationOwner();
        private static readonly ConcurrentDictionary<int, PooledNativeAllocationOwner> s_owners = new();
        private static long s_pooledBytes;

        private readonly PooledNativeAllocationOwner _owner;

        public NativeMemoryAllocator(int alignment = DefaultAlignment)
            : base(alignment)
        {
            _owner = s_owners.GetOrAdd(alignment, static value => new PooledNativeAllocationOwner(value));
        }

        protected override IMemoryOwner<byte> AllocateInternal(int length, out int bytesAllocated)
        {
            int size = AllocationSize(length, Alignment);
            IntPtr ptr = _owner.Rent(size);
            if (ptr == IntPtr.Zero)
            {
                ptr = Marshal.AllocHGlobal(size);
                if (size >= MemoryPressureThreshold)
                {
                    GC.AddMemoryPressure(size);
                }
            }

            int offset = (int)(Alignment - (ptr.ToInt64() & (Alignment - 1)));
            NativeMemoryManager manager;
            try
            {
                manager = new NativeMemoryManager(_owner, ptr, offset, length);
            }
            catch
            {
                _owner.Release(ptr, offset, length);
                throw;
            }

            bytesAllocated = size;
            try
            {
                // Arrow builders assume newly allocated validity/value buffers are zero-initialized.
                manager.Memory.Span.Clear();
                return manager;
            }
            catch
            {
                ((IDisposable)manager).Dispose();
                throw;
            }
        }

        private static int AllocationSize(int length, int alignment)
        {
            int requested = checked(length + alignment);
            if (requested > MaximumPooledAllocationBytes)
            {
                return requested;
            }

            int bucket = 128;
            while (bucket < requested)
            {
                bucket <<= 1;
            }

            return bucket;
        }

        private sealed class PooledNativeAllocationOwner(int alignment) : INativeAllocationOwner
        {
            private readonly ConcurrentDictionary<int, ConcurrentStack<IntPtr>> _pools = new();

            public IntPtr Rent(int size)
            {
                if (size <= MaximumPooledAllocationBytes
                    && _pools.TryGetValue(size, out var pool)
                    && pool.TryPop(out var ptr))
                {
                    Interlocked.Add(ref s_pooledBytes, -size);
                    return ptr;
                }

                return IntPtr.Zero;
            }

            public void Release(IntPtr ptr, int offset, int length)
            {
                int size = AllocationSize(length, alignment);
                if (size <= MaximumPooledAllocationBytes)
                {
                    long pooledBytes = Interlocked.Add(ref s_pooledBytes, size);
                    if (pooledBytes <= MaximumPooledBytes)
                    {
                        _pools.GetOrAdd(size, static _ => new ConcurrentStack<IntPtr>()).Push(ptr);
                        return;
                    }

                    Interlocked.Add(ref s_pooledBytes, -size);
                }

                Marshal.FreeHGlobal(ptr);
                if (size >= MemoryPressureThreshold)
                {
                    GC.RemoveMemoryPressure(size);
                }
            }
        }

        private sealed class NativeAllocationOwner : INativeAllocationOwner
        {
            public void Release(IntPtr ptr, int offset, int length)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }
}
