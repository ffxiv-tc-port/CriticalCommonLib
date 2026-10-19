using System.Collections.Generic;

namespace CriticalCommonLib.Models;

/// <summary>
/// 每個物品 ID（HQ 為 ItemId＋1,000,000）所屬的套裝編號與名稱，順序同 <c>IInventoryScanner.GetGearSets()</c> 那個 HashSet 的列舉順序。
/// 建好之後不再改；<see cref="TryGet"/> 交出去的兩個陣列由同一個 ID 的所有格子共用，任何人都不可原地修改。
/// </summary>
public sealed class GearSetMembership
{
    public static readonly GearSetMembership Empty = new(new Dictionary<uint, (uint[] Ids, string[] Names)>());

    private readonly Dictionary<uint, (uint[] Ids, string[] Names)> _byItemId;

    private GearSetMembership(Dictionary<uint, (uint[] Ids, string[] Names)> byItemId)
    {
        _byItemId = byItemId;
    }

    public static GearSetMembership FromSets(Dictionary<uint, HashSet<(byte, string)>> gearSets)
    {
        var byItemId = new Dictionary<uint, (uint[] Ids, string[] Names)>(gearSets.Count);
        foreach (var (itemId, sets) in gearSets)
        {
            var ids = new uint[sets.Count];
            var names = new string[sets.Count];
            var k = 0;
            foreach (var (gearSetId, name) in sets)
            {
                ids[k] = gearSetId;
                names[k] = name;
                k++;
            }

            byItemId.Add(itemId, (ids, names));
        }

        return new GearSetMembership(byItemId);
    }

    public bool TryGet(uint itemId, out uint[] gearSetIds, out string[] gearSetNames)
    {
        if (_byItemId.TryGetValue(itemId, out var entry))
        {
            gearSetIds = entry.Ids;
            gearSetNames = entry.Names;
            return true;
        }

        gearSetIds = null!;
        gearSetNames = null!;
        return false;
    }
}
