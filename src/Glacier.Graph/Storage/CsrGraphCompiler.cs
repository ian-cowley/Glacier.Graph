using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Glacier.Graph.Storage
{
    /// <summary>
    /// Compiles a dynamic Forward Star graph (<see cref="GraphStore"/>) into a compact,
    /// static Compressed Sparse Row (CSR) binary format (<c>.csr</c> / <c>.gcsr</c>) on disk,
    /// preserving node metadata, relation IDs, and per-edge weights with sorted per-node neighbor lists.
    /// </summary>
    public static class CsrGraphCompiler
    {
        internal const int CurrentVersion = 3;
        internal const int FlagNeighborsSorted = 1 << 0;
        private const int ChunkElementCapacity = 16_384; // 16,384 * 4 bytes = 64 KB (< 85 KB LOH threshold)

        private readonly record struct CsrEdgeEntry(int TargetNodeId, int RelationId, float Weight) : IComparable<CsrEdgeEntry>
        {
            public int CompareTo(CsrEdgeEntry other)
            {
                int cmp = TargetNodeId.CompareTo(other.TargetNodeId);
                if (cmp != 0) return cmp;
                cmp = RelationId.CompareTo(other.RelationId);
                if (cmp != 0) return cmp;
                return Weight.CompareTo(other.Weight);
            }
        }

        public static void CompileFromForwardStar(GraphStore source, string outFilePath)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(outFilePath);

            string fullPath = Path.GetFullPath(outFilePath);
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";

            source.EnterReadLock();
            try
            {
                int nodeCount = source._nodeCount;
                int edgeCount = source._edgeCount;

                int[]? rowChunk = ArrayPool<int>.Shared.Rent(ChunkElementCapacity);
                CsrEdgeEntry[]? perNodeBuf = ArrayPool<CsrEdgeEntry>.Shared.Rent(64);
                int[]? colChunk = ArrayPool<int>.Shared.Rent(ChunkElementCapacity);
                int[]? relChunk = ArrayPool<int>.Shared.Rent(ChunkElementCapacity);
                float[]? wtChunk = ArrayPool<float>.Shared.Rent(ChunkElementCapacity);

                try
                {
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    {
                        using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
                        {
                            // 1. Header (Magic Bytes + Version 3 + Active Counts + Sorted Flag)
                            writer.Write("GCSR");
                            writer.Write(CurrentVersion);
                            writer.Write(nodeCount);
                            writer.Write(edgeCount);
                            writer.Write(FlagNeighborsSorted);

                            // 2. Relation Types Dict
                            writer.Write(source._relationTypes.Count);
                            foreach (var kvp in source._relationTypes)
                            {
                                writer.Write(kvp.Key);
                                writer.Write(kvp.Value);
                            }

                            // 3. Node ID Mappings
                            writer.Write(source._externalToInternalId.Count);
                            foreach (var kvp in source._externalToInternalId)
                            {
                                writer.Write(kvp.Key);
                                writer.Write(kvp.Value);
                            }

                            // 4. String Metadata Array (only active slots 0..nodeCount)
                            int activeMetadataSlots = nodeCount + 1;
                            writer.Write(activeMetadataSlots);
                            for (int i = 0; i < activeMetadataSlots; i++)
                            {
                                string meta = i < source._nodeMetadata.Length
                                    ? (source._nodeMetadata[i] ?? string.Empty)
                                    : string.Empty;
                                writer.Write(meta);
                            }

                            writer.Flush();
                        }

                        // 5. Stream rowOffsets (NodeCount + 2 ints) in 64 KB chunks
                        int rowChunkCount = 0;
                        rowChunk[rowChunkCount++] = 0; // rowOffsets[0] = 0

                        int runningOffset = 0;
                        for (int i = 1; i <= nodeCount; i++)
                        {
                            rowChunk[rowChunkCount++] = runningOffset;
                            if (rowChunkCount == ChunkElementCapacity)
                            {
                                fs.Write(MemoryMarshal.AsBytes(rowChunk.AsSpan(0, rowChunkCount)));
                                rowChunkCount = 0;
                            }

                            var degreeEdges = source.GetOutwardEdgesUnsafe(i);
                            while (degreeEdges.MoveNext())
                            {
                                runningOffset++;
                                degreeEdges.Advance();
                            }
                        }

                        rowChunk[rowChunkCount++] = runningOffset; // rowOffsets[nodeCount + 1]
                        if (rowChunkCount > 0)
                        {
                            fs.Write(MemoryMarshal.AsBytes(rowChunk.AsSpan(0, rowChunkCount)));
                            rowChunkCount = 0;
                        }

                        fs.Flush();

                        // 6. Stream sorted per-node (TargetNodeId, RelationId, Weight) into section offsets via RandomAccess.Write
                        long colStart = fs.Position;
                        long relStart = colStart + (long)edgeCount * sizeof(int);
                        long wtStart = relStart + (long)edgeCount * sizeof(int);
                        long totalFileLength = wtStart + (long)edgeCount * sizeof(float);
                        fs.SetLength(totalFileLength);

                        int chunkCount = 0;
                        long writtenEdges = 0;

                        for (int i = 1; i <= nodeCount; i++)
                        {
                            int deg = 0;
                            var edges = source.GetOutwardEdgesUnsafe(i);
                            while (edges.MoveNext())
                            {
                                if (deg == perNodeBuf.Length)
                                {
                                    int newCap = Math.Max(deg + 1, perNodeBuf.Length * 2);
                                    CsrEdgeEntry[] grown = ArrayPool<CsrEdgeEntry>.Shared.Rent(newCap);
                                    perNodeBuf.AsSpan(0, deg).CopyTo(grown);
                                    ArrayPool<CsrEdgeEntry>.Shared.Return(perNodeBuf);
                                    perNodeBuf = grown;
                                }

                                perNodeBuf[deg++] = new CsrEdgeEntry(
                                    edges.CurrentTargetNodeId,
                                    edges.CurrentRelationId,
                                    edges.CurrentWeight);
                                edges.Advance();
                            }

                            if (deg > 1)
                            {
                                perNodeBuf.AsSpan(0, deg).Sort();
                            }

                            for (int k = 0; k < deg; k++)
                            {
                                CsrEdgeEntry entry = perNodeBuf[k];
                                colChunk[chunkCount] = entry.TargetNodeId;
                                relChunk[chunkCount] = entry.RelationId;
                                wtChunk[chunkCount] = entry.Weight;
                                chunkCount++;

                                if (chunkCount == ChunkElementCapacity)
                                {
                                    long byteOffset = writtenEdges * sizeof(int);
                                    RandomAccess.Write(fs.SafeFileHandle, MemoryMarshal.AsBytes(colChunk.AsSpan(0, chunkCount)), colStart + byteOffset);
                                    RandomAccess.Write(fs.SafeFileHandle, MemoryMarshal.AsBytes(relChunk.AsSpan(0, chunkCount)), relStart + byteOffset);
                                    RandomAccess.Write(fs.SafeFileHandle, MemoryMarshal.AsBytes(wtChunk.AsSpan(0, chunkCount)), wtStart + byteOffset);
                                    writtenEdges += chunkCount;
                                    chunkCount = 0;
                                }
                            }
                        }

                        if (chunkCount > 0)
                        {
                            long byteOffset = writtenEdges * sizeof(int);
                            RandomAccess.Write(fs.SafeFileHandle, MemoryMarshal.AsBytes(colChunk.AsSpan(0, chunkCount)), colStart + byteOffset);
                            RandomAccess.Write(fs.SafeFileHandle, MemoryMarshal.AsBytes(relChunk.AsSpan(0, chunkCount)), relStart + byteOffset);
                            RandomAccess.Write(fs.SafeFileHandle, MemoryMarshal.AsBytes(wtChunk.AsSpan(0, chunkCount)), wtStart + byteOffset);
                        }

                        fs.Flush(flushToDisk: true);
                    }

                    File.Move(tempPath, fullPath, overwrite: true);
                }
                catch
                {
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch
                    {
                    }
                    throw;
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(rowChunk);
                    ArrayPool<CsrEdgeEntry>.Shared.Return(perNodeBuf);
                    ArrayPool<int>.Shared.Return(colChunk);
                    ArrayPool<int>.Shared.Return(relChunk);
                    ArrayPool<float>.Shared.Return(wtChunk);
                }
            }
            finally
            {
                source.ExitReadLock();
            }
        }
    }
}
