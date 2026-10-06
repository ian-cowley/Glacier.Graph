namespace Glacier.Graph.Benchmarks;

using System;
using BenchmarkDotNet.Attributes;
using Glacier.Graph.Kernels;

[MemoryDiagnoser]
[ShortRunJob]
public class SimdCsrKernelBenchmarks
{
    private int[] _neighborArray = null!;
    private int[] _offsetsArray = null!;
    private int[] _degreesDestination = null!;
    private int[] _setA = null!;
    private int[] _setB = null!;
    private int _targetPresent;
    private int _targetMissing;

    [Params(16, 64, 256, 1024)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _neighborArray = new int[Size];
        for (int i = 0; i < Size; i++)
        {
            _neighborArray[i] = (i + 1) * 2;
        }

        _targetPresent = _neighborArray[Size / 2];
        _targetMissing = _neighborArray[^1] + 1;

        _offsetsArray = new int[Size + 2];
        int curr = 0;
        for (int i = 0; i < _offsetsArray.Length; i++)
        {
            _offsetsArray[i] = curr;
            curr += rng.Next(1, 10);
        }

        _degreesDestination = new int[Size];

        _setA = new int[Size];
        _setB = new int[Size];
        for (int i = 0; i < Size; i++)
        {
            _setA[i] = i * 2;
            _setB[i] = (i / 2) * 2;
        }
    }

    [Benchmark(Baseline = true)]
    public bool NeighborLookup_Scalar()
    {
        return CsrKernels.ContainsNeighborScalar(_neighborArray.AsSpan(), _targetPresent);
    }

    [Benchmark]
    public bool NeighborLookup_SIMD()
    {
        return CsrKernels.ContainsNeighbor(_neighborArray.AsSpan(), _targetPresent);
    }

    [Benchmark]
    public int NeighborIndexOf_SIMD()
    {
        return CsrKernels.IndexOfNeighbor(_neighborArray.AsSpan(), _targetPresent);
    }

    [Benchmark]
    public void DegreeComputation_Scalar()
    {
        CsrKernels.ComputeDegreesScalar(_offsetsArray.AsSpan(), 1, _degreesDestination.AsSpan());
    }

    [Benchmark]
    public void DegreeComputation_SIMD()
    {
        CsrKernels.ComputeDegrees(_offsetsArray.AsSpan(), 1, _degreesDestination.AsSpan());
    }

    [Benchmark]
    public int CommonNeighbors_Scalar()
    {
        return CsrKernels.CountCommonNeighborsScalar(_setA.AsSpan(), _setB.AsSpan());
    }

    [Benchmark]
    public int CommonNeighbors_SIMD()
    {
        return CsrKernels.CountCommonNeighbors(_setA.AsSpan(), _setB.AsSpan(), isSorted: false);
    }
}
