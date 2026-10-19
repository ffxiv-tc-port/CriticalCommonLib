using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AllaganLib.GameSheets.Sheets;
using AllaganLib.GameSheets.Sheets.Rows;
using CriticalCommonLib.Enums;
using CriticalCommonLib.Extensions;
using CriticalCommonLib.GameStructs;
using CriticalCommonLib.Interfaces;

using Dalamud.Interface.Colors;
using FFXIVClientStructs.FFXIV.Common.Component.Excel;
using Lumina;
using Lumina.Data;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;
using Newtonsoft.Json;

namespace CriticalCommonLib.Models
{
    using FFXIVClientStructs.FFXIV.Client.UI.Agent;

    public class InventoryItem : IEquatable<InventoryItem>, ICsv, IItem, Helpers.ICsvFields
    {
        private readonly ItemSheet _itemSheet;
        private readonly ExcelSheet<Stain> _stainSheet;
        public InventoryType Container;
        public short Slot;
        public uint Quantity;
        public ushort Spiritbond;
        public ushort Condition;
        public FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags Flags;
        public ushort Materia0;
        public ushort Materia1;
        public ushort Materia2;
        public ushort Materia3;
        public ushort Materia4;
        public byte MateriaLevel0;
        public byte MateriaLevel1;
        public byte MateriaLevel2;
        public byte MateriaLevel3;
        public byte MateriaLevel4;
        public byte Stain;
        public byte Stain2;
        public uint GlamourId;
        public InventoryType SortedContainer;
        public InventoryCategory SortedCategory;
        public int SortedSlotIndex;
        [JsonIgnore]
        public int GlamourIndex;
        public ulong RetainerId;
        [JsonIgnore]
        public uint TempQuantity = 0;
        public uint RetainerMarketPrice;
        //Cabinet category

        public uint[]? GearSets = Array.Empty<uint>();
        public string[]? GearSetNames = Array.Empty<string>();

        public delegate InventoryItem Factory();

        public InventoryItem(ItemSheet itemSheet, ExcelSheet<Stain> stainSheet)
        {
            _itemSheet = itemSheet;
            _stainSheet = stainSheet;
        }

        /// <summary>每個欄位回到剛建構好的值（兩張表除外），讓暫存物件可以重用；新增欄位時這裡要一起補。</summary>
        internal void ResetForReuse()
        {
            Container = default;
            Slot = 0;
            ItemId = 0;
            Quantity = 0;
            Spiritbond = 0;
            Condition = 0;
            Flags = default;
            Materia0 = 0;
            Materia1 = 0;
            Materia2 = 0;
            Materia3 = 0;
            Materia4 = 0;
            MateriaLevel0 = 0;
            MateriaLevel1 = 0;
            MateriaLevel2 = 0;
            MateriaLevel3 = 0;
            MateriaLevel4 = 0;
            Stain = 0;
            Stain2 = 0;
            GlamourId = 0;
            SortedContainer = default;
            SortedCategory = default;
            SortedSlotIndex = 0;
            GlamourIndex = 0;
            RetainerId = 0;
            TempQuantity = 0;
            RetainerMarketPrice = 0;
            GearSets = Array.Empty<uint>();
            GearSetNames = Array.Empty<string>();
        }

