﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using FParser;
using Core;
using JetBrains.Annotations;

/// <summary>
/// Node holds inert data for a NodeLinkedList.
/// </summary>
/// <typeparam name="V"></typeparam>
public class Node<V> {
    [CanBeNull] public Node<V> prev;
    [CanBeNull] public Node<V> next;
    public V obj;

    public Node(V t) {
        obj = t;
    }
    private void SetNext(Node<V> n) {
        next = n;
    }
    private void SetPrev(Node<V> n) {
        prev = n;
    }
    public void RemoveThis() {
        next?.SetPrev(prev);
        prev?.SetNext(next);
    }
}
public sealed class NodeLinkedList<T> {
    [CanBeNull] public Node<T> first { get; private set; }
    [CanBeNull] public Node<T> last { get; private set; }
    [CanBeNull] private static Node<T> cacheFirst = null; //Can be shared between LLs.
    public int count { get; private set; } = 0;
    

    public Node<T> Add(T obj) => Append(Get(obj));

    public Node<T> AddBefore(Node<T> curr, T obj) => InsertBefore(curr, Get(obj));
    public Node<T> AddAfter(Node<T> curr, T obj) => InsertAfter(curr, Get(obj));

    private static Node<T> Get(T obj) {
        if (cacheFirst != null) {
            var n = cacheFirst;
            cacheFirst = n.next;
            n.obj = obj;
            return n;
        }
        return new Node<T>(obj);
    }

    private static void AddToCache(Node<T> n) {
        n.next = cacheFirst;
        cacheFirst = n;
    }

    private static void AddToCache(Node<T> first, Node<T> last) {
        last.next = cacheFirst;
        cacheFirst = first;
    }

    private Node<T> Append(Node<T> n) {
        if (last != null) {
            last.next = n;
        }
        n.next = null;
        n.prev = last;
        last = n;
        if (first == null) {
            first = n;
        }
        ++count;
        return n;
    }

    public Node<T> InsertBefore(Node<T> curr, Node<T> toAdd) {
        if (curr.prev == null) { //curr == first
            first = toAdd;
        } else {
            curr.prev.next = toAdd;
        }
        toAdd.prev = curr.prev;
        toAdd.next = curr;
        curr.prev = toAdd;
        ++count;
        return toAdd;
    }
    public Node<T> InsertAfter(Node<T> curr, Node<T> toAdd) {
        if (curr.next == null) { //curr == last
            last = toAdd;
        } else {
            curr.next.prev = toAdd;
        }
        toAdd.next = curr.next;
        toAdd.prev = curr;
        curr.next = toAdd;
        ++count;
        return toAdd;
    }

    /// <summary>
    /// Remove a node. If cacheable is set, then the node object will be used by the LL to optimize allocation.
    /// If cacheable is set, you must NOT use the node after calling. 
    /// </summary>
    public void Remove(Node<T> n, bool cacheable) {
        if (first == n) { first = n.next; }
        if (last == n) { last = n.prev; }
        n.RemoveThis();
        --count;
        if (cacheable) {
            AddToCache(n);
        }
    }

    public void Reset() {
        if (count > 0) {
            AddToCache(first, last);
            first = null;
            last = null;
            count = 0;
        }
    }
    
    #if UNITY_EDITOR

    public Node<T> At(int ii) {
        for (Node<T> nr = first; nr != null; nr = nr.next, --ii) {
            if (ii == 0) return nr;
        }
        return null;
    }
    public int IndexOf(Node<T> n) {
        int ii = 0;
        for (Node<T> nr = first; nr != null; nr = nr.next, ++ii) {
            if (nr == n) return ii;
        }
        return -1;
    }
    #endif
}

/// <summary>
/// Fast resizeable wrapper around T[] that is safe for arbitrary indexing.
/// </summary>
public class SafeResizableArray<T> {
    private int count;
    private T[] arr;
    public SafeResizableArray(int size = 8) {
        arr = new T[size];
        count = 0;
    }

