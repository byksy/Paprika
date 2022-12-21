﻿using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;

namespace Tree;

/// <summary>
/// Patricia tree.
/// </summary>
/// <remarks>
/// Nibble path encoding
///
/// This is done in a way to optimize slicing.
/// To get a path shorter by 1, that will be of
/// - odd length, nothing is done, as previous nibble matches the path.
/// - even length, slice by 1, the previous nibble was odd.
/// This allows for efficient operations.
///
/// For comments, B - branch, E - extension, L1, L2...L16 - leaves.
/// </remarks>
public class PaprikaTree
{
    private const long Null = 0;
    private const int KeccakLength = 32;
    private const int KeyLenght = KeccakLength;
    private const int ValueLenght = KeccakLength;
    private const int PrefixLength = 1;

    // types
    private const byte TypeMask = 0b1100_0000;

    // leaf [...][path][value]
    private const byte LeafType = 0b0100_0000;

    // extension: [...][path][long]
    private const byte ExtensionType = 0b0000_0000;

    // branch [...][branch0][branch3][branch7]
    private const byte BranchType = 0b1000_0000;

    private readonly IDb _db;

    private long _root = Null;

    // the id which the file was flushed to
    private long _lastFlushTo = 0;

    private readonly Store _store;

    public PaprikaTree(IDb db)
    {
        _db = db;
        _store = new Store(db);
    }

    public void Set(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) =>
        _root = Set(_store, _root, NibblePath.FromKey(key, 0), value);

    private static long Set(IStore db, long current, in NibblePath addedPath,
        in ReadOnlySpan<byte> value)
    {
        if (current == Null)
        {
            return WriteLeaf(db, addedPath, value);
        }

        var node = db.Read(current);

        ref readonly var first = ref node[0];

        if ((first & TypeMask) == LeafType)
        {
            var leafValue = ReadLeaf(node.Slice(PrefixLength), out var existingPath);

            var diffAt = addedPath.FindFirstDifferentNibble(existingPath);

            if (diffAt == addedPath.Length)
            {
                return WriteLeafUpdatable(db, existingPath, value, current);
            }

            if (diffAt > 0)
            {
                // need to create E -> B -> L1, L2
                // 1. extension path will be of length diffAt
                // 2. branch at diffAt index (one forward)
                // 3. leaves
                // do the bottom up

                // 3. leaves first
                var existing = WriteLeaf(db, existingPath.SliceFrom(diffAt + 1), leafValue);
                var added = WriteLeaf(db, addedPath.SliceFrom(diffAt + 1), value);

                // 2. write branch
                Branch branch = default;
                unsafe
                {
                    branch.Branches[existingPath.GetAt(diffAt)] = existing;
                    branch.Branches[addedPath.GetAt(diffAt)] = added;
                }

                var branchId = WriteToDb(branch, db);

                // 3. extension
                var extensionPath = addedPath.SliceTo(diffAt);
                Span<byte> destination = stackalloc byte[Extension.MaxDestinationSize];
                var extension = Extension.WriteTo(extensionPath, branchId, destination);

                return db.TryUpdateOrAdd(current, extension);
            }
            else
            {
                // need to capture values of nibbles as they are overwritten by potential upgradable
                var nibbleExisting = existingPath.FirstNibble;
                var nibbleAdded = addedPath.FirstNibble;

                // build the key for the nested nibble, this one exist and 1lvl deeper is needed
                var @new = WriteLeaf(db, addedPath.SliceFrom(1), value);

                // write existing one, try use updatable
                var oldValue = node.Slice(PrefixLength + existingPath.RawByteLength);
                var @old = WriteLeafUpdatable(db, existingPath.SliceFrom(1), oldValue, current);

                Branch branch = default;
                unsafe
                {
                    branch.Branches[nibbleExisting] = @old;
                    branch.Branches[nibbleAdded] = @new;
                }

                // 1. write branch
                return WriteToDb(branch, db);
            }
        }

        if ((first & TypeMask) == BranchType)
        {
            var newNibble = addedPath.FirstNibble;

            if (Branch.TryFindInFull(node, newNibble, out var found))
            {
                var @new = Set(db, found, addedPath.SliceFrom(1), value);
                Span<byte> copy = stackalloc byte[node.Length];
                node.CopyTo(copy);
                Branch.SetInFull(copy, newNibble, @new);

                return db.TryUpdateOrAdd(current, copy);
            }

            var branch = Branch.Read(node);

            unsafe
            {
                ref var branchNode = ref branch.Branches[newNibble];
                if (branchNode != Null)
                {
                    var @new = Set(db, branchNode, addedPath.SliceFrom(1), value);
                    if (@new == branchNode)
                    {
                        // nothing to update in the branch
                        return current;
                    }

                    // override with the new value
                    branchNode = @new;
                }
                else
                {
                    // not exist yet
                    branchNode = WriteLeaf(db, addedPath.SliceFrom(1), value);
                }
            }

            Span<byte> written = branch.WriteTo(stackalloc byte[Branch.MaxDestinationSize]);

            return db.TryUpdateOrAdd(current, written);
        }

        if ((first & TypeMask) == ExtensionType)
        {
            Extension.Read(node, out var extensionPath, out var jumpTo);

            var diffAt = extensionPath.FindFirstDifferentNibble(addedPath);

            // matches the extension
            if (diffAt == extensionPath.Length)
            {
                // matches extension
                var added = Set(db, jumpTo, addedPath.SliceFrom(diffAt), value);
                Span<byte> extension = stackalloc byte[Extension.MaxDestinationSize];
                extension = Extension.WriteTo(extensionPath, added, extension);

                return db.TryUpdateOrAdd(current, extension);
            }

            // build the key for the new value, one level deeper
            var @new = WriteLeaf(db, addedPath.SliceFrom(diffAt + 1), value);

            Branch branch = default;
            unsafe
            {
                branch.Branches[addedPath.GetAt(diffAt)] = @new;
                branch.Branches[extensionPath.GetAt(diffAt)] = PushExtensionDown(db, extensionPath, jumpTo, diffAt + 1);
            }

            var branchPayload = branch.WriteTo(stackalloc byte[Branch.MaxDestinationSize]);

            if (diffAt == 0)
            {
                // the branch is the first, no additional extension needed in front, just overwrite the current
                return db.TryUpdateOrAdd(current, branchPayload);
            }
            else
            {
                // the branch is in the middle and it needs to have an extension first
                var branchId = db.Write(branchPayload);

                // extension of at least length 1
                Span<byte> extension = stackalloc byte[Extension.MaxDestinationSize];
                extension = Extension.WriteTo(extensionPath.SliceTo(diffAt), branchId, extension);

                return db.TryUpdateOrAdd(current, extension);
            }
        }

        throw new Exception("Type not handled!");
    }