        public void FromSerializedItem(ulong[] serializedItem)
        {
            var gearSetLengh = serializedItem.Length - 25;
            var gearSets = gearSetLengh > 0 ? new ArraySegment<ulong>(serializedItem, 25, serializedItem.Length - 25).Select(i => (uint)i).ToArray() : null;

            Container = (InventoryType)serializedItem[0];
            Slot = (short)serializedItem[1];
            ItemId = (uint)serializedItem[2];
            Quantity = (uint)serializedItem[3];
            Spiritbond = (ushort)serializedItem[4];
            Condition = (ushort)serializedItem[5];
            Flags = (FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags)serializedItem[6];
            Materia0 = (ushort)serializedItem[7];
            Materia1 = (ushort)serializedItem[8];
            Materia2 = (ushort)serializedItem[9];
            Materia3 = (ushort)serializedItem[10];
            Materia4 = (ushort)serializedItem[11];
            MateriaLevel0 = (byte)serializedItem[12];
            MateriaLevel1 = (byte)serializedItem[13];
            MateriaLevel2 = (byte)serializedItem[14];
            MateriaLevel3 = (byte)serializedItem[15];
            MateriaLevel4 = (byte)serializedItem[16];
            Stain = (byte)serializedItem[17];
            Stain2 = (byte)serializedItem[18];
            GlamourId = (uint)serializedItem[19];
            SortedContainer = (InventoryType)serializedItem[20];
            SortedCategory = (InventoryCategory)serializedItem[21];
            SortedSlotIndex = (int)serializedItem[22];
            RetainerId = (uint)serializedItem[23];
            RetainerMarketPrice = (uint)serializedItem[24];
            GearSets = gearSets;
        }
        public void FromInventoryItem(InventoryItem inventoryItem)
        {
            Container = inventoryItem.Container;
            Slot = inventoryItem.Slot;
            ItemId = inventoryItem.ItemId;
            Quantity = inventoryItem.Quantity;
            Spiritbond = inventoryItem.Spiritbond;
            Condition = inventoryItem.Condition;
            Flags = inventoryItem.Flags;
            Materia0 = inventoryItem.Materia0;
            Materia1 = inventoryItem.Materia1;
            Materia2 = inventoryItem.Materia2;
            Materia3 = inventoryItem.Materia3;
            Materia4 = inventoryItem.Materia4;
            MateriaLevel0 = inventoryItem.MateriaLevel0;
            MateriaLevel1 = inventoryItem.MateriaLevel1;
            MateriaLevel2 = inventoryItem.MateriaLevel2;
            MateriaLevel3 = inventoryItem.MateriaLevel3;
            MateriaLevel4 = inventoryItem.MateriaLevel4;
            Stain = inventoryItem.Stain;
            Stain2 = inventoryItem.Stain2;
            GlamourId = inventoryItem.GlamourId;
            SortedContainer = inventoryItem.SortedContainer;
            SortedCategory = inventoryItem.SortedCategory;
            SortedSlotIndex = inventoryItem.SortedSlotIndex;
        }
        public void FromGameItem(FFXIVClientStructs.FFXIV.Client.Game.InventoryItem inventoryItem)
        {
            Container = inventoryItem.Container.Convert();
            Slot = inventoryItem.Slot;
            ItemId = inventoryItem.ItemId;
            Quantity = (uint)inventoryItem.Quantity;
            Spiritbond = inventoryItem.SpiritbondOrCollectability;
            Condition = inventoryItem.Condition;
            Flags = inventoryItem.Flags;
            Materia0 = inventoryItem.Materia[0];
            Materia1 = inventoryItem.Materia[1];
            Materia2 = inventoryItem.Materia[2];
            Materia3 = inventoryItem.Materia[3];
            Materia4 = inventoryItem.Materia[4];
            MateriaLevel0 = inventoryItem.MateriaGrades[0];
            MateriaLevel1 = inventoryItem.MateriaGrades[1];
            MateriaLevel2 = inventoryItem.MateriaGrades[2];
            MateriaLevel3 = inventoryItem.MateriaGrades[3];
            MateriaLevel4 = inventoryItem.MateriaGrades[4];
            Stain = inventoryItem.Stains[0];
            Stain2 = inventoryItem.Stains[1];
            GlamourId = inventoryItem.GlamourId;
        }
        public void FromRaw(InventoryType container, short slot, uint itemId, uint quantity, ushort spiritbond, ushort condition, FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags flags, ushort materia0, ushort materia1, ushort materia2, ushort materia3, ushort materia4, byte materiaLevel0, byte materiaLevel1, byte materiaLevel2, byte materiaLevel3, byte materiaLevel4, byte stain1, byte stain2, uint glamourId)
        {
            Container = container;
            Slot = slot;
            ItemId = itemId;
            Quantity = quantity;
            Spiritbond = spiritbond;
            Condition = condition;
            Flags = flags;
            Materia0 = materia0;
            Materia1 = materia1;
            Materia2 = materia2;
            Materia3 = materia3;
            Materia4 = materia4;
            MateriaLevel0 = materiaLevel0;
            MateriaLevel1 = materiaLevel1;
            MateriaLevel2 = materiaLevel2;
            MateriaLevel3 = materiaLevel3;
            MateriaLevel4 = materiaLevel4;
            Stain = stain1;
            Stain2 = stain2;
            GlamourId = glamourId;
        }
        [JsonIgnore]
        public bool IsHQ => (Flags & FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.HighQuality) == FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.HighQuality;
        [JsonIgnore]
        public bool IsCollectible => (Flags & FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.Collectable) == FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags.Collectable;
        [JsonIgnore]
        public bool IsEmpty => ItemId == 0;
        [JsonIgnore]
        public bool InRetainer => RetainerId.ToString().StartsWith("3");

