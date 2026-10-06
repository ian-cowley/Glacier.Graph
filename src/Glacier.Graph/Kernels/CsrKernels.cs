namespace Glacier.Graph.Kernels;

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

/// <summary>
/// Hardware-accelerated SIMD (Vector512 / Vector256 / Vector128 / AVX-512) kernels
/// for high-throughput sparse CSR (Compressed Sparse Row) and Forward Star graph traversals.
/// Provides vectorized neighbor searching, node degree computation, and common neighbor intersection.
/// </summary>
public static class CsrKernels
{
    // =========================================================================
    // 1. NEIGHBOR MEMBERSHIP & SCAN KERNELS
    // =========================================================================

    /// <summary>
    /// Checks whether the specified contiguous neighbor array contains the target neighbor ID
    /// using AVX-512 (Vector512), AVX2 (Vector256), or SSE/Neon (Vector128) hardware intrinsics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsNeighbor(ReadOnlySpan<int> neighbors, int targetNeighborId)
    {
        int length = neighbors.Length;
        if (length == 0) return false;

        ref int baseRef = ref MemoryMarshal.GetReference(neighbors);
        int i = 0;

        // Vector512 Kernel (16 integers / 64 bytes per iteration)
        if (Vector512.IsHardwareAccelerated && length >= Vector512<int>.Count)
        {
            var targetVec = Vector512.Create(targetNeighborId);
            int v512Limit = length - Vector512<int>.Count;
            for (; i <= v512Limit; i += Vector512<int>.Count)
            {
                var vec = Vector512.LoadUnsafe(ref baseRef, (nuint)i);
                var eq = Vector512.Equals(vec, targetVec);
                if (Vector512.ExtractMostSignificantBits(eq) != 0) return true;
            }
        }

        // Vector256 Kernel (8 integers / 32 bytes per iteration)
        if (Vector256.IsHardwareAccelerated && (length - i) >= Vector256<int>.Count)
        {
            var targetVec = Vector256.Create(targetNeighborId);
            int v256Limit = length - Vector256<int>.Count;
            for (; i <= v256Limit; i += Vector256<int>.Count)
            {
                var vec = Vector256.LoadUnsafe(ref baseRef, (nuint)i);
                var eq = Vector256.Equals(vec, targetVec);
                if (Vector256.ExtractMostSignificantBits(eq) != 0) return true;
            }
        }

        // Vector128 Kernel (4 integers / 16 bytes per iteration)
        if (Vector128.IsHardwareAccelerated && (length - i) >= Vector128<int>.Count)
        {
            var targetVec = Vector128.Create(targetNeighborId);
            int v128Limit = length - Vector128<int>.Count;
            for (; i <= v128Limit; i += Vector128<int>.Count)
            {
                var vec = Vector128.LoadUnsafe(ref baseRef, (nuint)i);
                var eq = Vector128.Equals(vec, targetVec);
                if (Vector128.ExtractMostSignificantBits(eq) != 0) return true;
            }
        }

        // Scalar cleanup tail
        for (; i < length; i++)
        {
            if (Unsafe.Add(ref baseRef, i) == targetNeighborId) return true;
        }

        return false;
    }

    /// <summary>
    /// Raw unmanaged pointer overload for checking neighbor membership using SIMD intrinsics.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe bool ContainsNeighbor(int* pNeighbors, int count, int targetNeighborId)
    {
        if (pNeighbors == null || count <= 0) return false;
        return ContainsNeighbor(new ReadOnlySpan<int>(pNeighbors, count), targetNeighborId);
    }

    /// <summary>
    /// Finds the 0-based index of the target neighbor within the neighbor array, or -1 if not found.
    /// Uses SIMD comparison combined with trailing bit count for sub-nanosecond index extraction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryFindNeighborIndex(ReadOnlySpan<int> neighbors, int targetNeighborId, out int index)
    {
        index = IndexOfNeighbor(neighbors, targetNeighborId);
        return index >= 0;
    }

