# :hot_pepper: Paprika

Paprika is a custom database implementation for `State` and `Storage` trees of Ethereum. This document covers the main design ideas, inspirations, and most important implementation details.

## Design

Paprika is a database that uses [memory-mapped files](https://en.wikipedia.org/wiki/Memory-mapped_file). To handle concurrency, [Copy on Write](https://en.wikipedia.org/wiki/Copy-on-write) is used. This allows multiple concurrent readers to cooperate in a full lock-free manner and a single writer that runs the current transaction. In that manner, it's heavily inspired by [LMBD](https://github.com/LMDB/lmdb). Paprika uses 4kb pages.

### Reorganizations handling

The foundation of any blockchain is a single list of blocks, a chain. The _canonical chain_ is the chain that is perceived to be the main chain. Due to the nature of the network, the canonical chain changes in time. If a block at a given position in the list is replaced by another, the effect is called a _reorganization_. The usual process of handling a reorganization is undoing recent N operations, until the youngest block was changed, and applying new blocks. From the database perspective, it means that it needs to be able to undo an arbitrary number of committed blocks.

In Paprika, it's handled by specifying the _history depth_ that keeps the block information for at least _history depth_. This allows undoing blocks till the reorganization boundary and applying blocks from the canonical chain. Paprika internally keeps the _history \_depth_ of the last root pages. If a reorganization is detected and the state needs to be reverted to any of the last _history depth_, it copies the root page with all the metadata as current and allows to re-build the state on top of it. Due to its internal page reuse, as the snapshot of the past is restored, it also logically undoes all the page writes, etc. It works as a clock that is turned back by a given amount of time (blocks).

### Merkle construct

Paprika focuses on delivering fast reads and writes, keeping the information about `Merkle` construct in separate pages. This allows to load and access of pages with data in a fast manner, leaving the update of the root hash (and other nodes) for the transaction commit. It also allows choosing which Keccaks are memoized. For example, the implementor may choose to store every other level of Keccaks.

### ACID

Paprika allows 2 modes of commits:

1. `FlushDataOnly`
1. `FlushDataAndRoot`

`FlushDataOnly` allows flushing the data on disk but keeps the root page in memory only. The root page pointing to the recent changes will be flushed the next time. This effectively means that the database preserves the semantics of **Atomic** but is not durable as there's always one write hanging in the air. This mode should be used for greater throughput as it requires only one flushing of the underlying file (`MSYNC` + `FSYNC`).

`FlushDataAndRoot` flushes both, all the data pages and the root page. This mode is not only **Atomic** but also **Durable** as after the commit, the database is fully stored on the disk. This requires two calls to `MSYNC` and two calls to `FSYNC` though, which is a lot heavier than the previous mode. `FlushDataOnly` should be the default one that is used and `FlushDataAndRoot` should be used mostly when closing the database.

### Memory-mapped caveats

It's worth mentioning that memory-mapped files were lately critiqued by [Andy Pavlo and the team](https://db.cs.cmu.edu/mmap-cidr2022/). The paper's outcome is that any significant DBMS system will need to provide buffer pooling management and `mmap` is not the right tool to build a database. At the moment of writing the decision is to keep the codebase small and use `mmap` and later, if performance is shown to be degrading, migrate.

## Implementation

The following part provides implementation-related details, that might be helpful when working on or amending the Paprika ~sauce~ source code.

### Allocations, classes, and objects

Whenever possible initialization should be skipped using `[SkipLocalsInit]` or `Unsafe.` methods.

If a `class` is declared instead of a `struct`, it should be allocated very infrequently. A good example is a transaction or a database that is allocated not that often. When designing constructs created often, like `Keccak` or a `Page`, using the class and allocating an object should be the last resort.

### NibblePath

`NibblePath` is a custom implementation of the path of nibbles, needed to traverse the Trie of Ethereum. The structure allocates no memory and uses `ref` semantics to effectively traverse the path. It also allows for efficient comparisons and slicing. As it's `ref` based, it can be built on top of `Span<byte>`.

### Keccak and RLP encoding

Paprika provides custom implementations for some of the operations involving `Keccak` and `RLP` encoding. As the Merkle construct is based on `Keccak` values calculated for Trie nodes that are RLP encoded, Paprika provides combined methods, that first perform the RLP encoding and then calculate the Keccak. This allows an efficient, allocation-free implementation. No `RLP` is used for storing or retrieving data. `RLP` is only used to match the requirements of the Merkle construct.

### Const constructs and \[StructLayout\]

Whenever a value type needs to be preserved, it's worth considering the usage of `[StructLayout]`, which specifies the placement of the fields. Additionally, the usage of a `Size` const can be of tremendous help. It allows having all the sizes calculated on the step of compilation. It also allows skipping to copy lengths and other unneeded information and replace it with information known upfront.

```csharp
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = Size)]
public struct PageHeader
{
    public const int Size = sizeof(long);
}
```

### Pages

Paprika uses paged-based addressing. Every page is 4kb. The size of the page is constant and cannot be changed. This makes pages lighter as they do not need to pass the information about their size. The page address, called `DbAddress`, can be encoded within the first 3 bytes of an `uint` if the database size is smaller than 64GB. This leaves one byte for addressing within the page without blowing the address beyond 4 bytes.

There are different types of pages:

1. `RootPage`
1. `AbandonedPage`
1. `DataPage`

#### Root page

The `RootPage` is a page responsible for holding all the metadata information needed to query and amend data. It consists of:

1. batch id - a monotonically increasing number, identifying each batch write to the database that happened
1. block information including its number and the hash
1. abandoned pages

The last one is a collection of `DbAddress` pointing to the `Abandoned Pages`. As the amount of metadata is not big, one root page can store over 1 thousand addresses of the abandoned pages.

#### Abandoned Page

An abandoned page is a page storing information about pages that were abandoned during a given batch. Let's describe what abandonment means. When a page is COWed, the original copy should be maintained for the readers. After a given period of time, defined by the reorganization max depth, the page should be reused to not blow up the database. That is why `AbandonedPage` memoizes the batch at which it was created. Whenever a new page is requested, the allocator checks the list of unused pages (pages that were abandoned that passed the threshold of max reorg depth. If there are some, the page can be reused.

As each `AbandonedPage` can store ~1,000 pages, in cases of big updates, several pages are required to store the addresses of all the abandoned pages. As they share the batch number in which they are abandoned, a linked list is used to occupy only a single slot in the `AbandonedPages` of the `RootPage`. The unlinking and proper management of the list is left up to the allocator that updates slots in the `RootPage` accordingly.

The biggest caveat is where to store the `AbandonedPage`. The same mechanism is used for them as for other pages. This means, that when a block is committed, to store an `AbandonedPage`, the batch needs to allocate (which may get it from the pool) a page and copy to it.

#### Data Page

A data page is responsible for storing data, meaning a map from the `Keccak`->`Account`. The data page tries to store as much data as possible inline. If there's no more space, left, it selects a bucket, defined by a nibble. The one with the highest count of items is flushed as a separate page and a pointer to that page is stored in the bucket of the original `DataPage`. This is a bit different approach from using page splits. Instead of splitting the page and updating the parent, the page can flush down some of its data, leaving more space for the new. A single `PageData` can hold roughly 31-60 accounts. This divided by the count of nibbles 16 gives a rough minimal estimate of how much flushing down can save memory (at least 2 frames).

### Page design in C\#

Pages are designed as value types that provide a wrapper around a raw memory pointer. The underlying pointer does not change, so pages can be implemented as `readonly unsafe struct` like in the following example.

```csharp
public readonly unsafe struct Page
{
    private readonly byte* _ptr;
    public Page(byte* ptr) => _ptr = ptr;
}
```

The differentiation of the pages and accessible methods is provided by poor man's derivation - composition. The following snippet presents a data page, that wraps around the generic `Page`.

```csharp
public readonly unsafe struct DataPage
{
    private readonly Page _page;
    public DataPage(Page root) => _page = root;
}
```

The following ASCII Art should provide a better picture of the composition approach

```bash
                Page Header Size, the same for all pages
  start, 0         │
         |         │
         ▼         ▼
         ┌─────────┬────────────────────────────────────────────────────────────────────────────┐
         │ Page    │                                                                            │
Page 4kb │ Header  │                                                                            │
         │         │                                                                            │
         ├─────────┼────────────────────────────────────────────────────────────────────────────┤
         │ Page    │                                                                            │
DataPage │ Header  │                 Payload of the DataPage                                    │
         │         │                                                                            │
         └─────────┴────────────────────────────────────────────────────────────────────────────┘
         │ Page    │                                                                            │
Abandoned│ Header  │                 Payload of the AbandonedPage                               │
         │         │                                                                            │
         └─────────┴────────────────────────────────────────────────────────────────────────────┘
              ▲
              │
              │
              │
          Page Header
          is shared by
          all the pages
```

As fields are located in the same place (`DataPage` wraps `Page` that wraps `byte*`) and all the pages are a size of a `byte*`. To implement the shared functionality, a markup interface `IPage` is used with some extension methods. Again, as pages have the data in the same place they can be cast with the help of `Unsafe`.

```csharp
// The markup interface IPage implemented
public readonly unsafe struct DataPage : IPage
{
    private readonly Page _page;
}

public static class PageExtensions
{
    // The ref to the page is cast to Page, as they underneath are nothing more than a byte* wrappers
    public static void CopyTo<TPage>(this TPage page, TPage destination) where TPage : unmanaged, IPage =>
        Unsafe.As<TPage, Page>(ref page).Span.CopyTo(Unsafe.As<TPage, Page>(ref destination).Span);
}
```

#### Page number

As each page is a wrapper for a pointer. It contains no information about the page number. The page number can be retrieved from the database though, that provides it with the following calculation:

```csharp
private DbAddress GetAddress(in Page page)
{
    return DbAddress.Page((uint)(Unsafe
        .ByteOffset(ref Unsafe.AsRef<byte>(Ptr), ref Unsafe.AsRef<byte>(page.Raw.ToPointer()))
        .ToInt64() / Page.PageSize));
}
```

#### Page headers

As pages may differ by how they use 4kb of provided memory, they need to share some amount of data that can be used to:

1. differentiate the type of the page
2. memoize the last time when the page was written to

The 1st point, the type differentiation, can be addressed either by storying the page type or reasoning about the page place where the page is used. For example, if a page is one of the N pages that support reorganizations, it must be a `RootPage`. Whenever the information can be reasoned out of the context, the type won't be stored to save some memory.

The 2nd point that covers storing important information is stored at the shared page header. The shared `PageHeader` is an amount of memory that is coherent across all the pages. Again, the memory size is const and C\# `const` constructs are leveraged to have it calculated by the compiler and not to deal with them in the runtime.

```csharp
/// <summary>
/// The header shared across all the pages.
/// </summary>
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = Size)]
public struct PageHeader
{
    public const int Size = sizeof(ulong);

    /// <summary>
    /// The id of the last batch that wrote to this page.
    /// </summary>
    [FieldOffset(0)]
    public uint BatchId;

    [FieldOffset(4)]
    public uint Reserved; // for not it's just alignment
}
```

#### FixedMap

Values stored in Paprika fall into one of several categories, like EOA or contract data. On top of that, as different values are encoded with different lengths, it was proved that a fixed frame approach does not work well. What Paprika uses is a modified pattern of the slot array, used by major players in the world of B+ oriented databases (see: [PostgreSQL page layout](https://www.postgresql.org/docs/current/storage-page-layout.html#STORAGE-PAGE-LAYOUT-FIGURE)). How it works then?

The slot array pattern uses a fixed-size buffer that is provided within the page. It allocates chunks of it from two directions:

1. from `0` forward
2. from the end downward

The first direction, from `0` is used for fixed-size structures that represent slots. Each slot has some metadata, including the most important one, the offset to the start of data. The direction from the end is used to store var length payloads. Paprika diverges from the usual slot array though. The slot array assumes that it's up to the higher level to map the slot identifiers to keys. What the page provides is just a container for tuples that stores them and maps them to the `CTID`s (see: [PostgreSQL system columns](https://www.postgresql.org/docs/current/ddl-system-columns.html)). How Paprika uses this approach

In Paprika, each page level represents a cutoff in the nibble path to make it aligned to the Merkle construct. The key management could be extracted out of the `FixedMap` component, but it would make it less self-contained. `FixedMap` then provides `TrySet` and `TryGet` methods that accept nibble paths. This impacts the design of the slot, which is as follows:

```csharp
[StructLayout(LayoutKind.Explicit, Size = Size)]
private struct Slot
{
    public const int Size = 4;

    // The address of this item.
    public ushort ItemAddress { /* bitwise magic */ }

    // Whether the slot is deleted
    public bool IsDeleted { /* bitwise magic */ }

    [FieldOffset(0)] private ushort Raw;

    // First 4 nibbles extracted as ushort.
    [FieldOffset(2)] public ushort Prefix;
}
```

The slot is 4 bytes long. It extracts 4 first nibbles as a prefix for fast comparisons. It has a pointer to the item. The length of the item is included in the encoded part. The drawback of this design is a linear search across all the slots when an item must be found. At the same time, usually, there will be no more than 100 items, which gives 400 bytes, which should be ok-ish with modern processors. The code is marked with an optimization opportunity.

With this, the `FixedMap` memory representation looks like the following.

```bash
┌───────────────┬───────┬───────┬───────────────────────────────┐
│HEADER         │Slot 0 │Slot 1 │                               │
│               │       │       │                               │
│High           │Prefix │Prefix │                               │
│Low            │Addr   │Addr   │ ► ► ►                         │
│Deleted        │   │   │   │   │                               │
│               │   │   │   │   │                               │
├───────────────┴───┼───┴───┼───┘                               │
│                   │       │                                   │
│                ┌──┼───────┘                                   │
│                │  │                                           │
│                │  │                                           │
│                │  └──────────┐                                │
│                │             │                                │
│                ▼             ▼                                │
│                ┌───┬─────────┬───┬────────────────────────────┤
│                │ L │         │ L │                            │
│                │ E │         │ E │                            │
│          ◄ ◄ ◄ │ N │  DATA   │ N │            DATA            │
│                │   │for slot1│   │          for slot 0        │
│                │   │         │   │                            │
└────────────────┴───┴─────────┴───┴────────────────────────────┘
```

The `FixedMap` can wrap an arbitrary span of memory so it can be used for any page that wants to store data by key.

## Learning materials

1. PostgreSQL
   1. [page layout docs](https://www.postgresql.org/docs/current/storage-page-layout.html)
   1. [bufapge.c implementation](https://github.com/postgres/postgres/blob/master/src/backend/storage/page/bufpage.c)
   1. [hio.c and bufpage usage](https://github.com/postgres/postgres/blob/master/src/backend/access/heap/hio.c)
1. Database Storage lectures by Andy Pavlo from CMU Intro to Database Systems / Fall 2022:
   1. Database Storage, pt. 1 https://www.youtube.com/watch?v=df-l2PxUidI
   1. Database Storage, pt. 2 https://www.youtube.com/watch?v=2HtfGdsrwqA
1. LMBD
   1. Howard Chu - LMDB [The Databaseology Lectures - CMU Fall 2015](https://www.youtube.com/watch?v=tEa5sAh-kVk)
   1. The main file of LMDB [mdb.c](https://github.com/LMDB/lmdb/blob/mdb.master/libraries/liblmdb/mdb.c)