        [JsonIgnore]
        public bool IsEquippedGear => Container is InventoryType.ArmoryBody or InventoryType.ArmoryEar or InventoryType.ArmoryFeet or InventoryType.ArmoryHand or InventoryType.ArmoryHead or InventoryType.ArmoryLegs or InventoryType.ArmoryLegs or InventoryType.ArmoryMain or InventoryType.ArmoryNeck or InventoryType.ArmoryOff or InventoryType.ArmoryRing or InventoryType.ArmoryWaist or InventoryType.ArmoryWrist or InventoryType.GearSet0 or InventoryType.RetainerEquippedGear;

        [JsonIgnore]
        public int ActualSpiritbond => Spiritbond / 100;

        [JsonIgnore]
        public Vector4 ItemColour
        {
            get
            {
                if (IsHQ)
                {
                    return ImGuiColors.TankBlue;
                }
                else if (IsCollectible)
                {
                    return ImGuiColors.DalamudOrange;
                }

                return ImGuiColors.HealerGreen;
            }
        }
        [JsonIgnore]
        public uint RemainingStack
        {
            get
            {
                return Item.Base.StackSize - Quantity;
            }
        }
        [JsonIgnore]
        public uint RemainingTempStack
        {
            get
            {
                return Item.Base.StackSize - TempQuantity;
            }
        }
        [JsonIgnore]
        public bool FullStack
        {
            get
            {
                return (Quantity == Item.Base.StackSize);
            }
        }
        [JsonIgnore]
        public bool CanBeTraded
        {
            get
            {
                return !Item.Base.IsUntradable && (Spiritbond * 100) == 0;
            }
        }
        [JsonIgnore]
        public bool CanBePlacedOnMarket
        {
            get
            {
                return !Item.Base.IsUntradable && Item.CanBePlacedOnMarket && (Spiritbond * 100) == 0;
            }
        }

        [JsonIgnore]
        public int MateriaCount =>
            (Materia0 != 0 ? 1 : 0) + (Materia1 != 0 ? 1 : 0) + (Materia2 != 0 ? 1 : 0) +
            (Materia3 != 0 ? 1 : 0) + (Materia4 != 0 ? 1 : 0);

        [JsonIgnore]
        public int DyeCount =>
            (Stain != 0 ? 1 : 0) + (Stain2 != 0 ? 1 : 0);


        public static ConcurrentDictionary<(InventoryType, int), Vector2> SlotIndexCache => _slotIndexCache ??= new ConcurrentDictionary<(InventoryType, int), Vector2>();

        private static ConcurrentDictionary<(InventoryType, int), Vector2>? _slotIndexCache;