    public void SafeAssign(int index, T value) {
        while (index >= arr.Length) {
            var narr = new T[arr.Length * 2];
            arr.CopyTo(narr, 0);
            arr = narr;
        }
        arr[index++] = value;
        if (index > count) count = index;
    }

    public T SafeGet(int index) {
        while (index >= arr.Length) {
            var narr = new T[arr.Length * 2];
            arr.CopyTo(narr, 0);
            arr = narr;
        }
        return arr[index];
    }
    
    private static readonly ExFunction safeAssign = ExUtils.Wrap<SafeResizableArray<T>>("SafeAssign",new[] { typeof(int), typeof(T) });
    private static readonly ExFunction safeGet = ExUtils.Wrap<SafeResizableArray<T>, int>("SafeGet");

    public Expression SafeAssign(Expression index, Expression value) =>
        safeAssign.InstanceOf(Expression.Constant(this), index, value);
    public Expression SafeGet(Expression index) =>
        safeGet.InstanceOf(Expression.Constant(this), index);
    
    public void Empty(bool trueClear) {
        if (trueClear) Array.Clear(arr, 0, arr.Length);
        count = 0;
    }
    
}

/// <summary>
/// An ordered collection that supports iteration, as well as deletion of arbitrary indices.
/// Indices are not guaranteed to be persistent, so deletion must occur during the iteration block of an index.
/// </summary>
/// <typeparam name="T"></typeparam>
public class CompactingArray<T> where T: struct {
    protected int count;
    public int Count => count;
    protected bool[] rem;
    public T[] arr;
    private bool requiresCompact;

    public CompactingArray(int size = 1) {
        arr = new T[size];
        rem = new bool[size];
        count = 0;
        requiresCompact = false;
    }

    public void DeleteLast() {
        rem[--count] = false;
    }
    public void Delete(int ind) {
        rem[ind] = true;
        requiresCompact = true;
    }
    public void Compact() {
        if (requiresCompact) {
            int ii = 0;
            
            while (true) {
                //Prevents incorrect compacting if requiresCompact is not accurate
                if (ii == count) return;
                if (rem[ii++]) {
                    rem[ii - 1] = false;
                    break;
                }
            }
            int deficit = 1;
            int start_copy = ii;
            for (; ii < count; ++ii) {
                if (rem[ii]) {
                    if (ii > start_copy) {
                        Array.Copy(arr, start_copy,
                            arr, start_copy - deficit, ii - start_copy);
                    }
                    rem[ii] = false;
                    ++deficit;
                    start_copy = ii + 1;
                }
            }
            Array.Copy(arr, start_copy,
                arr, start_copy - deficit, count - start_copy);
            count -= deficit;
            requiresCompact = false;
        }
    }
    public void Add(ref T obj) {
        if (count >= arr.Length) {
            var narr = new T[arr.Length * 2];
            arr.CopyTo(narr, 0);
            arr = narr;
            var nrem = new bool[arr.Length * 2];
            rem.CopyTo(nrem, 0);
            rem = nrem;
        }
        arr[count++] = obj;
    }

    protected void Empty(bool trueClear) {
        if (trueClear) Array.Clear(arr, 0, arr.Length);
        count = 0;
    }
    
    public ref T this[int index] => ref arr[index];
}

public class DeletionMarker<T> {
    public T obj;
    public int priority;
    public bool markedForDeletion { get; private set; }
    private static readonly Stack<DeletionMarker<T>> cache = new Stack<DeletionMarker<T>>();

    public static DeletionMarker<T> Get(T obj, int priority) {
        DeletionMarker<T> delt;
        if (cache.Count > 0) {
            delt = cache.Pop();
        } else {
            delt = new DeletionMarker<T>();
        }
        delt.obj = obj;
        delt.priority = priority;
        delt.markedForDeletion = false;
        return delt;
    }

    public void MarkForDeletion() => markedForDeletion = true;

    public void Destroy() {
        obj = default;
        cache.Push(this);
    }
}

