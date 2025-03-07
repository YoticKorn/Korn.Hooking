﻿using Korn.Utils.Memory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Korn.Hooking
{
    public unsafe class MethodAllocator : IDisposable
    {
        const int RoutineRegionAllocationSize = 0x10000;
        const int ArraysRegionAllocationSize = 0x10000;
        const int IndirectRegionAllocationSize = 0x1000;

        static bool isInitialized;
        static MethodAllocator instance;
        public static MethodAllocator Instance
        {
            get
            {
                if (!isInitialized)
                {
                    isInitialized = true;
                    instance = new MethodAllocator();
                }
                return instance;
            }
        }

        MethodAllocator()
        {
            regionAllocator = new RegionAllocator();
            caveFinder = new CaveFinder();
            routineRegionAllocator = new Routine.Region.Allocator(regionAllocator);
            indirectRegionAllocator = new Indirect.Region.Allocator(regionAllocator, caveFinder);
            linkedArrayAllocator = new LinkedArray.Region.Allocator(regionAllocator);
        }

        RegionAllocator regionAllocator;
        CaveFinder caveFinder;
        Routine.Region.Allocator routineRegionAllocator;
        Indirect.Region.Allocator indirectRegionAllocator;
        LinkedArray.Region.Allocator linkedArrayAllocator;

        public Indirect CreateIndirect(IntPtr nearTo) => indirectRegionAllocator.CreateIndirect(nearTo);

        public Routine CreateRoutine(byte[] routineBytes)
        {
            fixed (byte* routineBytesPointer = routineBytes)
                return CreateRoutine(routineBytesPointer, routineBytes.Length);
        }

        public Routine CreateRoutine(byte* routineBytesPointer, int routineBytesCount) 
            => routineRegionAllocator.CreateRoutine(routineBytesPointer, routineBytesCount);

        public AllocatedRoutine CreateAllocatedRoutine(int initialSize)
        {
            var routine = routineRegionAllocator.CreateRoutine(initialSize);
            var allocatedRoutine = AllocatedRoutine.From(routine);
            return allocatedRoutine;
        }

        public LinkedArray CreateLinkedArray() => CreateLinkedArray(LinkedArray.DEFAULT_NODE_VALUE);

        public LinkedArray CreateLinkedArray(IntPtr startValue)
        {
            var rootNode = linkedArrayAllocator.AllocateNode(startValue);
            var array = new LinkedArray(linkedArrayAllocator, rootNode);
            return array;
        }

        public void Dispose() => regionAllocator.Dispose();

        ~MethodAllocator() => Dispose();

        public class RegionAllocator : IDisposable
        {
            List<MemoryRegion.Allocated> regions = new List<MemoryRegion.Allocated>();

            public MemoryRegion.Allocated AllocateMemory(long size)
            {
                var mbi = MemoryAllocator.Allocate(size);
                var region = MemoryRegion.Allocated.Descriptor.From(&mbi);
                var managedBlob = MemoryRegion.Allocated.From(region);
                regions.Add(managedBlob);

                return managedBlob;
            }

            public MemoryRegion.Allocated AllocateNearMemory(IntPtr address, long size)
            {
                var mbi = MemoryAllocator.AllocateNear(address, size);
                if (!mbi.IsValid)
                    return null;

                var region = MemoryRegion.Allocated.Descriptor.From(&mbi);
                var managedBlob = MemoryRegion.Allocated.From(region);
                regions.Add(managedBlob);

                return managedBlob;
            }

            public void Dispose()
            {
                foreach (var region in regions)
                    region.Dispose();
            }
        }

        public class CaveFinder
        {
            const int MinCaveFreeSize = 0x10;

            List<MemoryRegion.Caved> caves = new List<MemoryRegion.Caved>();

            public MemoryRegion.Caved GetFreeCaveNear(IntPtr address)
            {
                return FindMemoryCave(address);
            }

            MemoryRegion.Caved FindMemoryCave(IntPtr address)
            {
                var mbi = MemoryAllocator.Query(address);

                MemoryRegion.Caved freeCave;
                do
                {
                    freeCave = FindFreeCaveNear(address, &mbi);
                    caves.Add(freeCave);
                }
                while (freeCave.Size < MinCaveFreeSize);

                return freeCave;
            }

            MemoryRegion.Caved FindFreeCaveNear(IntPtr address, MemoryBaseInfo* mbi)
            {
                var cave = FindFreeCaveNearTop(address, mbi);
                if (cave == null)
                    cave = FindFreeCaveNearBot(address, mbi);
                if (cave == null)
                    throw new KornError(
                        "Korn.Hooking.MethodAllocator: " +
                        "There are no free regions or caves to allocate memory for hooking funtionality, the arrow struck Achilles' heel 😞"
                    );

                return cave;
            }

            MemoryRegion.Caved FindFreeCaveNearTop(IntPtr address, MemoryBaseInfo* startMbi)
            {
                if (!IsCaveFound(startMbi) && IsSuitForCave(startMbi))
                    return BuildCaveBlobFromFreeCave(startMbi);

                var mbi = MemoryAllocator.QueryNextTop(startMbi);
                while ((long)startMbi->BaseAddress > 0x10000 && AddressSpaceUtils.IsRegionNearToAddress(&mbi, address))
                {
                    if (!IsCaveFound(&mbi) && IsSuitForCave(&mbi))
                        return BuildCaveBlobFromFreeCave(&mbi);

                    mbi = MemoryAllocator.QueryNextTop(&mbi);
                }

                return null;
            }

            MemoryRegion.Caved FindFreeCaveNearBot(IntPtr address, MemoryBaseInfo* startMbi)
            {
                if (!IsCaveFound(startMbi) && IsSuitForCave(startMbi))
                    return BuildCaveBlobFromFreeCave(startMbi);

                var mbi = MemoryAllocator.QueryNextBot(startMbi);
                while ((long)startMbi->BaseAddress > 0x10000 && AddressSpaceUtils.IsRegionNearToAddress(&mbi, address))
                {
                    if (!IsCaveFound(&mbi) && IsSuitForCave(&mbi))
                        return BuildCaveBlobFromFreeCave(&mbi);

                    mbi = MemoryAllocator.QueryNextBot(&mbi);
                }

                return null;
            }

            MemoryRegion.Caved BuildCaveBlobFromFreeCave(MemoryBaseInfo* mbi)
            {
                mbi->SetProtection(MemoryProtect.ExecuteReadWrite);

                /* // I forgot why I added that code 🥺
                var isExecutable =
                    mbi->Protect.HasFlag(MemoryProtect.Execute) ||
                    mbi->Protect.HasFlag(MemoryProtect.ExecuteRead) || // usual case
                    mbi->Protect.HasFlag(MemoryProtect.ExecuteReadWrite) ||
                    mbi->Protect.HasFlag(MemoryProtect.ExecuteWriteCopy);
                */

                var size = CountLastZeroBytes((IntPtr)((long)mbi->BaseAddress + mbi->RegionSize - 1));
                size -= 8; // prevent the use of memory used by an instruction
                           // size may be negative, this region will be added to the used regions, but will not actually be used

                var start = (long)mbi->BaseAddress + mbi->RegionSize - size;
                return new MemoryRegion.Caved(mbi->BaseAddress, (IntPtr)start, size);

                int CountLastZeroBytes(IntPtr address)
                {
                    var pointer = (byte*)address;
                    while (*pointer-- == 0) ;
                    return (int)((long)address - (long)pointer + 1);
                }
            }

            bool IsSuitForCave(MemoryBaseInfo* mbi) => mbi->Type == MemoryType.Image;

            bool IsCaveFound(MemoryBaseInfo* mbi)
            {
                foreach (var blob in caves)
                    if (blob.RegionBase == mbi->BaseAddress)
                        return true;
                return false;
            }
        }

        public class Indirect : IDisposable
        {
            public Indirect(Region indirectsRegion, IntPtr address) => (IndirectsRegion, Address) = (indirectsRegion, address);

            public Region IndirectsRegion { get; private set; }
            public IntPtr Address { get; private set; }

            public IntPtr* IndirectAddress => (IntPtr*)Address;
            public int IndirectIndex;

            bool disposed;
            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;

                *IndirectAddress = IntPtr.Zero;
                IndirectsRegion.RemoveIndirect(this);
            }

            public class Region : IDisposable
            {
                public Region(MemoryRegion memoryRegion)
                {
                    MemoryRegion = memoryRegion;
                    statusStorage = new StatusStorage(this);
                }

                public MemoryRegion MemoryRegion { get; private set; }
                List<Indirect> indirects = new List<Indirect>();
                StatusStorage statusStorage;

                public Indirect CreateIndirect()
                {
                    var index = statusStorage.GetFreeStatusIndex();
                    if (index == -1)
                        throw new KornError(
                            "Korn.Hooking.MethodAllocator.IndirectsRegion->CreateIndirect: " +
                            "Bad check for free indirect slots. There are no free slots in this region"
                        );

                    var indirect = statusStorage.CreateIndirect(index);
                    indirects.Add(indirect);
                    return indirect;
                }

                public void RemoveIndirect(Indirect indirect)
                {
                    statusStorage.RemoveIndirect(indirect);
                    indirects.Remove(indirect);

                    indirect.Dispose();
                }

                public bool HasFreeIndirectSlot => statusStorage.HasFreeStatusSlot;

                public void Dispose()
                {
                    foreach (var indirect in indirects)
                        indirect.Dispose();
                }

                public class StatusStorage : IDisposable
                {
                    public StatusStorage(Region region)
                    {
                        this.region = region;

                        var bytes = region.MemoryRegion.Size;
                        statusesCount = bytes / sizeof(long);
                        longsCount = (statusesCount + 63) / 64;
                        longs = MemoryEx.Alloc<long>(longsCount);

                        UpdateNoSpaceState();
                    }

                    int statusesCount;
                    Region region;
                    int longsCount;
                    long* longs;

                    public StatusEnum this[int index]
                    {
                        get => (longs[index / 64] & (1L << (index % 64))) == 0 ? StatusEnum.Free : StatusEnum.Reserved;
                        set => longs[index / 64] =
                           value == StatusEnum.Free
                           ? (longs[index / 64] & ~(1L << (index % 64)))
                           : (longs[index / 64] | (1L << (index % 64)));
                    }

                    public StatusEnum GetStatusByAddress(IntPtr address) => this[(int)((long)address - (long)region.MemoryRegion.Address) / sizeof(IntPtr)];
                    public IntPtr GetAddressByIndex(int index) => region.MemoryRegion.Address + index * sizeof(IntPtr);

                    public bool HasFreeStatusSlot;

                    public Indirect CreateIndirect(int index)
                    {
                        var address = GetAddressByIndex(index);
                        var indirect = new Indirect(region, address) { IndirectIndex = index };
                        this[index] = StatusEnum.Reserved;

                        UpdateNoSpaceState();
                        return indirect;
                    }

                    public void RemoveIndirect(Indirect indirect)
                    {
                        var index = indirect.IndirectIndex;
                        this[index] = StatusEnum.Free;
                        indirect.Dispose();

                        UpdateNoSpaceState(false);
                    }

                    void UpdateNoSpaceState() => UpdateNoSpaceState(GetFreeStatusIndex() == -1);
                    void UpdateNoSpaceState(bool noSpace)
                    {
                        HasFreeStatusSlot = !noSpace;
                    }

                    public int GetFreeStatusIndex()
                    {
                        for (var hi = 0; hi < statusesCount / 64; hi++)
                            if (longs[hi] != 1 << 64)
                                for (var li = 0; li < 64; li++)
                                {
                                    var index = hi * 64 + li;
                                    if (this[index] == StatusEnum.Free)
                                        return index;
                                }

                        for (var index = statusesCount / 64 * 64; index < statusesCount; index++)
                            if (this[index] == StatusEnum.Free)
                                return index;

                        return -1;
                    }

                    public void Dispose() => MemoryEx.Free(longs);

                    public enum StatusEnum : byte
                    {
                        Free = 0,
                        Reserved = 1 // or any other state
                    }
                }

                public class Allocator
                {
                    public Allocator(RegionAllocator regionAllocator, CaveFinder caveFinder)
                    {
                        this.regionAllocator = regionAllocator;
                        this.caveFinder = caveFinder;
                    }

                    List<Region> regions = new List<Region>();
                    RegionAllocator regionAllocator;
                    CaveFinder caveFinder;

                    public Indirect CreateIndirect(IntPtr nearTo)
                    {
                        var region = GetIndirectsRegion(nearTo);
                        var indirect = region.CreateIndirect();
                        return indirect;
                    }

                    Region GetIndirectsRegion(IntPtr nearTo)
                    {
                        while (true)
                        {
                            foreach (var region in regions)
                            {
                                if (region.MemoryRegion.IsNearTo(nearTo))
                                    if (region.HasFreeIndirectSlot)
                                        return region;
                            }

                            CreateIndirectRegion(nearTo);
                        }
                    }

                    Region CreateIndirectRegion(IntPtr nearTo)
                    {
                        var memoryRegion = FindMemoryRegion();
                        var region = new Region(memoryRegion);
                        regions.Add(region);
                        return region;

                        MemoryRegion FindMemoryRegion()
                        {
                            var allocatedRegion = regionAllocator.AllocateNearMemory(nearTo, IndirectRegionAllocationSize);
                            if (allocatedRegion != null)
                                return allocatedRegion;

                            var caveRegion = caveFinder.GetFreeCaveNear(nearTo);
                            return caveRegion;
                        }
                    }
                }
            }
        }

        // when using linked array it must have at least 1 node, otherwise its work is considered invalid
        public class LinkedArray : IDisposable
        {
            public static readonly IntPtr DEFAULT_NODE_VALUE = (IntPtr)1;

            public LinkedArray(Region.Allocator alloctor, Node* rootNode)
            {
                this.alloctor = alloctor;
                RootNode = rootNode;
                LastNode = rootNode;
            }

            Region.Allocator alloctor;
            public Node* RootNode;
            public Node* LastNode;

            public Node* AddNode(IntPtr address)
            {
                if (RootNode->Value == DEFAULT_NODE_VALUE)
                {
                    RootNode->Value = address;
                    return RootNode;
                }

                var node = alloctor.AllocateNode(address);
                LastNode = LastNode->Next = node;
                return node;
            }

            public void RemoveNode(Node* node)
            {
                if (RootNode == node)
                {                    
                    if (RootNode->Next->IsValid)
                    {
                        var removedNode = RootNode;
                        RootNode = RootNode->Next;
                        removedNode->DestroyNode();
                    }
                    else
                    {
                        RootNode->Value = DEFAULT_NODE_VALUE;
                    }
                }
                else
                {
                    var previousNode = RootNode;
                    var currentNode = RootNode;
                    do
                    {
                        currentNode = currentNode->Next;
                        if (currentNode == node)
                        {
                            previousNode->Next = currentNode->Next;
                            currentNode->DestroyNode();
                            break;
                        }
                        previousNode = currentNode;
                    }
                    while (currentNode->IsValid);
                }
            }

            public void Dispose() => RootNode->DestroySequence();

            public struct Node
            {
                public IntPtr Value;
                public Node* Next;

                public bool IsValid => !(Value == IntPtr.Zero && Next == null);
                public bool HasNext => Next != null;

                public void DestroySequence()
                {
                    fixed (Node* self = &this)
                    {
                        var next = self;

                        do
                        {
                            next->Value = IntPtr.Zero;
                            var nextNext = next->Next;
                            next = nextNext;
                        }
                        while (next != null);
                    }
                }

                public void DestroyNode()
                {
                    Value = IntPtr.Zero;
                    Next = null;
                }
            }

            public class Region : IDisposable
            {
                public Region(MemoryRegion.Allocated allocatedMemory)
                    => AllocatedMemory = allocatedMemory;

                public MemoryRegion.Allocated AllocatedMemory { get; private set; }
                public bool HasSpace { get; private set; } = true;

                void UpdateHasSpace()
                {
                    var pointer = (Node*)AllocatedMemory.Address;
                    var count = AllocatedMemory.Size / sizeof(LinkedArray.Node);

                    for (var i = 0; i < count; i++)
                        if (!pointer->IsValid)
                        {
                            HasSpace = true;
                            return;
                        }
                        else pointer++;

                    HasSpace = false;
                }

                public Node* AllocateNode(IntPtr value)
                {
                    lock (AllocatedMemory)
                    {
                        if (!HasSpace)
                            throw new KornException(
                                "Korn.Hooking.MethodAllocator.LinkedArraysRegion->AllocateNode",
                                "Bad check for free node slots. There are no free slots in this region"
                            );

                        var node = FindFreeNode();
                        node->Value = value;

                        UpdateHasSpace();
                        return node;
                    }
                }

                Node* FindFreeNode()
                {
                    var pointer = (Node*)AllocatedMemory.Address;
                    var count = AllocatedMemory.Size / sizeof(Node);
                    for (var i = 0; i < count; i++)
                        if (!pointer->IsValid)
                            return pointer;
                        else pointer++;

                    throw new KornException(
                        "Korn.Hooking.MethodAllocator.LinkedArraysRegion->AllocateNode",
                        "Bad check for free indirect node. There are no free slots in this region"
                    );
                }

                public void Dispose() => AllocatedMemory.Dispose();

                public class Allocator : IDisposable
                {
                    public Allocator(RegionAllocator regionAllocator)
                    {
                        this.regionAllocator = regionAllocator;
                    }

                    List<LinkedArray> arrays = new List<LinkedArray>();

                    RegionAllocator regionAllocator;
                    List<Region> regions = new List<Region>();

                    public LinkedArray CreateArray(IntPtr startValue)
                    {
                        var region = GetFreeRegion();
                        var node = region.AllocateNode(startValue);

                        var array = new LinkedArray(this, node);
                        arrays.Add(array);
                        return array;
                    }

                    public Node* AllocateNode(IntPtr value)
                    {
                        var region = GetFreeRegion();
                        var node = region.AllocateNode(value);

                        return node;
                    }

                    Region GetFreeRegion()
                    {
                        foreach (var region in regions)
                            if (region.HasSpace)
                                return region;

                        return AllocateRegion();
                    }

                    Region AllocateRegion()
                    {
                        var memoryRegion = regionAllocator.AllocateMemory(ArraysRegionAllocationSize);
                        var region = new Region(memoryRegion);
                        regions.Add(region);
                        return region;
                    }

                    public void Dispose()
                    {
                        foreach (var region in regions)
                            region.Dispose();
                    }
                }
            }
        }

        public class AllocatedRoutine : Routine
        {
            public AllocatedRoutine(Region routinesRegion, int regionOffset, IntPtr address, int size)
                : base(routinesRegion, regionOffset, address, size) { }

            // to prevent GC clear original routine
            Routine originalRoutine;

            public bool IsSizeFixed { get; private set; }

            public void FixSize(int size)
            {
                if (IsSizeFixed)
                    throw new KornException(
                        "Korn.Hooking.MethodAllocator.AllocatedRoutine->FixSize: ",
                        "Routine size already fixed"
                    );

                IsSizeFixed = true;
                                
                originalRoutine.Size = Size = size;
            }

            public static AllocatedRoutine From(Routine routine) => new AllocatedRoutine(routine.RoutinesRegion, routine.RegionOffset, routine.Address, routine.Size)
            {
                originalRoutine = routine
            };
        }

        public unsafe class Routine : IDisposable
        {
            public Routine(Region routinesRegion, int regionOffset, IntPtr address, int size)
                => (RoutinesRegion, RegionOffset, Address, Size) = (routinesRegion, regionOffset, address, size);

            public Region RoutinesRegion { get; private set; }
            public int RegionOffset { get; private set; }
            public IntPtr Address { get; private set; }
            public int Size { get; set; }

            bool disposed;
            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;

                Utils.Memory.MemoryEx.Zero(Address, Size);
                RoutinesRegion.RemoveRoutine(this);
            }

            public class Region : IDisposable
            {
                public Region(MemoryRegion.Allocated allocatedMemory) => AllocatedMemory = allocatedMemory;

                public MemoryRegion.Allocated AllocatedMemory { get; private set; }
                List<Routine> routines = new List<Routine>();

                public Routine AddRoutine(byte[] routineBytes)
                    => AddRoutine(GetOffsetForInsertRoutine(routineBytes.Length), routineBytes);

                public Routine AddRoutine(int offset, byte[] routineBytes)
                {
                    fixed (byte* routineBytesPointer = routineBytes)
                        return AddRoutine(offset, routineBytesPointer, routineBytes.Length);
                }

                public Routine AddRoutine(byte* routineBytes, int routinLength)
                    => AddRoutine(GetOffsetForInsertRoutine(routinLength), routineBytes, routinLength);

                public Routine AddRoutine(int offset, byte* routineBytes, int routineLength)
                {
                    var address = AllocatedMemory.Address + offset;
                    Buffer.MemoryCopy(routineBytes, (byte*)address, routineLength, routineLength);

                    var routine = new Routine(this, offset, address, routineLength);
                    AddRoutine(routine);
                    return routine;
                }

                public Routine AddRoutine(int routinLength)
                    => AddRoutine(GetOffsetForInsertRoutine(routinLength), routinLength);

                public Routine AddRoutine(int offset, int routineLength)
                {
                    var address = AllocatedMemory.Address + offset;
                    var routine = new Routine(this, offset, address, routineLength);
                    AddRoutine(routine);
                    return routine;
                }

                // adds a routine to the list, so that the offsets are in order
                public void AddRoutine(Routine routine)
                {
                    int index = 0;
                    for (var i = 0; i < routines.Count; i++)
                        if (routines[i].RegionOffset > routine.RegionOffset)
                            index = i;

                    routines.Insert(index, routine);
                }

                public void RemoveRoutine(Routine routine)
                {
                    var removed = routines.Remove(routine);
                    if (!removed)
                        return;

                    routine.Dispose();
                }

                public bool HasSpaceForNewRoutine(int requestedSize) => GetOffsetForInsertRoutine(requestedSize) != -1;

                public int GetOffsetForInsertRoutine(int size)
                {
                    var regionSize = AllocatedMemory.Size;

                    if (routines.Count == 0)
                        return regionSize > size ? 0 : -1;

                    var firstRoutine = routines[0];
                    if (firstRoutine.RegionOffset >= size)
                        return 0;

                    var lastRoutine = routines.Last();
                    if (lastRoutine.RegionOffset + lastRoutine.Size + size < regionSize)
                        return lastRoutine.RegionOffset + lastRoutine.Size;

                    for (var i = 0; i < routines.Count - 1; i++)
                    {
                        var currentRoutine = routines[i];
                        var nextRoutine = routines[i + 1];
                        var nextHRoutineOffset = currentRoutine.RegionOffset + currentRoutine.Size + size;
                        if (nextHRoutineOffset <= nextRoutine.RegionOffset)
                            return nextHRoutineOffset;
                    }

                    return -1;
                }

                public void Dispose() => AllocatedMemory.Dispose();

                public class Allocator
                {
                    public Allocator(RegionAllocator regionAllocator)
                    {
                        this.regionAllocator = regionAllocator;
                    }

                    List<Region> regions = new List<Region>();
                    RegionAllocator regionAllocator;

                    public Routine CreateRoutine(byte[] routineBytes)
                    {
                        fixed (byte* routineBytesPointer = routineBytes)
                            return CreateRoutine(routineBytesPointer, routineBytes.Length);
                    }

                    public Routine CreateRoutine(byte* routineBytes, int routineSize)
                    {
                        var region = GetRoutinesRegion(routineSize);
                        return region.AddRoutine(routineBytes, routineSize);
                    }

                    public Routine CreateRoutine(int size)
                    {
                        var region = GetRoutinesRegion(size);
                        return region.AddRoutine(size);
                    }

                    Region GetRoutinesRegion(int requestedSize)
                    {
                        foreach (var region in regions)
                            if (region.HasSpaceForNewRoutine(requestedSize))
                                return region;

                        return CreateRoutinesRegion();
                    }

                    Region CreateRoutinesRegion()
                    {
                        var memoryRegion = regionAllocator.AllocateMemory(RoutineRegionAllocationSize);

                        var region = new Region(memoryRegion);
                        regions.Add(region);
                        return region;
                    }
                }
            }
        }        

        public abstract class MemoryRegion
        {
            public MemoryRegion(IntPtr address, int size) => (Address, Size) = (address, size);

            public IntPtr Address { get; private set; }
            public int Size { get; private set; }

            public bool IsNearTo(IntPtr address) => AddressSpaceUtils.IsRegionNearToAddress(Address, Size, address);

            public class Caved : MemoryRegion
            {
                public readonly IntPtr RegionBase;

                public Caved(IntPtr regionBase, IntPtr address, int size) : base(address, size)
                    => RegionBase = regionBase;
            }

            public class Allocated : MemoryRegion, IDisposable
            {
                Allocated(Descriptor regionDescriptor) : base(regionDescriptor.Address, regionDescriptor.Size)
                    => RegionDescriptor = regionDescriptor;

                public Descriptor RegionDescriptor;

                public void Free() => MemoryAllocator.Free(RegionDescriptor.Address, RegionDescriptor.Size);

                public static Allocated From(Descriptor regionDescriptor) => new Allocated(regionDescriptor);
                public static Allocated From(MemoryBaseInfo* mbi) => From(Descriptor.From(mbi));

                bool disposed;
                public void Dispose()
                {
                    if (disposed)
                        return;
                    disposed = true;

                    Free();
                }
                ~Allocated() => Dispose();

                public unsafe struct Descriptor
                {
                    Descriptor(IntPtr address, int size) => (Address, Size) = (address, size);

                    public readonly IntPtr Address;
                    public readonly int Size;

                    public static Descriptor From(MemoryBaseInfo* mbi) => new Descriptor(mbi->BaseAddress, (int)mbi->RegionSize);
                }
            }
        }
    }

    public unsafe static class AddressSpaceUtils
    {
        public static bool IsRegionNearToAddress(MemoryBaseInfo* mbi, IntPtr nearTo)
            => IsRegionNearToAddress(mbi->BaseAddress, mbi->RegionSize, nearTo);

        public static bool IsRegionNearToAddress(IntPtr regionAddress, long regionSize, IntPtr nearTo) => 
            (long)regionAddress > (long)nearTo
            ? (long)regionAddress - (long)nearTo - regionSize < 0x7FFFFFF0
            : (long)nearTo - (long)regionAddress < 0x7FFFFFF0;
    }
}