    private static long PushExtensionDown(IStore db, NibblePath extensionPath, long jumpTo, int pushDownBy)
    {
        if (extensionPath.Length == pushDownBy)
        {
            return jumpTo;
        }

        Span<byte> extension = stackalloc byte[Extension.MaxDestinationSize];
        extension = Extension.WriteTo(extensionPath.SliceFrom(pushDownBy), jumpTo, extension);
        return db.Write(extension);
    }

    private static long WriteToDb(in Branch branch, IStore db)
    {
        Span<byte> destination = stackalloc byte[Branch.MaxDestinationSize];
        return db.Write(branch.WriteTo(destination));
    }

    private static long WriteLeaf(IStore db, NibblePath path, ReadOnlySpan<byte> value)
    {
        var length = PrefixLength + path.MaxLength + value.Length;

        Span<byte> destination = stackalloc byte[length];

        destination[0] = LeafType;
        var leftover = path.WriteTo(destination.Slice(1));
        value.CopyTo(leftover);
        return db.Write(destination.Slice(0, length - leftover.Length + value.Length));
    }

    private static long WriteLeafUpdatable(IStore db, NibblePath path, ReadOnlySpan<byte> value, long current)
    {
        var length = PrefixLength + path.MaxLength + value.Length;

        Span<byte> destination = stackalloc byte[length];

        destination[0] = LeafType;
        var leftover = path.WriteTo(destination.Slice(1));
        value.CopyTo(leftover);
        var leaf = destination.Slice(0, length - leftover.Length + value.Length);
        return db.TryUpdateOrAdd(current, leaf);
    }

    private static ReadOnlySpan<byte> ReadLeaf(ReadOnlySpan<byte> leaf, out NibblePath path)
    {
        var value = NibblePath.ReadFrom(leaf, out path);
        return value.Slice(0, ValueLenght);
    }

    public bool TryGet(in ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value) => TryGet(_db, _root, key, out value);

