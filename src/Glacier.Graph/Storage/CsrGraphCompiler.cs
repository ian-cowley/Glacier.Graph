using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Glacier.Graph.Storage
{
    /// <summary>
    /// Compiles a dynamic Forward Star graph (GraphStore) into a highly optimized, 
    /// static Compressed Sparse Row (CSR) format on disk.
    /// </summary>
    public static class CsrGraphCompiler
    {
        public static void CompileFromForwardStar(GraphStore source, string outFilePath)
        {
            int nodeCount = source.NodeCount;
            int edgeCount = source.EdgeCount;

            // CSR Arrays
            // Row offsets need to be NodeCount + 2 because nodes are 1-indexed.
            // rowOffsets[i] is the start index in columnIndices for node i's edges.
            // rowOffsets[i+1] - rowOffsets[i] is the degree of node i.
            int[] rowOffsets = new int[nodeCount + 2];
            int[] columnIndices = new int[edgeCount];
            int[] edgeRelations = new int[edgeCount];

            int currentEdgeIndex = 0;

            for (int i = 1; i <= nodeCount; i++)
            {
                rowOffsets[i] = currentEdgeIndex;

                var edges = source.GetOutwardEdgesByInternalId(i);
                while (edges.MoveNext())
                {
                    columnIndices[currentEdgeIndex] = edges.CurrentTargetNodeId;
                    edgeRelations[currentEdgeIndex] = edges.CurrentRelationId;
                    currentEdgeIndex++;
                    edges.Advance();
                }
            }
            
            // Set the final boundary
            rowOffsets[nodeCount + 1] = currentEdgeIndex;

            // Save to disk
            using var fs = new FileStream(outFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            using var writer = new BinaryWriter(fs);

            // 1. Header (Magic Bytes + Version + Capacities)
            writer.Write("GCSR");
            writer.Write(1);
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

            // 4. String Metadata Array
            // Note: source._head.Length is the node capacity, not the node count.
            writer.Write(source._head.Length);
            for (int i = 0; i < source._head.Length; i++)
            {
                writer.Write(source._nodeMetadata[i] ?? string.Empty);
            }

            // 5. Raw Array Byte Dumps (The fast part)
            fs.Write(MemoryMarshal.AsBytes(rowOffsets.AsSpan()));
            fs.Write(MemoryMarshal.AsBytes(columnIndices.AsSpan()));
            fs.Write(MemoryMarshal.AsBytes(edgeRelations.AsSpan()));
        }
    }
}
