using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Glacier.Graph.Storage
{
    /// <summary>
    /// Compiles a dynamic Forward Star graph (<see cref="GraphStore"/>) into a compact,
    /// static Compressed Sparse Row (CSR) binary format (<c>.csr</c> / <c>.gcsr</c>) on disk,
    /// preserving node metadata, relation IDs, and per-edge weights.
    /// </summary>
    public static class CsrGraphCompiler
    {
        public static void CompileFromForwardStar(GraphStore source, string outFilePath)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(outFilePath);

            source.EnterReadLock();
            try
            {
                int nodeCount = source._nodeCount;
                int edgeCount = source._edgeCount;

                // CSR Arrays
                // Row offsets need to be NodeCount + 2 because nodes are 1-indexed.
                // rowOffsets[i] is the start index in columnIndices for node i's edges.
                // rowOffsets[i + 1] - rowOffsets[i] is the out-degree of node i.
                int[] rowOffsets = new int[nodeCount + 2];
                int[] columnIndices = new int[edgeCount];
                int[] edgeRelations = new int[edgeCount];
                float[] edgeWeights = new float[edgeCount];

                int currentEdgeIndex = 0;

                for (int i = 1; i <= nodeCount; i++)
                {
                    rowOffsets[i] = currentEdgeIndex;

                    var edges = source.GetOutwardEdgesUnsafe(i);
                    while (edges.MoveNext())
                    {
                        columnIndices[currentEdgeIndex] = edges.CurrentTargetNodeId;
                        edgeRelations[currentEdgeIndex] = edges.CurrentRelationId;
                        edgeWeights[currentEdgeIndex] = edges.CurrentWeight;
                        currentEdgeIndex++;
                        edges.Advance();
                    }
                }

                // Set the final boundary
                rowOffsets[nodeCount + 1] = currentEdgeIndex;

                // Save to disk
                using var fs = new FileStream(outFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                using var writer = new BinaryWriter(fs);

                // 1. Header (Magic Bytes + Version 2 + Active Counts)
                writer.Write("GCSR");
                writer.Write(2); // Version 2 persists float[] edgeWeights and compact active metadata
                writer.Write(nodeCount);
                writer.Write(edgeCount);

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

                // 5. Raw Array Byte Dumps
                fs.Write(MemoryMarshal.AsBytes(rowOffsets.AsSpan()));
                fs.Write(MemoryMarshal.AsBytes(columnIndices.AsSpan(0, currentEdgeIndex)));
                fs.Write(MemoryMarshal.AsBytes(edgeRelations.AsSpan(0, currentEdgeIndex)));
                fs.Write(MemoryMarshal.AsBytes(edgeWeights.AsSpan(0, currentEdgeIndex)));
            }
            finally
            {
                source.ExitReadLock();
            }
        }
    }
}