        public Vector2 BagLocation(InventoryType bagType, int? glamourIndex = null)
        {
            if (!SlotIndexCache.ContainsKey((bagType, glamourIndex ?? SortedSlotIndex)))
            {
                if (bagType is InventoryType.Bag0 or InventoryType.Bag1 or InventoryType.Bag2 or InventoryType.Bag3
                    or InventoryType.RetainerBag0 or InventoryType.RetainerBag1 or InventoryType.RetainerBag2
                    or InventoryType.RetainerBag3 or InventoryType.RetainerBag4 or InventoryType.SaddleBag0
                    or InventoryType.SaddleBag1 or InventoryType.PremiumSaddleBag0 or InventoryType.PremiumSaddleBag1)
                {
                    var x = SortedSlotIndex % 5;
                    var y = SortedSlotIndex / 5;
                    SlotIndexCache[(bagType, SortedSlotIndex)] = new Vector2(x, y);

                }

                else if (bagType is InventoryType.ArmoryBody or InventoryType.ArmoryEar or InventoryType.ArmoryFeet
                         or InventoryType.ArmoryHand or InventoryType.ArmoryHead or InventoryType.ArmoryLegs
                         or InventoryType.ArmoryMain or InventoryType.ArmoryNeck or InventoryType.ArmoryOff
                         or InventoryType.ArmoryRing or InventoryType.ArmoryWrist or InventoryType.ArmorySoulCrystal
                         or InventoryType.FreeCompanyBag0 or InventoryType.FreeCompanyBag1 or InventoryType.FreeCompanyBag2
                         or InventoryType.FreeCompanyBag3 or InventoryType.FreeCompanyBag4)
                {
                    var x = SortedSlotIndex;
                    SlotIndexCache[(bagType, SortedSlotIndex)] = new Vector2(x, 0);

                }
                else if (bagType is InventoryType.GlamourChest)
                {
                    var x = (glamourIndex ?? GlamourIndex) % 10;
                    var y = (glamourIndex ?? GlamourIndex) / 10;
                    SlotIndexCache[(bagType, glamourIndex ?? GlamourIndex)] = new Vector2(x, y);
                }
                else
                {
                    SlotIndexCache[(bagType, SortedSlotIndex)] = Vector2.Zero;
                }
            }

            if (bagType is InventoryType.GlamourChest)
            {
                return SlotIndexCache[(bagType, glamourIndex ?? GlamourIndex)];
            }

            return SlotIndexCache[(bagType, glamourIndex ?? SortedSlotIndex)];
        }

        [JsonIgnore]
        public string FormattedType
        {
            get
            {
                return this.IsCollectible ? "Collectible" : (IsHQ ? "HQ" : "NQ");
            }
        }

        [JsonIgnore]
        public string FormattedName
        {
            get
            {
                if (ItemId == 0)
                {
                    return "Empty Slot";
                }
                return Item.NameString;
            }
        }

        [JsonIgnore]
        public uint SellToVendorPrice
        {
            get
            {
                return IsHQ ? Item.Base.PriceLow + 1 : Item.Base.PriceLow;
            }
        }

        [JsonIgnore]
        public uint BuyFromVendorPrice
        {
            get
            {
                return IsHQ ? Item.Base.PriceMid + 1 : Item.Base.PriceMid;
            }
        }

        public IEnumerable<(ushort materiaId, byte level)> Materia() {
            if (Materia0 != 0) yield return (Materia0, MateriaLevel0); else yield break;
            if (Materia1 != 0) yield return (Materia1, MateriaLevel1); else yield break;
            if (Materia2 != 0) yield return (Materia2, MateriaLevel2); else yield break;
            if (Materia3 != 0) yield return (Materia3, MateriaLevel3); else yield break;
            if (Materia4 != 0) yield return (Materia4, MateriaLevel4);
        }

        [JsonIgnore] public bool InGearSet => (GearSets?.Length ?? 0) != 0;

        [JsonIgnore]
        public ItemRow Item => _itemSheet.GetRowOrDefault(ItemId) ?? _itemSheet.GetRow(1);

        [JsonIgnore]
        public Stain? StainEntry => _stainSheet.GetRowOrDefault(Stain);

        [JsonIgnore]
        public Stain? Stain2Entry => _stainSheet.GetRowOrDefault(Stain2);

        [JsonIgnore]
        public ushort Icon {
            get {
                return Item.Icon;
            }
        }

        public bool Equals(InventoryItem? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return ItemId == other.ItemId && Flags == other.Flags;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((InventoryItem) obj);
        }