    private static bool TryGet(IDb db, long root, in ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value)
    {
        if (root == Null)
        {
            value = default;
            return false;
        }

        var current = root;

        var keyPath = NibblePath.FromKey(key);

        while (keyPath.Length > 0)
        {
            var node = db.Read(current);
            ref readonly var first = ref node[0];

            NibblePath path;

            if ((first & TypeMask) == LeafType)
            {
                var leaf = node.Slice(PrefixLength);
                value = ReadLeaf(leaf, out path);

                if (path.Equals(path))
                {
                    return true;
                }

                value = default;
                return false;
            }

            if ((first & TypeMask) == BranchType)
            {
                var jump = Branch.Find(node, keyPath.FirstNibble);
                if (jump != Null)
                {
                    keyPath = keyPath.SliceFrom(1);
                    current = jump;
                    continue;
                }

                value = default;
                return false;
            }

            if ((first & TypeMask) == ExtensionType)
            {
                Extension.Read(node, out path, out var jumpTo);
                var diffAt = path.FindFirstDifferentNibble(keyPath);

                // jump only if it consumes the whole path
                if (diffAt == path.Length)
                {
                    keyPath = keyPath.SliceFrom(diffAt);
                    current = jumpTo;
                    continue;
                }

                value = default;
                return false;
            }
        }

        value = default;
        return false;
    }

    public IBatch Begin() => new Batch(this);

    // enum NodeType : byte
    // {
    //     Branch,
    //     Extension,
    //     Leaf
    // }

    struct Branch
    {
        public const int MaxDestinationSize = BranchCount * EntrySize + PrefixLength;
        private const int BranchCount = 16;
        private const int EntrySize = 8;
        private const int Shift = 60;
        private const int Mask = 0xF;
        private const long NodeMask = 0x0FFF_FFFF_FFFF_FFFF;
        private const int BranchMinChildCount = 2;
        private const byte BranchChildCountMask = 0b0000_1111;

        public unsafe fixed long Branches[BranchCount];

        public static unsafe Branch Read(in ReadOnlySpan<byte> source)
        {
            Branch result = default; // zero undefined

            ref var b = ref Unsafe.AsRef(in source[0]);
            var count = (b & BranchChildCountMask) + BranchMinChildCount;

            // consume first
            b = ref Unsafe.Add(ref b, PrefixLength);

            for (var i = 0; i < count; i++)
            {
                var value = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref b, i * EntrySize));
                var node = value & NodeMask;
                var nibble = (byte)((value >> Shift) & Mask);

                result.Branches[nibble] = node;
            }

