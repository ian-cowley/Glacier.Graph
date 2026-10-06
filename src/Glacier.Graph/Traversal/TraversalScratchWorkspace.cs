namespace Glacier.Graph.Traversal;

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

/// <summary>
/// A high-performance, reusable scratch buffer and generation-tagged visited workspace
/// for graph traversals (BFS, DFS, Dijkstra, Neighborhood).
/// Eliminates GC heap allocations by renting primitive flat arrays from <see cref="ArrayPool{T}.Shared"/>
/// and using an O(1) integer generation tag to reset visited node state without clearing memory.
/// </summary>
public sealed class TraversalScratchWorkspace : IDisposable
{
    [ThreadStatic]
    private static TraversalScratchWorkspace? t_threadInstance;

    private int[] _visitedTag;
    private int[] _parent;
    private int[] _distance;
    private float[] _distances;
    private int[] _queueBuffer;
    private int[] _heapNodes;
    private float[] _heapPriorities;

    private int _capacity;
    private int _queueCapacity;
    private int _heapCapacity;
    private int _currentGeneration;
    private bool _disposed;

    // Fast queue pointers (BFS / Neighborhood)
    public int Head;
    public int Tail;

    // Fast stack pointer (DFS)
    public int StackTop;

    // Fast min-heap counter (Dijkstra)
    public int HeapCount;

    /// <summary>
    /// Gets the current generation number used to identify nodes visited in the current traversal.
    /// </summary>
    public int CurrentGeneration => _currentGeneration;

    /// <summary>
    /// Gets the allocated node capacity of this workspace.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Gets the raw visited tag array. A node <c>u</c> is visited if <c>VisitedTag[u] == CurrentGeneration</c>.
    /// </summary>
    public int[] VisitedTag => _visitedTag;

    /// <summary>
    /// Gets the raw parent array used for path reconstruction.
    /// </summary>
    public int[] Parent => _parent;

    /// <summary>
    /// Gets the raw hop distance array.
    /// </summary>
    public int[] Distance => _distance;

    /// <summary>
    /// Gets the raw weighted distance array for Dijkstra.
    /// </summary>
    public float[] Distances => _distances;

    /// <summary>
    /// Gets the raw queue / stack buffer.
    /// </summary>
    public int[] QueueBuffer => _queueBuffer;

    /// <summary>
    /// Initializes a new instance of <see cref="TraversalScratchWorkspace"/> with the specified initial capacity.
    /// </summary>
    public TraversalScratchWorkspace(int initialCapacity = 4096)
    {
        _capacity = Math.Max(16, initialCapacity);
        _queueCapacity = _capacity * 2;
        _heapCapacity = _capacity * 2;

        _visitedTag = ArrayPool<int>.Shared.Rent(_capacity);
        Array.Clear(_visitedTag, 0, _visitedTag.Length);

        _parent = ArrayPool<int>.Shared.Rent(_capacity);
        _distance = ArrayPool<int>.Shared.Rent(_capacity);
        _distances = ArrayPool<float>.Shared.Rent(_capacity);

        _queueBuffer = ArrayPool<int>.Shared.Rent(_queueCapacity);
        _heapNodes = ArrayPool<int>.Shared.Rent(_heapCapacity);
        _heapPriorities = ArrayPool<float>.Shared.Rent(_heapCapacity);

        _currentGeneration = 1;
    }

    /// <summary>
    /// Retrieves a thread-static workspace instance, automatically ensuring sufficient capacity.
    /// Avoids all heap allocations across repeated graph traversals on the calling thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TraversalScratchWorkspace GetThreadStatic(int requiredCapacity)
    {
        var ws = t_threadInstance;
        if (ws == null || ws._disposed)
        {
            ws = new TraversalScratchWorkspace(requiredCapacity);
            t_threadInstance = ws;
        }
        else
        {
            ws.EnsureCapacity(requiredCapacity);
        }

        return ws;
    }

    /// <summary>
    /// Rents a standalone workspace for dedicated or long-lived background traversal workloads.
    /// Must be disposed when no longer needed to return buffers to the pool.
    /// </summary>
    public static TraversalScratchWorkspace Rent(int initialCapacity)
    {
        return new TraversalScratchWorkspace(initialCapacity);
    }

    /// <summary>
    /// Ensures that the workspace arrays are large enough to index nodes up to <paramref name="requiredCapacity"/>.
    /// </summary>
    public void EnsureCapacity(int requiredCapacity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (requiredCapacity <= _capacity) return;

        int newCapacity = Math.Max(requiredCapacity, _capacity * 2);

        // Resize visited tag
        int[] newVisited = ArrayPool<int>.Shared.Rent(newCapacity);
        Array.Clear(newVisited, 0, newVisited.Length);
        ArrayPool<int>.Shared.Return(_visitedTag);
        _visitedTag = newVisited;

        // Resize parent
        int[] newParent = ArrayPool<int>.Shared.Rent(newCapacity);
        ArrayPool<int>.Shared.Return(_parent);
        _parent = newParent;

        // Resize distance
        int[] newDist = ArrayPool<int>.Shared.Rent(newCapacity);
        ArrayPool<int>.Shared.Return(_distance);
        _distance = newDist;

        // Resize float distances
        float[] newDistances = ArrayPool<float>.Shared.Rent(newCapacity);
        ArrayPool<float>.Shared.Return(_distances);
        _distances = newDistances;

        _capacity = newCapacity;
        _currentGeneration = 1; // reset generation since visited tag was cleared
    }

