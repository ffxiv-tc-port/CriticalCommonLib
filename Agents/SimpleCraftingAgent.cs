using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CriticalCommonLib.Agents
{
    //Not really an agent but seems a good as spot as any
    /// <summary>
    /// Reads the SynthesisSimple (quick synthesis) window's AtkValues; same contract as <see cref="CraftingAgent"/>:
    /// no native pointer kept between frames, slots outside AtkValuesCount read 0.
    /// </summary>
    public unsafe class SimpleCraftingAgent
    {
        // Byte offsets into the AtkValues array: slot * 16 + 8.
        private const int OffsetNqTotal       = 88;
        private const int OffsetHqTotal     = 136;
        private const int OffsetCompleted = 56;
        private const int OffsetResultItemId   = 120;
        private const int OffsetTotal   = 72;

        private readonly Func<nint> _addonResolver;

        /// <param name="addonResolver">Returns the SynthesisSimple window's current address, 0 when it is not loaded.</param>
        public SimpleCraftingAgent(Func<nint> addonResolver)
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
            return new Values(
                (uint)CraftingAgent.ReadInt(values, count, OffsetNqTotal),
                (uint)CraftingAgent.ReadInt(values, count, OffsetHqTotal),
                (uint)CraftingAgent.ReadInt(values, count, OffsetCompleted),
                (uint)CraftingAgent.ReadInt(values, count, OffsetResultItemId),
                CraftingAgent.ReadInt(values, count, OffsetTotal));
        }

        public readonly record struct Values(uint NqCompleted, uint HqCompleted, uint TotalCompleted, uint ResultItemId, int Total)
        {
            public uint TotalFailed => TotalCompleted - NqCompleted - HqCompleted;

            public bool Finished => TotalCompleted == Total;
        }

        public uint NqCompleted => Read().NqCompleted;

        public uint HqCompleted => Read().HqCompleted;

        public uint TotalCompleted => Read().TotalCompleted;

        public uint TotalFailed => Read().TotalFailed;

        public int Total => Read().Total;

        public bool Finished => Read().Finished;

        public uint ResultItemId => Read().ResultItemId;
    }
}