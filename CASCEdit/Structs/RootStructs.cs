using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CASCEdit.Handlers;
using CASCEdit.Helpers;

namespace CASCEdit.Structs
{
    [Flags]
    public enum LocaleFlags : uint
    {
        All = 0xFFFFFFFF,
        None = 0,
        enUS = 0x2,
        koKR = 0x4,
        frFR = 0x10,
        deDE = 0x20,
        zhCN = 0x40,
        esES = 0x80,
        zhTW = 0x100,
        enGB = 0x200,
        enCN = 0x400,
        enTW = 0x800,
        esMX = 0x1000,
        ruRU = 0x2000,
        ptBR = 0x4000,
        itIT = 0x8000,
        ptPT = 0x10000,
        All_WoW = enUS | koKR | frFR | deDE | zhCN | esES | zhTW | enGB | esMX | ruRU | ptBR | itIT | ptPT,
        All_WoW_Classic = enUS | koKR | frFR | deDE | zhCN | esES | zhTW | enGB | esMX | ruRU | ptBR | ptPT
    }

    [Flags]
    public enum ContentFlags : uint
    {
        None = 0,
        F00000001 = 0x1, // seen on *.wlm files
        F00000002 = 0x2,
        F00000004 = 0x4,
        Windows = 0x8, // added in 7.2.0.23436
        MacOS = 0x10, // added in 7.2.0.23436
        Alternate = 0x80, // many chinese models have this flag
        F00000100 = 0x100, // apparently client doesn't load files with this flag
        F00000800 = 0x800, // only seen on UpdatePlugin files
        F00008000 = 0x8000, // Windows ARM64?
        F00020000 = 0x20000, // new 9.0
        F00040000 = 0x40000, // new 9.0
        F00080000 = 0x80000, // new 9.0
        F00100000 = 0x100000, // new 9.0
        F00200000 = 0x200000, // new 9.0
        F00400000 = 0x400000, // new 9.0
        F00800000 = 0x800000, // new 9.0
        F02000000 = 0x2000000, // new 9.0
        F04000000 = 0x4000000, // new 9.0
        Encrypted = 0x8000000, // encrypted may be?
        NoNameHash = 0x10000000, // doesn't have name hash?
        F20000000 = 0x20000000, // added in 21737, used for many cinematics
        Bundle = 0x40000000, // not related to wow, used as some old overwatch hack
        NotCompressed = 0x80000000 // sounds have this flag
    }

    public class RootChunk
    {
        public uint Count;
        public ContentFlags ContentFlags;
        public LocaleFlags LocaleFlags;
        public List<RootEntry> Entries = new List<RootEntry>();
    }

    public class RootEntry
    {
        public MD5Hash CEKey;
        public uint FileDataIdOffset;
        public ulong NameHash;

        public uint FileDataId;
        public string Path;
    }
}