            return result;
        }

        public static bool TryFindInFull(in ReadOnlySpan<byte> source, byte nibble, out long found)
        {
            ref var b = ref Unsafe.AsRef(in source[0]);
            var count = (b & BranchChildCountMask) + BranchMinChildCount;

            if (count == BranchCount)
            {
                // special case, full branch node, can directly jump as values are always sorted
                var value = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref b, PrefixLength + nibble * EntrySize));
                found = value & NodeMask;
                return true;
            }

            found = default;
            return false;
        }

        public static void SetInFull(Span<byte> copy, byte nibble, long @new)
        {
            ref var b = ref Unsafe.AsRef(in copy[0]);
            var value = @new | ((long)nibble << Shift);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, PrefixLength + nibble * EntrySize), value);
        }

        public static long Find(in ReadOnlySpan<byte> source, byte nibble)
        {
            ref var b = ref Unsafe.AsRef(in source[0]);
            var count = (b & BranchChildCountMask) + BranchMinChildCount;

            if (count == BranchCount)
            {
                // special case, full branch node, can directly jump as values are always sorted
                var value = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref b, PrefixLength + nibble * EntrySize));
                return value & NodeMask;
            }

            // skip prefix
            b = ref Unsafe.Add(ref b, PrefixLength);

            for (var i = 0; i < count; i++)
            {
                var value = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref b, i * EntrySize));
                var actual = (byte)((value >> Shift) & Mask);
                if (actual == nibble)
                {
                    return value & NodeMask;
                }
            }

            return Null;
        }

        [Pure]
        public Span<byte> WriteTo(Span<byte> destination)
        {
            ref var b = ref Unsafe.AsRef(in destination[0]);

            int count = 0;
            for (long i = 0; i < BranchCount; i++)
            {
                unsafe
                {
                    if (Branches[i] != Null)
                    {
                        long value = Branches[i] | (i << Shift);
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, PrefixLength + count * EntrySize), value);
                        count++;
                    }
                }
            }

            b = (byte)(BranchType | (byte)(count - BranchMinChildCount));

            return destination.Slice(0, PrefixLength + EntrySize * count);
        }
    }

    class Extension
    {
        private const int BranchIdSize = 8;
        public const int MaxDestinationSize = PrefixLength + 1 + KeyLenght + BranchIdSize;

        public static Span<byte> WriteTo(NibblePath path, long branchId, Span<byte> destination)
        {
            destination[0] = ExtensionType;
            var leftover = path.WriteTo(destination.Slice(1));
            BinaryPrimitives.WriteInt64LittleEndian(leftover, branchId);
            return destination.Slice(0, destination.Length - leftover.Length + BranchIdSize);
        }

        public static void Read(ReadOnlySpan<byte> source, out NibblePath path, out long jumpTo)
        {
            var leftover = NibblePath.ReadFrom(source.Slice(1), out path);
            jumpTo = BinaryPrimitives.ReadInt64LittleEndian(leftover);
        }
    }

    class Batch : IBatch
    {
        private readonly PaprikaTree _parent;
        private readonly IDb _db;
        private long _root;

        private readonly long _lastFlushTo;
        private readonly Store _store;

        public Batch(PaprikaTree parent)
        {
            _parent = parent;
            _db = parent._db;
            _root = parent._root;

            _store = parent._store;
            _store.EnsureUpdatable();

            _lastFlushTo = parent._lastFlushTo;
        }

        void IBatch.Set(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
            _root = PaprikaTree.Set(_store, _root, NibblePath.FromKey(key, 0), value);
        }

        bool IBatch.TryGet(in ReadOnlySpan<byte> key, out ReadOnlySpan<byte> value) =>
            PaprikaTree.TryGet(_db, _root, key, out value);

        public void Commit(CommitOptions options)
        {
            _parent._root = _root;

            switch (options)
            {
                case CommitOptions.RootOnly:
                    // nothing to do
                    break;
                case CommitOptions.SealUpdatable:
                    _store.Seal();
                    break;
                case CommitOptions.ForceFlush:
                    _store.Seal();
                    _parent._lastFlushTo = _db.NextId - 1;
                    _db.FlushFrom(_lastFlushTo);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options), options, null);
            }
        }
    }

    interface IStore
    {
        public long TryUpdateOrAdd(long current, in Span<byte> written);

        ReadOnlySpan<byte> Read(long id);

        long Write(ReadOnlySpan<byte> payload);
    }

    class Store : IStore
    {
        private const int MaxCachedLength = 256;

        private readonly IDb _db;
        private long _updateFrom;
        private readonly long[] _slots;

        public Store(IDb db)
        {
            _db = db;
            _slots = new long[MaxCachedLength];
        }

        public void EnsureUpdatable()
        {
            if (_updateFrom == long.MaxValue)
            {
                _updateFrom = Math.Min(_db.NextId, _updateFrom);
            }
        }

        public void Seal()
        {
            _updateFrom = long.MaxValue;
            Array.Clear(_slots);
        }

        public long TryUpdateOrAdd(long current, in Span<byte> written)
        {
            var currentNode = _db.Read(current);

            if (current >= _updateFrom)
            {
                if (written.TryCopyTo(currentNode))
                {
                    return current;
                }
            }

            var length = currentNode.Length;

            // the current was not sufficient, cache it for future
            if (Id.Size <= length && length < MaxCachedLength)
            {
                // create a chain, write previous in node, put the current there
                BinaryPrimitives.WriteInt64LittleEndian(currentNode, _slots[length]);
                _slots[length] = current;
            }
            else
            {
                // not cacheable, free it
                _db.Free(current);
            }

            // current is no longer used, try to get a node from cache
            var writtenLength = written.Length;
            ref var slot = ref _slots[writtenLength];

            var nextId = _db.NextId;

            while (slot != Null)
            {
                var currentSlot = slot;
                var reusable = _db.Read(currentSlot);
                var next = BinaryPrimitives.ReadInt64LittleEndian(reusable);
                slot = next;

                // Only reuse slot if in the same file. Reusing a slot from an old file may result in a random access.
                if (Id.IsSameFile(nextId, currentSlot))
                {
                    written.CopyTo(reusable);
                    return currentSlot;
                }
            }

            // all caching failed, just write
            return _db.Write(written);
        }

        public ReadOnlySpan<byte> Read(long id) => _db.Read(id);

        public long Write(ReadOnlySpan<byte> payload) => _db.Write(payload);
    }
}