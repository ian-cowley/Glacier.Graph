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

    // =========================================================================
    // 4. HIPPORAG SPARSE CSR SPMV PERSONALIZED PAGERANK
    // =========================================================================

    /// <summary>
    /// Executes hardware-accelerated (Vector512 / AVX-512) Sparse Matrix-Vector (SpMV)
    /// Personalized PageRank (HippoRAG) over the CSR graph topology.
    /// Eliminates pairwise BFS loops with an O(k * |E|) power iteration pass.
    /// </summary>
    /// <param name="graph">The CSR graph store containing contiguous row offsets, column indices, and edge weights.</param>
    /// <param name="seedEntities">The 1-based internal IDs of the seed entities.</param>
    /// <param name="seedWeights">Optional initial probability weights for the seed entities (uniform if empty).</param>
    /// <param name="pprScoresOut">Destination span of length at least graph.NodeCount + 1 to receive stationary PPR scores.</param>
    /// <param name="alpha">Damping/continuation probability factor (typically 0.85f).</param>
    /// <param name="maxIterations">Maximum number of power iteration steps (typically 20).</param>
    /// <param name="tolerance">L1 norm convergence threshold (typically 1e-6f).</param>
    public static void PersonalizedPageRank(
        Glacier.Graph.Storage.CsrGraphStore graph,
        ReadOnlySpan<int> seedEntities,
        ReadOnlySpan<float> seedWeights,
        Span<float> pprScoresOut,
        float alpha = 0.85f,
        int maxIterations = 20,
        float tolerance = 1e-6f)
    {
        ArgumentNullException.ThrowIfNull(graph);

        ReadOnlySpan<int> rowOffsets = graph.GetRowOffsetsSpan();
        ReadOnlySpan<int> columnIndices = graph.GetColumnIndicesSpan();
        ReadOnlySpan<float> edgeWeights = graph.GetEdgeWeightsSpan();

        if (!rowOffsets.IsEmpty && !columnIndices.IsEmpty)
        {
            PersonalizedPageRank(
                rowOffsets,
                columnIndices,
                edgeWeights,
                graph.NodeCount,
                seedEntities,
                seedWeights,
                pprScoresOut,
                alpha,
                maxIterations,
                tolerance);
            return;
        }

        // Fallback for non-memory-mapped access
        int nodeCount = graph.NodeCount;
        if (nodeCount <= 0) return;
        if (pprScoresOut.Length < nodeCount + 1)
            throw new ArgumentException($"pprScoresOut length ({pprScoresOut.Length}) must be >= NodeCount + 1 ({nodeCount + 1}).");

        int[] dynamicRowOffsets = new int[nodeCount + 2];
        System.Collections.Generic.List<int> dynamicColIndices = new(graph.EdgeCount);
        System.Collections.Generic.List<float> dynamicWeights = new(graph.EdgeCount);

        for (int u = 1; u <= nodeCount; u++)
        {
            dynamicRowOffsets[u] = dynamicColIndices.Count;
            if (graph.TryGetNeighborsAndWeights(u, out var nbrs, out var wts))
            {
                for (int e = 0; e < nbrs.Length; e++)
                {
                    dynamicColIndices.Add(nbrs[e]);
                    dynamicWeights.Add(e < wts.Length ? wts[e] : 1.0f);
                }
            }
            else if (graph.TryGetNeighbors(u, out var nbrsOnly))
            {
                for (int e = 0; e < nbrsOnly.Length; e++)
                {
                    dynamicColIndices.Add(nbrsOnly[e]);
                    dynamicWeights.Add(1.0f);
                }
            }
        }
        dynamicRowOffsets[nodeCount + 1] = dynamicColIndices.Count;

        PersonalizedPageRank(
            dynamicRowOffsets,
            CollectionsMarshal.AsSpan(dynamicColIndices),
            CollectionsMarshal.AsSpan(dynamicWeights),
            nodeCount,
            seedEntities,
            seedWeights,
            pprScoresOut,
            alpha,
            maxIterations,
            tolerance);
    }

    /// <summary>
    /// Primitive span-based overload for CSR data structures in memory.
    /// </summary>
    public static void PersonalizedPageRank(
        ReadOnlySpan<int> rowOffsets,
        ReadOnlySpan<int> columnIndices,
        ReadOnlySpan<float> edgeWeights,
        int nodeCount,
        ReadOnlySpan<int> seedEntities,
        ReadOnlySpan<float> seedWeights,
        Span<float> pprScoresOut,
        float alpha = 0.85f,
        int maxIterations = 20,
        float tolerance = 1e-6f)
    {
        if (nodeCount <= 0) return;
        if (rowOffsets.Length < nodeCount + 2)
            throw new ArgumentException("rowOffsets length must be >= nodeCount + 2.");
        if (pprScoresOut.Length < nodeCount + 1)
            throw new ArgumentException("pprScoresOut length must be >= nodeCount + 1.");

        pprScoresOut.Clear();

        float[]? rentedP0 = null;
        float[]? rentedPCurr = null;
        float[]? rentedPNext = null;
        float[]? rentedInvOut = null;
        bool[]? rentedDangling = null;

        int bufSize = nodeCount + 1;
        Span<float> p0 = bufSize <= 1024 ? stackalloc float[bufSize] : (rentedP0 = System.Buffers.ArrayPool<float>.Shared.Rent(bufSize)).AsSpan(0, bufSize);
        Span<float> pCurr = bufSize <= 1024 ? stackalloc float[bufSize] : (rentedPCurr = System.Buffers.ArrayPool<float>.Shared.Rent(bufSize)).AsSpan(0, bufSize);
        Span<float> pNext = bufSize <= 1024 ? stackalloc float[bufSize] : (rentedPNext = System.Buffers.ArrayPool<float>.Shared.Rent(bufSize)).AsSpan(0, bufSize);
        Span<float> invOut = bufSize <= 1024 ? stackalloc float[bufSize] : (rentedInvOut = System.Buffers.ArrayPool<float>.Shared.Rent(bufSize)).AsSpan(0, bufSize);
        Span<bool> isDangling = bufSize <= 1024 ? stackalloc bool[bufSize] : (rentedDangling = new bool[bufSize]).AsSpan(0, bufSize);

        try
        {
            p0.Clear();
            invOut.Clear();
            isDangling.Clear();

            // 1. Initialize personalization vector p0
            float totalSeedWeight = 0f;

            for (int s = 0; s < seedEntities.Length; s++)
            {
                int u = seedEntities[s];
                if (u >= 1 && u <= nodeCount)
                {
                    float w = (s < seedWeights.Length && seedWeights[s] > 0f) ? seedWeights[s] : 1.0f;
                    p0[u] += w;
                    totalSeedWeight += w;
                }
            }

            if (totalSeedWeight > 0f)
            {
                float invSum = 1.0f / totalSeedWeight;
                for (int u = 1; u <= nodeCount; u++)
                {
                    if (p0[u] > 0f)
                    {
                        p0[u] *= invSum;
                    }
                }
            }
            else
            {
                float uniform = 1.0f / nodeCount;
                for (int u = 1; u <= nodeCount; u++)
                {
                    p0[u] = uniform;
                }
            }

            p0.CopyTo(pCurr);

            // 2. Precompute out-degrees and inverse weights
            bool hasWeights = !edgeWeights.IsEmpty;
            for (int u = 1; u <= nodeCount; u++)
            {
                int start = rowOffsets[u];
                int end = rowOffsets[u + 1];
                int degree = end - start;

                if (degree <= 0)
                {
                    isDangling[u] = true;
                    invOut[u] = 0f;
                }
                else if (!hasWeights)
                {
                    invOut[u] = 1.0f / degree;
                }
                else
                {
                    float wSum = 0f;
                    for (int k = start; k < end; k++)
                    {
                        wSum += edgeWeights[k];
                    }
                    if (wSum > 0f)
                    {
                        invOut[u] = 1.0f / wSum;
                    }
                    else
                    {
                        isDangling[u] = true;
                        invOut[u] = 0f;
                    }
                }
            }

            // 3. Power Iteration with Dangling Node Redistribution
            ref float p0Ref = ref MemoryMarshal.GetReference(p0);
            ref float pCurrRef = ref MemoryMarshal.GetReference(pCurr);
            ref float pNextRef = ref MemoryMarshal.GetReference(pNext);

            for (int iter = 0; iter < maxIterations; iter++)
            {
                // A. Compute trapped dangling mass
                float mDangle = 0f;
                for (int u = 1; u <= nodeCount; u++)
                {
                    if (isDangling[u])
                    {
                        mDangle += pCurr[u];
                    }
                }

                float sDangle = (1.0f - alpha) + (alpha * mDangle);

                // B. Initialize pNext with teleport contribution S_dangle * p0 (Vectorized)
                int i = 1;
                if (Vector512.IsHardwareAccelerated && (nodeCount - i + 1) >= Vector512<float>.Count)
                {
                    var vS = Vector512.Create(sDangle);
                    int limit = nodeCount - Vector512<float>.Count + 1;
                    for (; i <= limit; i += Vector512<float>.Count)
                    {
                        var vP0 = Vector512.LoadUnsafe(ref p0Ref, (nuint)i);
                        (vS * vP0).StoreUnsafe(ref pNextRef, (nuint)i);
                    }
                }
                if (Vector256.IsHardwareAccelerated && (nodeCount - i + 1) >= Vector256<float>.Count)
                {
                    var vS = Vector256.Create(sDangle);
                    int limit = nodeCount - Vector256<float>.Count + 1;
                    for (; i <= limit; i += Vector256<float>.Count)
                    {
                        var vP0 = Vector256.LoadUnsafe(ref p0Ref, (nuint)i);
                        (vS * vP0).StoreUnsafe(ref pNextRef, (nuint)i);
                    }
                }
                for (; i <= nodeCount; i++)
                {
                    pNext[i] = sDangle * p0[i];
                }

                // C. Sparse CSR SpMV transition
                for (int u = 1; u <= nodeCount; u++)
                {
                    if (isDangling[u]) continue;

                    float pVal = pCurr[u];
                    if (pVal == 0f) continue;

                    float coeff = alpha * pVal * invOut[u];
                    int start = rowOffsets[u];
                    int end = rowOffsets[u + 1];

                    if (!hasWeights)
                    {
                        for (int k = start; k < end; k++)
                        {
                            int v = columnIndices[k];
                            pNext[v] += coeff;
                        }
                    }
                    else
                    {
                        for (int k = start; k < end; k++)
                        {
                            int v = columnIndices[k];
                            pNext[v] += coeff * edgeWeights[k];
                        }
                    }
                }

                // D. Check L1 convergence (Vectorized)
                float delta = 0f;
                int j = 1;
                if (Vector512.IsHardwareAccelerated && (nodeCount - j + 1) >= Vector512<float>.Count)
                {
                    var vDelta = Vector512<float>.Zero;
                    int limit = nodeCount - Vector512<float>.Count + 1;
                    for (; j <= limit; j += Vector512<float>.Count)
                    {
                        var vNext = Vector512.LoadUnsafe(ref pNextRef, (nuint)j);
                        var vCurr = Vector512.LoadUnsafe(ref pCurrRef, (nuint)j);
                        vDelta += Vector512.Abs(vNext - vCurr);
                    }
                    delta += Vector512.Sum(vDelta);
                }
                if (Vector256.IsHardwareAccelerated && (nodeCount - j + 1) >= Vector256<float>.Count)
                {
                    var vDelta = Vector256<float>.Zero;
                    int limit = nodeCount - Vector256<float>.Count + 1;
                    for (; j <= limit; j += Vector256<float>.Count)
                    {
                        var vNext = Vector256.LoadUnsafe(ref pNextRef, (nuint)j);
                        var vCurr = Vector256.LoadUnsafe(ref pCurrRef, (nuint)j);
                        vDelta += Vector256.Abs(vNext - vCurr);
                    }
                    delta += Vector256.Sum(vDelta);
                }
                for (; j <= nodeCount; j++)
                {
                    delta += MathF.Abs(pNext[j] - pCurr[j]);
                }

                // Swap buffers for next iteration
                pNext.CopyTo(pCurr);

                if (delta < tolerance)
                {
                    break;
                }
            }

            pCurr.Slice(0, nodeCount + 1).CopyTo(pprScoresOut);
        }
        finally
        {
            if (rentedP0 != null) System.Buffers.ArrayPool<float>.Shared.Return(rentedP0);
            if (rentedPCurr != null) System.Buffers.ArrayPool<float>.Shared.Return(rentedPCurr);
            if (rentedPNext != null) System.Buffers.ArrayPool<float>.Shared.Return(rentedPNext);
            if (rentedInvOut != null) System.Buffers.ArrayPool<float>.Shared.Return(rentedInvOut);
        }
    }
}
