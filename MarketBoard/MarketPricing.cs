using System;
using System.Collections.Generic;
using System.Globalization;
using AllaganLib.GameSheets.Sheets.Rows;
using Lumina;
using Lumina.Data;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaSupplemental.Excel.Model;

namespace CriticalCommonLib.MarketBoard;

public class MarketPricing  : ICsv, Helpers.ICsvFields
{
    public uint ItemId { get; set; }
    public uint WorldId { get; set; }
    public float AveragePriceNq { get; set; }
    public float AveragePriceHq { get; set; }
    public float MinPriceNq { get; set; }
    public float MinPriceHq { get; set; }
    public int SevenDaySellCount { get; set; }

    public int Available { get; set; }
    public DateTime? LastSellDate { get; set; }
    public DateTime LastUpdate { get; set; } = DateTime.Now;

    public RecentHistory[]? recentHistory;
    public Listing[]? listings;

    /// <summary>
    /// 留在記憶體裡的成交紀錄筆數上限。API 回的順序由新到舊,取前面就是最近幾筆。
    /// </summary>
    private const int RetainedHistoryEntries = 20;

    public void FromCsv(string[] lineData)
    {
        ItemId = uint.Parse( lineData[ 0 ], CultureInfo.InvariantCulture );
        WorldId = uint.Parse( lineData[ 1 ], CultureInfo.InvariantCulture );
        AveragePriceNq = float.Parse( lineData[ 2 ], CultureInfo.InvariantCulture );
        AveragePriceHq = float.Parse( lineData[ 3 ], CultureInfo.InvariantCulture );
        MinPriceNq = float.Parse( lineData[ 4 ], CultureInfo.InvariantCulture );
        MinPriceHq = float.Parse( lineData[ 5 ], CultureInfo.InvariantCulture );
        SevenDaySellCount = int.Parse( lineData[ 6 ], CultureInfo.InvariantCulture );
        LastSellDate = lineData[7] == "" ? null : DateTime.Parse( lineData[ 7 ], CultureInfo.InvariantCulture );
        LastUpdate = DateTime.Parse( lineData[ 8 ], CultureInfo.InvariantCulture );
        Available = int.Parse( lineData[ 9 ], CultureInfo.InvariantCulture );
    }

    /// <summary><see cref="ToCsv"/> 的欄位數。</summary>
    internal const int CsvFieldCount = 10;

    public string[] ToCsv()
    {
        var csvData = new string[CsvFieldCount];
        WriteCsvFields(csvData, 0);
        return csvData;
    }

    // 與 WriteCsvFields 逐欄對應（同樣的 ToString 與文化設定）；改一邊就要改另一邊。
    void Helpers.ICsvFields.WriteCsvRow(Helpers.CsvRowWriter.FieldWriter row)
    {
        row.Formatted(ItemId);
        row.Formatted(WorldId);
        row.Formatted(AveragePriceNq, CultureInfo.InvariantCulture);
        row.Formatted(AveragePriceHq, CultureInfo.InvariantCulture);
        row.Formatted(MinPriceNq, CultureInfo.InvariantCulture);
        row.Formatted(MinPriceHq, CultureInfo.InvariantCulture);
        row.Formatted(SevenDaySellCount, CultureInfo.InvariantCulture);
        if (LastSellDate.HasValue)
        {
            row.Formatted(LastSellDate.Value, CultureInfo.InvariantCulture);
        }
        else
        {
            row.Text("");
        }

        row.Formatted(LastUpdate, CultureInfo.InvariantCulture);
        row.Formatted(Available, CultureInfo.InvariantCulture);
    }

