namespace Glacier.Graph.Tests;

using System;
using System.Linq;
using Glacier.Graph.Kernels;
using Xunit;

public class CsrSimdKernelTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(45)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void ContainsNeighbor_AllSizes_MatchesScalarOracle(int size)
    {
        var array = new int[size];
        for (int i = 0; i < size; i++)
        {
            array[i] = (i + 1) * 3; // 3, 6, 9, 12, ...
        }

        ReadOnlySpan<int> span = array.AsSpan();

        if (size > 0)
        {
            // Test first element
            Assert.True(CsrKernels.ContainsNeighbor(span, array[0]));
            Assert.Equal(CsrKernels.ContainsNeighborScalar(span, array[0]), CsrKernels.ContainsNeighbor(span, array[0]));

            // Test last element
            Assert.True(CsrKernels.ContainsNeighbor(span, array[size - 1]));
            Assert.Equal(CsrKernels.ContainsNeighborScalar(span, array[size - 1]), CsrKernels.ContainsNeighbor(span, array[size - 1]));

            // Test middle element
            int mid = size / 2;
            Assert.True(CsrKernels.ContainsNeighbor(span, array[mid]));
            Assert.Equal(CsrKernels.ContainsNeighborScalar(span, array[mid]), CsrKernels.ContainsNeighbor(span, array[mid]));

            // Test pointer overload
            unsafe
            {
                fixed (int* p = array)
                {
                    Assert.True(CsrKernels.ContainsNeighbor(p, size, array[mid]));
                    Assert.False(CsrKernels.ContainsNeighbor(p, size, -999));
                }
            }
        }

        // Test non-existent targets
        Assert.False(CsrKernels.ContainsNeighbor(span, -1));
        Assert.False(CsrKernels.ContainsNeighbor(span, 999_999));
        Assert.False(CsrKernels.ContainsNeighbor(span, 1)); // between values (not divisible by 3)
    }

    [Fact]
    public void IndexOfNeighbor_ReturnsAccurateLaneIndices()
    {
        int[] data = Enumerable.Range(0, 100).Select(x => x * 10).ToArray();
        ReadOnlySpan<int> span = data.AsSpan();

        for (int i = 0; i < data.Length; i++)
        {
            int target = data[i];
            int index = CsrKernels.IndexOfNeighbor(span, target);
            Assert.Equal(i, index);

            bool found = CsrKernels.TryFindNeighborIndex(span, target, out int outIdx);
            Assert.True(found);
            Assert.Equal(i, outIdx);
        }

        Assert.Equal(-1, CsrKernels.IndexOfNeighbor(span, -5));
        Assert.Equal(-1, CsrKernels.IndexOfNeighbor(span, 1005));
        Assert.False(CsrKernels.TryFindNeighborIndex(span, 15, out _));
    }

    [Fact]
    public void ComputeDegrees_Vectorized_MatchesScalarReference()
    {
        const int nodeCount = 120;
        var rowOffsets = new int[nodeCount + 2];
        var rng = new Random(42);

        int currentOffset = 0;
        for (int i = 0; i < rowOffsets.Length; i++)
        {
            rowOffsets[i] = currentOffset;
            currentOffset += rng.Next(0, 15);
        }

        var degreesActual = new int[nodeCount];
        var degreesExpected = new int[nodeCount];

        CsrKernels.ComputeDegrees(rowOffsets, 1, degreesActual);
        CsrKernels.ComputeDegreesScalar(rowOffsets, 1, degreesExpected);

        Assert.Equal(degreesExpected, degreesActual);
    }

    [Fact]
    public void CountCommonNeighbors_MatchesScalarIntersection()
    {
        int[] a = [2, 5, 8, 11, 14, 17, 20, 23, 26, 29, 32, 35, 38, 41, 44, 47];
        int[] b = [5, 14, 23, 32, 41, 50, 59];

        // Sorted mode
        int commonSorted = CsrKernels.CountCommonNeighbors(a, b, isSorted: true);
        int expectedSorted = a.Intersect(b).Count();
        Assert.Equal(expectedSorted, commonSorted);

        // Unsorted mode
        var aShuffled = a.OrderBy(_ => Guid.NewGuid()).ToArray();
        var bShuffled = b.OrderBy(_ => Guid.NewGuid()).ToArray();

        int commonUnsorted = CsrKernels.CountCommonNeighbors(aShuffled, bShuffled, isSorted: false);
        int scalarUnsorted = CsrKernels.CountCommonNeighborsScalar(aShuffled, bShuffled);
        Assert.Equal(expectedSorted, commonUnsorted);
        Assert.Equal(scalarUnsorted, commonUnsorted);
    }

    [Fact]
    public void IntersectCommonNeighbors_FillsDestinationCorrectly()
    {
        int[] a = [1, 3, 5, 7, 9, 11, 13, 15, 17, 19];
        int[] b = [3, 7, 11, 15, 19, 23];

        Span<int> dest = stackalloc int[10];
        int count = CsrKernels.IntersectCommonNeighbors(a, b, dest, isSorted: true);

        Assert.Equal(5, count);
        var expected = new[] { 3, 7, 11, 15, 19 };
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(expected[i], dest[i]);
        }
    }
}