/// <summary>
/// An ordered collection that supports iteration, as well as deletion of arbitrary elements via
/// persistent identification of added elements returned to users.
/// Indices are not guaranteed to be persistent and should not be used for identification.
/// </summary>
/// <typeparam name="T"></typeparam>
public class DMCompactingArray<T> {
    private int count;
    public int Count => count;
    public DeletionMarker<T>[] arr;
    private bool requiresCompact;

    public DMCompactingArray(int size = 8) {
        arr = new DeletionMarker<T>[size];
        count = 0;
    }
    public void Compact() {
        int ii = 0;
        bool foundDeleted = false;
        while (ii < count) {
            if (arr[ii++].markedForDeletion) {
                arr[ii - 1].Destroy();
                foundDeleted = true;
                break;
            }
        }
        if (!foundDeleted) return;
        int deficit = 1;
        int start_copy = ii;
        for (; ii < count; ++ii) {
            if (arr[ii].markedForDeletion) {
                if (ii > start_copy) {
                    Array.Copy(arr, start_copy,
                        arr, start_copy - deficit, ii - start_copy);
                }
                arr[ii].Destroy();
                ++deficit;
                start_copy = ii + 1;
            }
        }
        Array.Copy(arr, start_copy,
            arr, start_copy - deficit, count - start_copy);
        count -= deficit;
    }

    private void MaybeResize() {
        if (count >= arr.Length) {
            var narr = new DeletionMarker<T>[arr.Length * 2];
            arr.CopyTo(narr, 0);
            arr = narr;
        }
    }
    public DeletionMarker<T> Add(T obj) {
        MaybeResize();
        var dm = DeletionMarker<T>.Get(obj, 0);
        arr[count++] = dm;
        return dm;
    }

    /// <summary>
    /// Returns the first index where the priority is greater than the given value.
    /// </summary>
    /// <param name="p"></param>
    /// <returns></returns>
    private int NextIndexForPriority(int p) {
        //TODO you can make this binary search or whatever
        for (int ii = 0; ii < count; ++ii) {
            if (arr[ii].priority > p) return ii;
        }
        return count;
    }

    public int FirstZeroPriority => FirstPriorityGT(0);
    public int FirstPriorityGT(int i) => NextIndexForPriority(i-1);
    /// <summary>
    /// Add an element into the array with a priority.
    /// Lower priorities will be inserted at the front of the array.
    /// </summary>
    /// <param name="obj"></param>
    /// <param name="priority"></param>
    /// <returns></returns>
    public DeletionMarker<T> AddPriority(T obj, int priority) {
        MaybeResize();
        var dm = DeletionMarker<T>.Get(obj, priority);
        arr.Insert(ref count, dm, NextIndexForPriority(priority));
        return dm;
    } 
    
    public void AddPriority(DeletionMarker<T> dm) {
        MaybeResize();
        arr.Insert(ref count, dm, NextIndexForPriority(dm.priority));
    }

    public void Empty() {
        for (int ii = 0; ii < count; ++ii) {
            arr[ii].Destroy();
            arr[ii] = null;
        }
        count = 0;
    }

    public void Delete(int ii) => arr[ii].MarkForDeletion();
    
    public T this[int index] => arr[index].obj;

    public void ForEachIfNotCancelled(Action<T> func) {
        int ct = count;
        for (int ii = 0; ii < ct; ++ii) {
            if (!arr[ii].markedForDeletion) func(arr[ii].obj);
        }
        Compact();
    }
}

public static class DictCache<K, V> {
    private static readonly Stack<Dictionary<K, V>> cached = new Stack<Dictionary<K, V>>();

    public static void Consign(Dictionary<K, V> cacheMe) {
        cacheMe.Clear();
        cached.Push(cacheMe);
    }

    public static Dictionary<K, V> Get() => cached.Count > 0 ? cached.Pop() : new Dictionary<K, V>();
}

public static class ListCache<T> {
    private static readonly Stack<List<T>> cached = new Stack<List<T>>();
    public static void Consign(List<T> cacheMe) {
        cacheMe.Clear();
        cached.Push(cacheMe);
    }

    public static List<T> Get() => cached.Count > 0 ? cached.Pop() : new List<T>();
}