    /// <summary>
    /// Advances the generation counter by 1 in O(1) time without zeroing memory.
    /// When the generation counter reaches <see cref="int.MaxValue"/>, resets it to 1 and clears the visited tag array.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NextGeneration(int requiredCapacity)
    {
        EnsureCapacity(requiredCapacity);

        _currentGeneration++;
        if (_currentGeneration == int.MaxValue)
        {
            Array.Clear(_visitedTag, 0, _visitedTag.Length);
            _currentGeneration = 1;
        }
    }

    /// <summary>
    /// Manually resets the generation counter and clears the visited tag array.
    /// </summary>
    public void ResetGeneration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Array.Clear(_visitedTag, 0, _visitedTag.Length);
        _currentGeneration = 1;
    }

    /// <summary>
    /// Checks whether the given node internal ID has been visited in the current generation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsVisited(int node)
    {
        return (uint)node < (uint)_visitedTag.Length && _visitedTag[node] == _currentGeneration;
    }

    /// <summary>
    /// Marks the given node internal ID as visited in the current generation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetVisited(int node)
    {
        _visitedTag[node] = _currentGeneration;
    }

    // --- Queue Operations (BFS / Neighborhood) ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetQueue()
    {
        Head = 0;
        Tail = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(int node)
    {
        if ((uint)Tail >= (uint)_queueBuffer.Length)
        {
            GrowQueue();
        }

        _queueBuffer[Tail++] = node;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Dequeue()
    {
        return _queueBuffer[Head++];
    }

    public bool HasQueueItems
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Head < Tail;
    }

    public int QueueCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Tail - Head;
    }

    private void GrowQueue()
    {
        int newCap = _queueBuffer.Length * 2;
        int[] newBuf = ArrayPool<int>.Shared.Rent(newCap);
        if (Tail > Head)
        {
            Array.Copy(_queueBuffer, Head, newBuf, 0, Tail - Head);
            Tail -= Head;
            Head = 0;
        }
        else
        {
            Head = 0;
            Tail = 0;
        }

        ArrayPool<int>.Shared.Return(_queueBuffer);
        _queueBuffer = newBuf;
        _queueCapacity = newCap;
    }

    // --- Stack Operations (DFS) ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetStack()
    {
        StackTop = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushStack(int node)
    {
        if ((uint)StackTop >= (uint)_queueBuffer.Length)
        {
            GrowQueue();
        }

        _queueBuffer[StackTop++] = node;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PopStack()
    {
        return _queueBuffer[--StackTop];
    }

    public bool HasStackItems
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StackTop > 0;
    }

    // --- Min-Heap Operations (Dijkstra) ---

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetHeap()
    {
        HeapCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnqueueHeap(int node, float priority)
    {
        if ((uint)HeapCount >= (uint)_heapNodes.Length)
        {
            GrowHeap();
        }

        int index = HeapCount++;
        _heapNodes[index] = node;
        _heapPriorities[index] = priority;
        SiftUp(index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueHeap(out int node, out float priority)
    {
        if (HeapCount == 0)
        {
            node = 0;
            priority = 0f;
            return false;
        }

        node = _heapNodes[0];
        priority = _heapPriorities[0];

        HeapCount--;
        if (HeapCount > 0)
        {
            _heapNodes[0] = _heapNodes[HeapCount];
            _heapPriorities[0] = _heapPriorities[HeapCount];
            SiftDown(0);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) >> 1;
            if (_heapPriorities[index] < _heapPriorities[parent])
            {
                // Swap
                int tempNode = _heapNodes[index];
                _heapNodes[index] = _heapNodes[parent];
                _heapNodes[parent] = tempNode;

                float tempPrio = _heapPriorities[index];
                _heapPriorities[index] = _heapPriorities[parent];
                _heapPriorities[parent] = tempPrio;

                index = parent;
            }
            else
            {
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SiftDown(int index)
    {
        int count = HeapCount;
        while (true)
        {
            int left = (index << 1) + 1;
            int right = left + 1;
            int smallest = index;

            if (left < count && _heapPriorities[left] < _heapPriorities[smallest])
            {
                smallest = left;
            }

            if (right < count && _heapPriorities[right] < _heapPriorities[smallest])
            {
                smallest = right;
            }

            if (smallest != index)
            {
                // Swap
                int tempNode = _heapNodes[index];
                _heapNodes[index] = _heapNodes[smallest];
                _heapNodes[smallest] = tempNode;

                float tempPrio = _heapPriorities[index];
                _heapPriorities[index] = _heapPriorities[smallest];
                _heapPriorities[smallest] = tempPrio;

                index = smallest;
            }
            else
            {
                break;
            }
        }
    }

    private void GrowHeap()
    {
        int newCap = _heapNodes.Length * 2;
        int[] newNodes = ArrayPool<int>.Shared.Rent(newCap);
        float[] newPrios = ArrayPool<float>.Shared.Rent(newCap);

        Array.Copy(_heapNodes, newNodes, HeapCount);
        Array.Copy(_heapPriorities, newPrios, HeapCount);

        ArrayPool<int>.Shared.Return(_heapNodes);
        ArrayPool<float>.Shared.Return(_heapPriorities);

        _heapNodes = newNodes;
        _heapPriorities = newPrios;
        _heapCapacity = newCap;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (t_threadInstance == this)
        {
            t_threadInstance = null;
        }

        ArrayPool<int>.Shared.Return(_visitedTag);
        ArrayPool<int>.Shared.Return(_parent);
        ArrayPool<int>.Shared.Return(_distance);
        ArrayPool<float>.Shared.Return(_distances);
        ArrayPool<int>.Shared.Return(_queueBuffer);
        ArrayPool<int>.Shared.Return(_heapNodes);
        ArrayPool<float>.Shared.Return(_heapPriorities);
    }
}
