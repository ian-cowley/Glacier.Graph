namespace Glacier.Graph.Benchmarks;

using System;
using System.Diagnostics;
using System.IO;
using BenchmarkDotNet.Running;
using Glacier.Graph.Kernels;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--bdn", StringComparison.OrdinalIgnoreCase))
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
            return;
        }

        Console.WriteLine("================================================================================");
        Console.WriteLine("          GLACIER.GRAPH MICROBENCHMARK & VERIFICATION SUITE                     ");
        Console.WriteLine("================================================================================");

        RunCsrTraversalBenchmarks();
        RunSimdKernelBenchmarks();

        Console.WriteLine("\n================================================================================");
        Console.WriteLine("          ALL MICROBENCHMARKS COMPLETED SUCCESSFULLY                            ");
        Console.WriteLine("================================================================================");
    }

    private static void RunCsrTraversalBenchmarks()
    {
        Console.WriteLine("\n[1] CSR Graph Traversals: Allocating Baseline vs Zero-Allocation Workspace:");

        const int nodeCount = 50_000;
        const int edgeCount = 250_000;
        var store = new GraphStore(initialNodeCapacity: nodeCount + 100, initialEdgeCapacity: edgeCount + 100);
        var rng = new Random(42);

        for (int i = 1; i <= nodeCount; i++)
        {
            store.AddNode($"Node_{i}");
        }

        for (int i = 1; i < nodeCount; i++)
        {
            store.AddEdge($"Node_{i}", $"Node_{i + 1}", "NEXT", 1.0f);
            if (i % 3 == 0 && i + 5 <= nodeCount)
            {
                store.AddEdge($"Node_{i}", $"Node_{i + 5}", "JUMP", 0.5f);
            }
            if (i % 10 == 0 && i + 50 <= nodeCount)
            {
                store.AddEdge($"Node_{i}", $"Node_{i + 50}", "HIGHWAY", 0.1f);
            }
        }

        string tempCsr = Path.Combine(Path.GetTempPath(), $"glacier_bench_graph_{Guid.NewGuid():N}.gcsr");
        try
        {
            CsrGraphCompiler.CompileFromForwardStar(store, tempCsr);
            using var csrStore = new CsrGraphStore(tempCsr, useMemoryMapping: true);

            var search = new GraphSearch(store);
            var csrSearch = new CsrGraphSearch(csrStore);

            const int start = 1;
            const int target = 25_000;
            string startStr = $"Node_{start}";
            string targetStr = $"Node_{target}";

            int[] pathBuffer = new int[4096];
            Span<int> pathSpan = pathBuffer.AsSpan();

            // Warmup
            for (int i = 0; i < 20; i++)
            {
                _ = search.FindShortestPath(startStr, targetStr);
                _ = search.FindShortestPath(start, target, pathSpan);
                _ = search.FindPathDfs(start, target, pathSpan);
                _ = search.FindShortestPathDijkstra(start, target, pathSpan);
                _ = search.FindNeighborhood(start, maxHops: 3, pathSpan);
                _ = csrSearch.FindShortestPath(start, target, pathSpan);
                _ = csrSearch.FindShortestPathDijkstra(start, target, pathSpan);
            }

            const int iterations = 1000;

            // 1. BFS Allocating Baseline
            long allocBeforeBfsBase = GC.GetAllocatedBytesForCurrentThread();
            var swBfsBase = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                _ = search.FindShortestPath(startStr, targetStr);
            }
            swBfsBase.Stop();
            long allocAfterBfsBase = GC.GetAllocatedBytesForCurrentThread();
            long bfsBaseBytes = allocAfterBfsBase - allocBeforeBfsBase;

            // 2. BFS Zero-Alloc Scratch
            long allocBeforeBfsZero = GC.GetAllocatedBytesForCurrentThread();
            var swBfsZero = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                _ = search.FindShortestPath(start, target, pathSpan);
            }
            swBfsZero.Stop();
            long allocAfterBfsZero = GC.GetAllocatedBytesForCurrentThread();
            long bfsZeroBytes = allocAfterBfsZero - allocBeforeBfsZero;

            // 3. DFS Zero-Alloc
            long allocBeforeDfsZero = GC.GetAllocatedBytesForCurrentThread();
            var swDfsZero = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                _ = search.FindPathDfs(start, target, pathSpan);
            }
            swDfsZero.Stop();
            long allocAfterDfsZero = GC.GetAllocatedBytesForCurrentThread();
            long dfsZeroBytes = allocAfterDfsZero - allocBeforeDfsZero;

            // 4. Dijkstra Zero-Alloc
            long allocBeforeDijZero = GC.GetAllocatedBytesForCurrentThread();
            var swDijZero = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                _ = search.FindShortestPathDijkstra(start, target, pathSpan);
            }
            swDijZero.Stop();
            long allocAfterDijZero = GC.GetAllocatedBytesForCurrentThread();
            long dijZeroBytes = allocAfterDijZero - allocBeforeDijZero;

            // 5. CSR Memory-Mapped BFS Zero-Alloc
            long allocBeforeCsrBfs = GC.GetAllocatedBytesForCurrentThread();
            var swCsrBfs = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                _ = csrSearch.FindShortestPath(start, target, pathSpan);
            }
            swCsrBfs.Stop();
            long allocAfterCsrBfs = GC.GetAllocatedBytesForCurrentThread();
            long csrBfsBytes = allocAfterCsrBfs - allocBeforeCsrBfs;

            // 6. CSR Memory-Mapped Dijkstra Zero-Alloc
            long allocBeforeCsrDij = GC.GetAllocatedBytesForCurrentThread();
            var swCsrDij = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                _ = csrSearch.FindShortestPathDijkstra(start, target, pathSpan);
            }
            swCsrDij.Stop();
            long allocAfterCsrDij = GC.GetAllocatedBytesForCurrentThread();
            long csrDijBytes = allocAfterCsrDij - allocBeforeCsrDij;

            PrintMetric("BFS (Allocating Baseline)", swBfsBase.Elapsed, iterations, bfsBaseBytes);
            PrintMetric("BFS (Zero-Alloc Scratch)", swBfsZero.Elapsed, iterations, bfsZeroBytes);
            PrintMetric("DFS (Zero-Alloc Scratch)", swDfsZero.Elapsed, iterations, dfsZeroBytes);
            PrintMetric("Dijkstra (Zero-Alloc Scratch)", swDijZero.Elapsed, iterations, dijZeroBytes);
            PrintMetric("CSR MMF BFS (Zero-Alloc)", swCsrBfs.Elapsed, iterations, csrBfsBytes);
            PrintMetric("CSR MMF Dijkstra (Zero-Alloc)", swCsrDij.Elapsed, iterations, csrDijBytes);

            if (bfsBaseBytes > 0)
            {
                double reduction = (bfsBaseBytes - bfsZeroBytes) / (double)bfsBaseBytes * 100.0;
                Console.WriteLine($"    >>> GC Allocation Drop: {reduction:F1}% (from {bfsBaseBytes / (double)iterations:F0} B/op down to {bfsZeroBytes / (double)iterations:F0} B/op)");
            }
        }
        finally
        {
            if (File.Exists(tempCsr))
            {
                try { File.Delete(tempCsr); } catch { }
            }
        }
    }

    private static void RunSimdKernelBenchmarks()
    {
        Console.WriteLine("\n[2] SIMD/AVX-512 Kernels Throughput:");

        int[] sizes = [64, 256, 1024, 4096];
        var rng = new Random(42);

        foreach (int size in sizes)
        {
            int[] data = new int[size];
            for (int i = 0; i < size; i++) data[i] = (i + 1) * 2;
            int target = data[size / 2];

            int[] offsets = new int[size + 2];
            int curr = 0;
            for (int i = 0; i < offsets.Length; i++) { offsets[i] = curr; curr += rng.Next(1, 10); }
            int[] dest = new int[size];

            int[] setA = new int[size];
            int[] setB = new int[size];
            for (int i = 0; i < size; i++) { setA[i] = i * 2; setB[i] = (i / 2) * 2; }

            const int iterations = 100_000;

            // Neighbor Lookup: Scalar vs SIMD
            for (int i = 0; i < 1000; i++)
            {
                _ = CsrKernels.ContainsNeighborScalar(data.AsSpan(), target);
                _ = CsrKernels.ContainsNeighbor(data.AsSpan(), target);
            }

            var swLookupScalar = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) _ = CsrKernels.ContainsNeighborScalar(data.AsSpan(), target);
            swLookupScalar.Stop();

            var swLookupSimd = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) _ = CsrKernels.ContainsNeighbor(data.AsSpan(), target);
            swLookupSimd.Stop();

            // Degree computation: Scalar vs SIMD
            var swDegScalar = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) CsrKernels.ComputeDegreesScalar(offsets.AsSpan(), 1, dest.AsSpan());
            swDegScalar.Stop();

            var swDegSimd = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) CsrKernels.ComputeDegrees(offsets.AsSpan(), 1, dest.AsSpan());
            swDegSimd.Stop();

            // Common neighbors
            const int cnIterations = 1000;
            var swCnScalar = Stopwatch.StartNew();
            for (int i = 0; i < cnIterations; i++) _ = CsrKernels.CountCommonNeighborsScalar(setA.AsSpan(), setB.AsSpan());
            swCnScalar.Stop();

            var swCnSimd = Stopwatch.StartNew();
            for (int i = 0; i < cnIterations; i++) _ = CsrKernels.CountCommonNeighbors(setA.AsSpan(), setB.AsSpan(), isSorted: false);
            swCnSimd.Stop();

            double lookupScalarMops = (iterations / swLookupScalar.Elapsed.TotalSeconds) / 1_000_000.0;
            double lookupSimdMops = (iterations / swLookupSimd.Elapsed.TotalSeconds) / 1_000_000.0;
            double degScalarMops = (iterations / swDegScalar.Elapsed.TotalSeconds) / 1_000_000.0;
            double degSimdMops = (iterations / swDegSimd.Elapsed.TotalSeconds) / 1_000_000.0;
            double cnScalarMops = (cnIterations / swCnScalar.Elapsed.TotalSeconds) / 1_000_000.0;
            double cnSimdMops = (cnIterations / swCnSimd.Elapsed.TotalSeconds) / 1_000_000.0;

            Console.WriteLine($"    Size {size,4}: Neighbor Lookup [Scalar={lookupScalarMops,6:F1} | SIMD={lookupSimdMops,6:F1} MOps/s ({lookupSimdMops / lookupScalarMops:F1}x)]");
            Console.WriteLine($"               Degree Compute  [Scalar={degScalarMops,6:F1} | SIMD={degSimdMops,6:F1} MOps/s ({degSimdMops / degScalarMops:F1}x)]");
            Console.WriteLine($"               Common Neighbor [Scalar={cnScalarMops,6:F3} | SIMD={cnSimdMops,6:F3} MOps/s ({cnSimdMops / cnScalarMops:F1}x)]");
        }
    }

    private static void PrintMetric(string name, TimeSpan elapsed, int iterations, long totalAllocBytes)
    {
        double opsPerSec = iterations / elapsed.TotalSeconds;
        double latencyUs = (elapsed.TotalMilliseconds / iterations) * 1000.0;
        double bytesPerOp = totalAllocBytes / (double)iterations;

        Console.WriteLine($"    {name,-30}: {opsPerSec,10:N0} Ops/s | Latency: {latencyUs,8:F2} µs | Alloc: {bytesPerOp,6:F0} B/op");
    }
}
