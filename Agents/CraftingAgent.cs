using System;
using CriticalCommonLib.Crafting;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CriticalCommonLib.Agents
{
    //Not really an agent but seems a good as spot as any
    /// <summary>
    /// Reads the Synthesis window's AtkValues. Holds no native pointer between frames: every read resolves the window
    /// again and only reads a slot that is inside AtkValuesCount (window gone or array too short reads 0, like a null agent did).
    /// </summary>
    public unsafe class CraftingAgent
    {
        // Byte offsets into the AtkValues array: slot * 16 + 8, the value half of AtkValue[slot].
        private const int OffsetStep       = 248;
        private const int OffsetStatus     = 200;
        private const int OffsetDurability = 120;
        private const int OffsetHqChance   = 168;
        private const int OffsetQuality    = 152;
        private const int OffsetProgress   = 88;
        private const int OffsetResultItemId   = 264;
        private const int OffsetIsTrialSynthesis   = 24;

        private readonly Func<nint> _addonResolver;

        /// <param name="addonResolver">Returns the Synthesis window's current address, 0 when it is not loaded.</param>
        public CraftingAgent(Func<nint> addonResolver)
        {
            _addonResolver = addonResolver;
        }

        /// <summary>The window's current AtkValues array, for display only; null when the window is not loaded.</summary>
        public byte* Agent
        {
            get
            {
                var addon = (AtkUnitBase*)_addonResolver();
                return addon == null ? null : (byte*)addon->AtkValues;
            }
        }

        /// <summary>All values from one lookup of the window; read once per frame instead of property by property.</summary>
        public Values Read()
        {
            var addon = (AtkUnitBase*)_addonResolver();
            if (addon == null || addon->AtkValues == null)
            {
                return default;
            }

            var values = (byte*)addon->AtkValues;
            int count = addon->AtkValuesCount;
            var trial = Slot(values, count, OffsetIsTrialSynthesis);
            return new Values(
                ReadInt(values, count, OffsetStep),
                (CraftStatus)ReadInt(values, count, OffsetStatus),
                trial != null && *(bool*)trial,
                ReadInt(values, count, OffsetDurability),
                ReadInt(values, count, OffsetHqChance),
                ReadInt(values, count, OffsetQuality),
                ReadInt(values, count, OffsetProgress),
                (uint)ReadInt(values, count, OffsetResultItemId));
        }

        internal static byte* Slot(byte* values, int count, int byteOffset)
            => byteOffset / 16 < count ? values + byteOffset : null;

        internal static int ReadInt(byte* values, int count, int byteOffset)
        {
            var slot = Slot(values, count, byteOffset);
            return slot == null ? 0 : *(int*)slot;
        }

        public readonly record struct Values(int Step, CraftStatus Status, bool IsTrialSynthesis, int Durability, int HqChance, int Quality, int Progress, uint ResultItemId);

        public int Step => Read().Step;

        public CraftStatus Status => Read().Status;

        public bool IsTrialSynthesis => Read().IsTrialSynthesis;

        public int Durability => Read().Durability;

        public int HqChance => Read().HqChance;

        public int Quality => Read().Quality;

        public int Progress => Read().Progress;

        public uint ResultItemId => Read().ResultItemId;
    }
}