    /// <summary>
    /// Returns the 0-based index of the target neighbor within the neighbor array, or -1 if not found.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IndexOfNeighbor(ReadOnlySpan<int> neighbors, int targetNeighborId)
    {
        int length = neighbors.Length;
        if (length == 0) return -1;

        ref int baseRef = ref MemoryMarshal.GetReference(neighbors);
        int i = 0;

        // Vector512 Kernel
        if (Vector512.IsHardwareAccelerated && length >= Vector512<int>.Count)
        {
            var targetVec = Vector512.Create(targetNeighborId);
            int v512Limit = length - Vector512<int>.Count;
            for (; i <= v512Limit; i += Vector512<int>.Count)
            {
                var vec = Vector512.LoadUnsafe(ref baseRef, (nuint)i);
                var eq = Vector512.Equals(vec, targetVec);
                ulong mask = Vector512.ExtractMostSignificantBits(eq);
                if (mask != 0)
                {
                    return i + BitOperations.TrailingZeroCount(mask);
                }
            }
        }

        // Vector256 Kernel
        if (Vector256.IsHardwareAccelerated && (length - i) >= Vector256<int>.Count)
        {
            var targetVec = Vector256.Create(targetNeighborId);
            int v256Limit = length - Vector256<int>.Count;
            for (; i <= v256Limit; i += Vector256<int>.Count)
            {
                var vec = Vector256.LoadUnsafe(ref baseRef, (nuint)i);
                var eq = Vector256.Equals(vec, targetVec);
                uint mask = Vector256.ExtractMostSignificantBits(eq);
                if (mask != 0)
                {
                    return i + BitOperations.TrailingZeroCount(mask);
                }
            }
        }

        // Vector128 Kernel
        if (Vector128.IsHardwareAccelerated && (length - i) >= Vector128<int>.Count)
        {
            var targetVec = Vector128.Create(targetNeighborId);
            int v128Limit = length - Vector128<int>.Count;
            for (; i <= v128Limit; i += Vector128<int>.Count)
            {
                var vec = Vector128.LoadUnsafe(ref baseRef, (nuint)i);
                var eq = Vector128.Equals(vec, targetVec);
                uint mask = Vector128.ExtractMostSignificantBits(eq);
                if (mask != 0)
                {
                    return i + BitOperations.TrailingZeroCount(mask);
                }
            }
        }

        // Scalar cleanup tail
        for (; i < length; i++)
        {
            if (Unsafe.Add(ref baseRef, i) == targetNeighborId) return i;
        }

        return -1;
    }

    /// <summary>
    /// Pure scalar baseline for neighbor membership testing (used for microbenchmark comparisons).
    /// </summary>
    public static bool ContainsNeighborScalar(ReadOnlySpan<int> neighbors, int targetNeighborId)
    {
        for (int i = 0; i < neighbors.Length; i++)
        {
            if (neighbors[i] == targetNeighborId) return true;
        }
        return false;
    }

    // =========================================================================
    // 2. VECTORIZED DEGREE COMPUTATION KERNELS
    // =========================================================================

    /// <summary>
    /// Vectorized node degree computation from CSR row offsets:
    /// <c>degree[i] = rowOffsets[startNode + i + 1] - rowOffsets[startNode + i]</c>.
    /// Computes consecutive node degrees into <paramref name="destination"/> using Vector512 / Vector256 subtraction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ComputeDegrees(ReadOnlySpan<int> rowOffsets, int startNode, Span<int> destination)
    {
        int count = destination.Length;
        if (count == 0) return;

        ArgumentOutOfRangeException.ThrowIfLessThan(startNode, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startNode + count + 1, rowOffsets.Length);

        ref int offsetsRef = ref MemoryMarshal.GetReference(rowOffsets);
        ref int destRef = ref MemoryMarshal.GetReference(destination);
        int i = 0;

        // Vector512 SIMD Subtraction (16 nodes per step)
        if (Vector512.IsHardwareAccelerated && count >= Vector512<int>.Count)
        {
            int v512Limit = count - Vector512<int>.Count;
            for (; i <= v512Limit; i += Vector512<int>.Count)
            {
                var nextVec = Vector512.LoadUnsafe(ref offsetsRef, (nuint)(startNode + i + 1));
                var currVec = Vector512.LoadUnsafe(ref offsetsRef, (nuint)(startNode + i));
                var degVec = Vector512.Subtract(nextVec, currVec);
                degVec.StoreUnsafe(ref destRef, (nuint)i);
            }
        }

        // Vector256 SIMD Subtraction (8 nodes per step)
        if (Vector256.IsHardwareAccelerated && (count - i) >= Vector256<int>.Count)
        {
            int v256Limit = count - Vector256<int>.Count;
            for (; i <= v256Limit; i += Vector256<int>.Count)
            {
                var nextVec = Vector256.LoadUnsafe(ref offsetsRef, (nuint)(startNode + i + 1));
                var currVec = Vector256.LoadUnsafe(ref offsetsRef, (nuint)(startNode + i));
                var degVec = Vector256.Subtract(nextVec, currVec);
                degVec.StoreUnsafe(ref destRef, (nuint)i);
            }
        }

        // Vector128 SIMD Subtraction (4 nodes per step)
        if (Vector128.IsHardwareAccelerated && (count - i) >= Vector128<int>.Count)
        {
            int v128Limit = count - Vector128<int>.Count;
            for (; i <= v128Limit; i += Vector128<int>.Count)
            {
                var nextVec = Vector128.LoadUnsafe(ref offsetsRef, (nuint)(startNode + i + 1));
                var currVec = Vector128.LoadUnsafe(ref offsetsRef, (nuint)(startNode + i));
                var degVec = Vector128.Subtract(nextVec, currVec);
                degVec.StoreUnsafe(ref destRef, (nuint)i);
            }
        }

        // Scalar cleanup tail
        for (; i < count; i++)
        {
            int nextOffset = Unsafe.Add(ref offsetsRef, startNode + i + 1);
            int currOffset = Unsafe.Add(ref offsetsRef, startNode + i);
            Unsafe.Add(ref destRef, i) = nextOffset - currOffset;
        }
    }