        public override int GetHashCode()
        {
            var flags = (int) Flags * 100000;
            return (int) ItemId + flags;
        }

        public int GenerateHashCode(bool ignoreFlags = false)
        {
            if (ignoreFlags)
            {
                return (int)ItemId;
            }

            return GetHashCode();
        }

        /// <summary>
        /// Determines of the two instances of InventoryItem are functionally the same
        /// </summary>
        /// <param name="otherItem"></param>
        /// <returns></returns>
        public bool IsSame(InventoryItem otherItem)
        {
            if (SortedContainer != otherItem.SortedContainer)
            {
                return false;
            }

            if (SortedSlotIndex != otherItem.SortedSlotIndex)
            {
                return false;
            }

            if (ItemId != otherItem.ItemId)
            {
                return false;
            }

            if (Quantity != otherItem.Quantity)
            {
                return false;
            }

            if (Spiritbond != otherItem.Spiritbond)
            {
                return false;
            }

            if ((ItemId != 0 || otherItem.ItemId != 0) && Condition != otherItem.Condition)
            {
                return false;
            }

            if (Flags != otherItem.Flags)
            {
                return false;
            }

            if (Materia0 != otherItem.Materia0)
            {
                return false;
            }

            if (Materia1 != otherItem.Materia1)
            {
                return false;
            }

            if (Materia2 != otherItem.Materia2)
            {
                return false;
            }

            if (Materia3 != otherItem.Materia3)
            {
                return false;
            }

            if (Materia4 != otherItem.Materia4)
            {
                return false;
            }

            if (MateriaLevel0 != otherItem.MateriaLevel0)
            {
                return false;
            }

            if (MateriaLevel1 != otherItem.MateriaLevel1)
            {
                return false;
            }

            if (MateriaLevel2 != otherItem.MateriaLevel2)
            {
                return false;
            }

            if (MateriaLevel3 != otherItem.MateriaLevel3)
            {
                return false;
            }

            if (MateriaLevel4 != otherItem.MateriaLevel4)
            {
                return false;
            }

            if (Stain != otherItem.Stain)
            {
                return false;
            }

            if (Stain2 != otherItem.Stain2)
            {
                return false;
            }

            if (GlamourId != otherItem.GlamourId)
            {
                return false;
            }

            if (SortedContainer == InventoryType.RetainerMarket && RetainerMarketPrice != otherItem.RetainerMarketPrice)
            {
                return false;
            }

            if (GearSets != null && otherItem.GearSets != null && (GearSets.Length != 0 || otherItem.GearSets.Length != 0) && !SameGearSets(GearSets, otherItem.GearSets))
            {
                return false;
            }

            return true;
        }


