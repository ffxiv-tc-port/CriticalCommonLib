namespace CriticalCommonLib.Helpers;

/// <summary>
/// 能把一列直接寫進 <see cref="CsvRowWriter.FieldWriter"/> 的列：寫出的欄位必須與 <c>ICsv.ToCsv()</c> 逐欄相同
/// （同樣的 ToString 格式與文化設定）。
/// </summary>
internal interface ICsvFields
{
    void WriteCsvRow(CsvRowWriter.FieldWriter row);
}