    /// <summary>
    /// Pure scalar baseline for degree computation (used for microbenchmark comparisons).
    /// </summary>
    public static void ComputeDegreesScalar(ReadOnlySpan<int> rowOffsets, int startNode, Span<int> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = rowOffsets[startNode + i + 1] - rowOffsets[startNode + i];
        }
    }

    // =========================================================================
    // 3. COMMON NEIGHBOR INTERSECTION KERNELS
    // =========================================================================

    /// <summary>
    /// Counts the number of common neighbors shared between two nodes.
    /// Uses SIMD membership testing for unordered adjacency or fast two-pointer intersection if sorted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CountCommonNeighbors(ReadOnlySpan<int> a, ReadOnlySpan<int> b, bool isSorted = false)
    {
        if (a.Length == 0 || b.Length == 0) return 0;

        if (isSorted)
        {
            // Fast two-pointer intersection
            int i = 0, j = 0, count = 0;
            while (i < a.Length && j < b.Length)
            {
                int valA = a[i];
                int valB = b[j];

                if (valA == valB)
                {
                    count++;
                    i++;
                    j++;
                }
                else if (valA < valB)
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }

            return count;
        }

        // Unsorted: iterate over the smaller span and probe against the larger span using SIMD
        ReadOnlySpan<int> smaller = a.Length <= b.Length ? a : b;
        ReadOnlySpan<int> larger = a.Length <= b.Length ? b : a;

        int commonCount = 0;
        for (int i = 0; i < smaller.Length; i++)
        {
            if (ContainsNeighbor(larger, smaller[i]))
            {
                commonCount++;
            }
        }

        return commonCount;
    }

    /// <summary>
    /// Intersects the common neighbors of two nodes and writes the resulting node IDs into <paramref name="destination"/>.
    /// Returns the number of common neighbors written.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int IntersectCommonNeighbors(ReadOnlySpan<int> a, ReadOnlySpan<int> b, Span<int> destination, bool isSorted = false)
    {
        if (a.Length == 0 || b.Length == 0 || destination.Length == 0) return 0;

        int written = 0;

        if (isSorted)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length && written < destination.Length)
            {
                int valA = a[i];
                int valB = b[j];

                if (valA == valB)
                {
                    destination[written++] = valA;
                    i++;
                    j++;
                }
                else if (valA < valB)
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }

            return written;
        }

        ReadOnlySpan<int> smaller = a.Length <= b.Length ? a : b;
        ReadOnlySpan<int> larger = a.Length <= b.Length ? b : a;

        for (int i = 0; i < smaller.Length && written < destination.Length; i++)
        {
            int val = smaller[i];
            if (ContainsNeighbor(larger, val))
            {
                destination[written++] = val;
            }
        }

        return written;
    }

    /// <summary>
    /// Pure scalar baseline for common neighbor counting (used for microbenchmark comparisons).
    /// </summary>
    public static int CountCommonNeighborsScalar(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;

        int count = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int target = a[i];
            for (int j = 0; j < b.Length; j++)
            {
                if (b[j] == target)
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }
}