        /// <summary>
        /// Determines of the two instances of InventoryItem are functionally the same, without comparing their locations
        /// </summary>
        /// <param name="otherItem"></param>
        /// <returns></returns>
        public InventoryChangeReason? IsSameItem(InventoryItem otherItem)
        {
            if (ItemId != otherItem.ItemId)
            {
                return InventoryChangeReason.ItemIdChanged;
            }

            if (Quantity != otherItem.Quantity)
            {
                return InventoryChangeReason.QuantityChanged;
            }

            if (Spiritbond != otherItem.Spiritbond)
            {
                return InventoryChangeReason.SpiritbondChanged;
            }

            if ((ItemId != 0 || otherItem.ItemId != 0) && Condition != otherItem.Condition)
            {
                return InventoryChangeReason.ConditionChanged;
            }

            if (Flags != otherItem.Flags)
            {
                return InventoryChangeReason.FlagsChanged;
            }

            if (Materia0 != otherItem.Materia0)
            {
                return InventoryChangeReason.MateriaChanged;
            }

            if (Materia1 != otherItem.Materia1)
            {
                return InventoryChangeReason.MateriaChanged;
            }

            if (Materia2 != otherItem.Materia2)
            {
                return InventoryChangeReason.MateriaChanged;
            }

            if (Materia3 != otherItem.Materia3)
            {
                return InventoryChangeReason.MateriaChanged;
            }

            if (Materia4 != otherItem.Materia4)
            {
                return InventoryChangeReason.MateriaChanged;
            }

            if (MateriaLevel0 != otherItem.MateriaLevel0)
            {
                return InventoryChangeReason.MateriaChanged;
            }

            if (MateriaLevel1 != otherItem.MateriaLevel1)
            {
                return InventoryChangeReason.MateriaChanged;
            }

            if (MateriaLevel2 != otherItem.MateriaLevel2)
            {
                return InventoryChangeReason.MateriaChanged;
            }

            if (MateriaLevel3 != otherItem.MateriaLevel3)
            {
                return InventoryChangeReason.MateriaChanged;
            }

            if (MateriaLevel4 != otherItem.MateriaLevel4)
            {
                return InventoryChangeReason.MateriaChanged;
            }

            if (Stain != otherItem.Stain)
            {
                return InventoryChangeReason.StainChanged;
            }

            if (Stain2 != otherItem.Stain2)
            {
                return InventoryChangeReason.StainChanged;
            }

            if (GlamourId != otherItem.GlamourId)
            {
                return InventoryChangeReason.GlamourChanged;
            }

            if (SortedContainer == InventoryType.RetainerMarket && RetainerMarketPrice != otherItem.RetainerMarketPrice)
            {
                return InventoryChangeReason.MarketPriceChanged;
            }

            if (GearSets != null && otherItem.GearSets != null && (GearSets.Length != 0 || otherItem.GearSets.Length != 0) && GearSets.Length == otherItem.GearSets.Length && !SameGearSets(GearSets, otherItem.GearSets))
            {
                return InventoryChangeReason.GearsetsChanged;
            }

            return null;
        }

        private const int SameGearSetsStackLimit = 128;

        /// <summary>
        /// 與 <c>a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x))</c> 同值（多重集合相等：不計順序、重複要計、長度不同即不等），
        /// 但不配置記憶體；超過 <see cref="SameGearSetsStackLimit"/> 格才退回原寫法。
        /// </summary>
        private static bool SameGearSets(uint[] a, uint[] b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a.Length != b.Length)
            {
                return false;
            }

            if (a.AsSpan().SequenceEqual(b))
            {
                return true;
            }