    /// <summary>把 <see cref="ToCsv"/> 的欄位依序填進 <paramref name="csvData"/> 從 <paramref name="o"/> 起的 <see cref="CsvFieldCount"/> 格。</summary>
    internal void WriteCsvFields(string[] csvData, int o)
    {
        csvData[o + 0] = ItemId.ToString();
        csvData[o + 1] = WorldId.ToString();
        csvData[o + 2] = AveragePriceNq.ToString(CultureInfo.InvariantCulture);
        csvData[o + 3] = AveragePriceHq.ToString(CultureInfo.InvariantCulture);
        csvData[o + 4] = MinPriceNq.ToString(CultureInfo.InvariantCulture);
        csvData[o + 5] = MinPriceHq.ToString(CultureInfo.InvariantCulture);
        csvData[o + 6] = SevenDaySellCount.ToString(CultureInfo.InvariantCulture);
        csvData[o + 7] = LastSellDate.HasValue ? LastSellDate.Value.ToString(CultureInfo.InvariantCulture) : "";
        csvData[o + 8] = LastUpdate.ToString(CultureInfo.InvariantCulture);
        csvData[o + 9] = Available.ToString(CultureInfo.InvariantCulture);
    }

    public bool IncludeInCsv()
    {
        return true;
    }

    public void PopulateData(ExcelModule excelModule, Language language)
    {
    }

    public static MarketPricing FromApi(PricingAPIResponse apiResponse, uint worldId, int saleHistoryLimit)
    {
        MarketPricing response = new MarketPricing();
        // 🔴 averagePrice* 是成交歷史算的,查不到歷史時對方回 0。
        //    把「不知道」寫成 0 會在畫面上被讀成「均價 0 gil」,所以退回
        //    currentAveragePrice*(掛售算的,不依賴成交歷史)。
        response.AveragePriceNq = apiResponse.averagePriceNQ > 0
            ? apiResponse.averagePriceNQ
            : apiResponse.currentAveragePriceNQ;
        response.AveragePriceHq = apiResponse.averagePriceHQ > 0
            ? apiResponse.averagePriceHQ
            : apiResponse.currentAveragePriceHQ;
        response.MinPriceHq = apiResponse.minPriceHQ;
        response.MinPriceNq = apiResponse.minPriceNQ;
        response.ItemId = apiResponse.itemID;
        response.Available = apiResponse.listings?.Length ?? 0;
        response.WorldId = worldId;

        response.listings = apiResponse.listings;
        // 只留最近幾筆:成交數是拿整個時間窗算的(見下),但提示視窗會把每一筆
        // 都畫出來,留全部會長過螢幕,也讓快取白佔記憶體。
        response.recentHistory = apiResponse.recentHistory is { Length: > RetainedHistoryEntries } full
            ? full[..RetainedHistoryEntries]
            : apiResponse.recentHistory;
        int? realMinPriceHq = null;
        int? realMinPriceNq = null;
        if (apiResponse.listings != null && apiResponse.listings.Length != 0)
        {
            foreach (var listing in apiResponse.listings)
            {
                if (listing.hq)
                {
                    if (realMinPriceHq == null || realMinPriceHq > listing.pricePerUnit)
                    {
                        realMinPriceHq = listing.pricePerUnit;
                    }
                }
                else
                {
                    if (realMinPriceNq == null || realMinPriceNq > listing.pricePerUnit)
                    {
                        realMinPriceNq = listing.pricePerUnit;
                    }
                }
            }

            if (realMinPriceHq != null)
            {
                response.MinPriceHq = realMinPriceHq.Value;
            }

            if (realMinPriceNq != null)
            {
                response.MinPriceNq = realMinPriceNq.Value;
            }
        }
        if (apiResponse.recentHistory != null && apiResponse.recentHistory.Length != 0)
        {
            DateTime? latestDate = null;
            int sevenDaySales = 0;
            foreach (var history in apiResponse.recentHistory)
            {
                var dateTime = DateTimeOffset.FromUnixTimeSeconds(history.timestamp).LocalDateTime;
                if (latestDate == null || latestDate <= dateTime)
                {
                    latestDate = dateTime;
                }

                if (dateTime >= DateTime.Now.AddDays(-saleHistoryLimit))
                {
                    sevenDaySales++;
                }
            }

            response.SevenDaySellCount = sevenDaySales;
            response.LastSellDate = latestDate;

        }
        else
        {
            response.LastSellDate = null;
            response.SevenDaySellCount = 0;
        }

        return response;
    }
}