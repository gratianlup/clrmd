﻿using Microsoft.Diagnostics.Runtime.MacOS.Structs;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Microsoft.Diagnostics.Runtime.MacOS
{
    internal sealed unsafe class MachOModule
    {
        private readonly MachOCoreDump _parent;

        public ulong BaseAddress { get; }
        public ulong ImageSize { get; }
        public string FileName { get; }
        public ulong LoadBias { get; }
        public ImmutableArray<byte> BuildId { get; }

        private readonly MachHeader64 _header;
        private readonly SymtabLoadCommand _symtab;
        private readonly DysymtabLoadCommand _dysymtab;
        private readonly MachOSegment[] _segments;
        private readonly ulong _stringTableAddress;
        private volatile NList64[]? _symTable;

        internal MachOModule(MachOCoreDump parent, ulong address, string path)
            :this(parent, parent.ReadMemory<MachHeader64>(address), address, path)
        {
        }

        internal MachOModule(MachOCoreDump parent, in MachHeader64 header, ulong address, string path)
        {
            BaseAddress = address;
            FileName = path;
            _parent = parent;
            _header = header;

            if (header.Magic != MachHeader64.Magic64)
                throw new InvalidDataException($"Module at {address:x} does not contain the expected Mach-O header.");

            // Since MachO segments are not contiguous the image size is just the headers/commands
            ImageSize = MachHeader64.Size + _header.SizeOfCommands;

            List<MachOSegment> segments = new List<MachOSegment>((int)_header.NumberCommands);

            uint offset = (uint)sizeof(MachHeader64);
            for (int i = 0; i < _header.NumberCommands; i++)
            {
                ulong cmdAddress = BaseAddress + offset;
                LoadCommandHeader cmd = _parent.ReadMemory<LoadCommandHeader>(cmdAddress);

                switch (cmd.Kind)
                {
                    case LoadCommandType.Segment64:
                        Segment64LoadCommand seg64LoadCmd = _parent.ReadMemory<Segment64LoadCommand>(cmdAddress + (uint)sizeof(LoadCommandHeader));
                        segments.Add(new MachOSegment(seg64LoadCmd));
                        if (seg64LoadCmd.FileOffset == 0 && seg64LoadCmd.FileSize > 0)
                        {
                            LoadBias = BaseAddress - seg64LoadCmd.VMAddr;
                        }
                        break;

                    case LoadCommandType.SymTab:
                        _symtab = _parent.ReadMemory<SymtabLoadCommand>(cmdAddress);
                        break;

                    case LoadCommandType.DysymTab:
                        _dysymtab = _parent.ReadMemory<DysymtabLoadCommand>(cmdAddress);
                        break;

                    case LoadCommandType.Uuid:
                        var uuid = _parent.ReadMemory<UuidLoadCommand>(cmdAddress);
                        if (uuid.Header.Kind == LoadCommandType.Uuid)
                        {
                            BuildId = uuid.BuildId.ToImmutableArray();
                        }
                        break;
                }

                offset += cmd.Size;
            }

            segments.Sort((x, y) => x.Address.CompareTo(y.Address));
            _segments = segments.ToArray();

            if (_symtab.StrOff != 0)
                _stringTableAddress = GetAddressFromFileOffset(_symtab.StrOff);
        }

        public bool TryLookupSymbol(string symbol, out ulong address)
        {
            if (symbol is null)
                throw new ArgumentNullException(nameof(symbol));

            if (_stringTableAddress != 0)
            {
                // First, search just the "external" export symbols 
                if (TryLookupSymbol(_dysymtab.iextdefsym, _dysymtab.nextdefsym, symbol, out address))
                {
                    return true;
                }

                // If not found in external symbols, search all of them
                if (TryLookupSymbol(0, _symtab.NSyms, symbol, out address))
                {
                    return true;
                }
            }

            address = 0;
            return false;
        }

        private bool TryLookupSymbol(uint start, uint nsyms, string symbol, out ulong address)
        {
            address = 0;

            NList64[]? symTable = ReadSymbolTable();
            if (symTable is null)
                return false;

            for (uint i = 0; i < nsyms && start + i < symTable.Length; i++)
            {
                string name = GetSymbolName(symTable[start + i], symbol.Length + 1);
                if (name.Length > 0)
                {
                    // Skip the leading underscores to match Linux externs
                    if (name[0] == '_')
                    {
                        name = name.Substring(1);
                    }
                    if (name == symbol)
                    {
                        address = BaseAddress + symTable[start + i].n_value;
                        return true;
                    }
                }
            }

            return false;
        }

        private string GetSymbolName(NList64 tableEntry, int max)
        {
            ulong nameOffset = _stringTableAddress + tableEntry.n_strx;
            return _parent.ReadAscii(nameOffset, max);
        }

        private NList64[]? ReadSymbolTable()
        {
            if (_symTable != null)
                return _symTable;

            if (_dysymtab.Header.Kind != LoadCommandType.DysymTab || _symtab.Header.Kind != LoadCommandType.SymTab)
                return null;

            ulong symbolTableAddress = GetAddressFromFileOffset(_symtab.SymOff);
            NList64[] symTable = new NList64[_symtab.NSyms];

            int count;
            fixed (NList64* ptr = symTable)
                count = _parent.ReadMemory(symbolTableAddress, new Span<byte>(ptr, symTable.Length * sizeof(NList64))) / sizeof(NList64);

            _symTable = symTable;
            return symTable;
        }

        private ulong GetAddressFromFileOffset(uint fileOffset)
        {
            foreach (var seg in _segments)
                if (seg.FileOffset <= fileOffset && fileOffset < seg.FileOffset + seg.FileSize)
                    return LoadBias + fileOffset + seg.Address - seg.FileOffset;
            
            return LoadBias + fileOffset;
        }

        public IEnumerable<Segment64LoadCommand> EnumerateSegments()
        {
            uint offset = MachHeader64.Size;
            for (int i = 0; i < _header.NumberCommands; i++)
            {
                ulong cmdAddress = BaseAddress + offset;
                LoadCommandHeader cmd = _parent.ReadMemory<LoadCommandHeader>(cmdAddress);

                if (cmd.Kind == LoadCommandType.Segment64)
                    yield return _parent.ReadMemory<Segment64LoadCommand>(cmdAddress + LoadCommandHeader.HeaderSize);

                offset += cmd.Size;
            }
        }
    }
}