            if (a.Length > SameGearSetsStackLimit)
            {
                return a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x));
            }

            Span<uint> sortedA = stackalloc uint[a.Length];
            Span<uint> sortedB = stackalloc uint[b.Length];
            a.CopyTo(sortedA);
            b.CopyTo(sortedB);
            sortedA.Sort();
            sortedB.Sort();
            return sortedA.SequenceEqual(sortedB);
        }

        /// <summary>
        /// Determines of the two instances of InventoryItem are in the same position
        /// </summary>
        /// <param name="otherItem"></param>
        /// <returns></returns>
        public bool IsSamePosition(InventoryItem otherItem)
        {
            if (SortedContainer != otherItem.SortedContainer)
            {
                return false;
            }

            if (SortedSlotIndex != otherItem.SortedSlotIndex)
            {
                return false;
            }

            return true;
        }

        public void FromCsv(string[] csvData)
        {
            if (Enum.TryParse<InventoryType>(csvData[0], out var container))
            {
                Container = container;
            }
            if(short.TryParse(csvData[1], out var slot))
            {
                Slot = slot;
            }
            if(uint.TryParse(csvData[2], out var itemId))
            {
                ItemId = itemId;
            }
            if(uint.TryParse(csvData[3], out var quantity))
            {
                Quantity = quantity;
            }
            if(ushort.TryParse(csvData[4], out var spiritbond))
            {
                Spiritbond = spiritbond;
            }
            if(ushort.TryParse(csvData[5], out var condition))
            {
                Condition = condition;
            }
            if (Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Game.InventoryItem.ItemFlags>(csvData[6], out var flags))
            {
                Flags = flags;
            }
            if(ushort.TryParse(csvData[7], out var materia0))
            {
                Materia0 = materia0;
            }
            if(ushort.TryParse(csvData[8], out var materia1))
            {
                Materia1 = materia1;
            }
            if(ushort.TryParse(csvData[9], out var materia2))
            {
                Materia2 = materia2;
            }
            if(ushort.TryParse(csvData[10], out var materia3))
            {
                Materia3 = materia3;
            }
            if(ushort.TryParse(csvData[11], out var materia4))
            {
                Materia4 = materia4;
            }
            if(byte.TryParse(csvData[12], out var materiaLevel0))
            {
                MateriaLevel0 = materiaLevel0;
            }
            if(byte.TryParse(csvData[13], out var materiaLevel1))
            {
                MateriaLevel1 = materiaLevel1;
            }
            if(byte.TryParse(csvData[14], out var materiaLevel2))
            {
                MateriaLevel2 = materiaLevel2;
            }
            if(byte.TryParse(csvData[15], out var materiaLevel3))
            {
                MateriaLevel3 = materiaLevel3;
            }
            if(byte.TryParse(csvData[16], out var materiaLevel4))
            {
                MateriaLevel4 = materiaLevel4;
            }
            if(byte.TryParse(csvData[17], out var stain))
            {
                Stain = stain;
            }
            if(byte.TryParse(csvData[18], out var stain2))
            {
                Stain2 = stain2;
            }
            if(uint.TryParse(csvData[19], out var glamourId))
            {
                GlamourId = glamourId;
            }
            if (Enum.TryParse<InventoryType>(csvData[20], out var inventoryType))
            {
                SortedContainer = inventoryType;
            }
            if (Enum.TryParse<InventoryCategory>(csvData[21], out var inventoryCategory))
            {
                SortedCategory = inventoryCategory;
            }
            if(int.TryParse(csvData[22], out var sortedSlotIndex))
            {
                SortedSlotIndex = sortedSlotIndex;
            }
            if(ulong.TryParse(csvData[23], out var retainerId))
            {
                RetainerId = retainerId;
            }
            if(uint.TryParse(csvData[24], out var retainerMarketPrice))
            {
                RetainerMarketPrice = retainerMarketPrice;
            }

            var gearSetNames = csvData[26].Split(";");
            GearSetNames = gearSetNames.Where(c => c != string.Empty).ToArray();

            var gearSets = csvData[25].Split(";");
            GearSets = new uint[GearSetNames.Length];
            for (var index = 0; index < GearSetNames.Length && index < gearSets.Length; index++)
            {
                var gearSet = gearSets[index];
                if (uint.TryParse(gearSet, out var parsedGearSetId))
                {
                    GearSets[index] = parsedGearSetId;
                }
            }
        }

        /// <summary><see cref="ToCsv"/> 的欄位數。</summary>
        internal const int CsvFieldCount = 27;

        public string[] ToCsv()
        {
            var csvData = new string[CsvFieldCount];
            WriteCsvFields(csvData, 0);
            return csvData;
        }

        /// <summary>把 <see cref="ToCsv"/> 的欄位依序填進 <paramref name="csvData"/> 從 <paramref name="o"/> 起的 <see cref="CsvFieldCount"/> 格。</summary>
        internal void WriteCsvFields(string[] csvData, int o)
        {
            csvData[o + 0] = ((int)this.Container).ToString();
            csvData[o + 1] = Slot.ToString();
            csvData[o + 2] = ItemId.ToString();
            csvData[o + 3] = Quantity.ToString();
            csvData[o + 4] = Spiritbond.ToString();
            csvData[o + 5] = Condition.ToString();
            csvData[o + 6] = ((int)Flags).ToString();
            csvData[o + 7] = Materia0.ToString();
            csvData[o + 8] = Materia1.ToString();
            csvData[o + 9] = Materia2.ToString();
            csvData[o + 10] = Materia3.ToString();
            csvData[o + 11] = Materia4.ToString();
            csvData[o + 12] = MateriaLevel0.ToString();
            csvData[o + 13] = MateriaLevel1.ToString();
            csvData[o + 14] = MateriaLevel2.ToString();
            csvData[o + 15] = MateriaLevel3.ToString();
            csvData[o + 16] = MateriaLevel4.ToString();
            csvData[o + 17] = Stain.ToString();
            csvData[o + 18] = Stain2.ToString();
            csvData[o + 19] = GlamourId.ToString();
            csvData[o + 20] = ((int)SortedContainer).ToString();
            csvData[o + 21] = ((int)SortedCategory).ToString();
            csvData[o + 22] = SortedSlotIndex.ToString();
            csvData[o + 23] = RetainerId.ToString();
            csvData[o + 24] = RetainerMarketPrice.ToString();
            csvData[o + 25] = GearSets == null || GearSets.Length == 0 ? "" : String.Join(";", GearSets.Select(c => c.ToString()));
            csvData[o + 26] = GearSetNames == null || GearSetNames.Length == 0 ? "" : String.Join(";", GearSetNames.Select(c => c.ToString()));
        }

        // 與 WriteCsvFields 逐欄對應（同樣的 ToString、同樣的空值寫法）；改一邊就要改另一邊。
        void Helpers.ICsvFields.WriteCsvRow(Helpers.CsvRowWriter.FieldWriter row)
        {
            row.Formatted((int)this.Container);
            row.Formatted(Slot);
            row.Formatted(ItemId);
            row.Formatted(Quantity);
            row.Formatted(Spiritbond);
            row.Formatted(Condition);
            row.Formatted((int)Flags);
            row.Formatted(Materia0);
            row.Formatted(Materia1);
            row.Formatted(Materia2);
            row.Formatted(Materia3);
            row.Formatted(Materia4);
            row.Formatted(MateriaLevel0);
            row.Formatted(MateriaLevel1);
            row.Formatted(MateriaLevel2);
            row.Formatted(MateriaLevel3);
            row.Formatted(MateriaLevel4);
            row.Formatted(Stain);
            row.Formatted(Stain2);
            row.Formatted(GlamourId);
            row.Formatted((int)SortedContainer);
            row.Formatted((int)SortedCategory);
            row.Formatted(SortedSlotIndex);
            row.Formatted(RetainerId);
            row.Formatted(RetainerMarketPrice);
            if (GearSets == null || GearSets.Length == 0)
            {
                row.Text("");
            }
            else
            {
                row.Joined(GearSets);
            }

            if (GearSetNames == null || GearSetNames.Length == 0)
            {
                row.Text("");
            }
            else
            {
                row.Joined(GearSetNames);
            }
        }

        public bool IncludeInCsv()
        {
            return ItemId != 0;
        }

        public void PopulateData(Lumina.Excel.ExcelModule gameData, Language language)
        {

        }

        public ulong[] ToNumeric()
        {
            var serializedItem = new ulong[25 + GearSets?.Length ?? 0];
            serializedItem[0] = (ulong)Container;
            serializedItem[1] = (ulong)Slot;
            serializedItem[2] = ItemId;
            serializedItem[3] = Quantity;
            serializedItem[4] = Spiritbond;
            serializedItem[5] = Condition;
            serializedItem[6] = (ulong)Flags;
            serializedItem[7] = Materia0;
            serializedItem[8] = Materia1;
            serializedItem[9] = Materia2;
            serializedItem[10] = Materia3;
            serializedItem[11] = Materia4;
            serializedItem[12] = MateriaLevel0;
            serializedItem[13] = MateriaLevel1;
            serializedItem[14] = MateriaLevel2;
            serializedItem[15] = MateriaLevel3;
            serializedItem[16] = MateriaLevel4;
            serializedItem[17] = Stain;
            serializedItem[18] = Stain2;
            serializedItem[19] = GlamourId;
            serializedItem[20] = (ulong)SortedContainer;
            serializedItem[21] = (ulong)SortedCategory;
            serializedItem[22] = (ulong)SortedSlotIndex;
            serializedItem[23] = RetainerId;
            serializedItem[24] = RetainerMarketPrice;

            if (GearSets == null || GearSets.Length == 0) return serializedItem;
            for (int i = 0; i < GearSets.Length; i++) {
                serializedItem[25 + i] = GearSets[i];
            }

            return serializedItem;
        }

        public uint ItemId { get; set; }
    }
}