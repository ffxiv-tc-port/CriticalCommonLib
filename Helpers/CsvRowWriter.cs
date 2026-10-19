using System;
using System.Collections.Generic;
using System.IO;
using CSVFile;
using LuminaSupplemental.Excel.Model;

namespace CriticalCommonLib.Helpers;

/// <summary>
/// 把 <see cref="ICsv.ToCsv"/> 的字串欄位逐欄寫進 <see cref="StreamWriter"/>：輸出與 CSVFile 的
/// <c>new CSVWriter(dest).WriteLine(row)</c>（預設 <see cref="CSVSettings.CSV"/>）逐位元組相同，
/// 但不為每列配置 StringBuilder、整列字串與列舉器。
/// 只用區域狀態，可在任何執行緒呼叫；同一個 <see cref="StreamWriter"/> 不可並行寫。
/// </summary>
public static class CsvRowWriter
{
    public static void WriteRows<T>(StreamWriter dest, List<T> items) where T : ICsv
    {
        // 與 CSVWriter 建構子相同：設定物件取參考、危險字元與強制引號型別各算一次。
        var settings = CSVSettings.CSV;
        var riskyChars = settings.GetRiskyChars();
        var forceQualifierTypes = settings.GetForceQualifierTypes();
        FieldWriter? fieldWriter = null;
        foreach (T obj in items)
        {
            if (obj.IncludeInCsv())
            {
                if (obj is ICsvFields direct)
                {
                    fieldWriter ??= new FieldWriter(dest, settings, riskyChars, forceQualifierTypes);
                    direct.WriteCsvRow(fieldWriter);
                    fieldWriter.EndRow();
                }
                else
                {
                    WriteLine(dest, obj.ToCsv(), settings, riskyChars, forceQualifierTypes);
                }
            }
        }
    }

    /// <summary>
    /// 開檔與失敗語意照 LuminaSupplemental 的 <c>CsvLoader.ToCsvRaw</c>：直接覆寫目標檔、任何例外回 false。
    /// </summary>
    public static bool ToCsvRaw<T>(List<T> items, string filePath) where T : ICsv
    {
        try
        {
            using StreamWriter dest = new StreamWriter(filePath);
            WriteRows(dest, items);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 輸出與 <see cref="ToCsvRaw{T}"/> 相同，但先寫同目錄的 <c>.tmp</c>、落盤後才取代正式檔。
    /// 成功回 null；任何一步失敗（含欄位有落單代理字元時 UTF-8 編碼擲例外）回例外，暫存檔刪掉、正式檔保持原內容。
    /// 兩個呼叫重疊時後到的失敗（暫存檔是獨占開啟）。
    /// </summary>
    public static Exception? ToCsvRawAtomic<T>(List<T> items, string filePath) where T : ICsv
    {
        var tempPath = filePath + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter dest = new StreamWriter(stream))
            {
                WriteRows(dest, items);
                dest.Flush();
                stream.Flush(true);
            }

            File.Move(tempPath, filePath, true);
            return null;
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception)
            {
                // 刻意不處理：刪不掉的暫存檔下一次寫入會覆寫。
            }

            return ex;
        }
    }

    // 規則逐條照 CSVFile 3.2 的 CSV.ItemsToCsv 處理字串欄位的分支。
    private static void WriteLine(StreamWriter dest, string[] fields, CSVSettings settings, char[] riskyChars, Dictionary<Type, int>? forceQualifierTypes)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i != 0)
            {
                dest.Write(settings.FieldDelimiter);
            }

            var text = fields[i];
            if (text == null)
            {
                if (settings.AllowNull)
                {
                    dest.Write(settings.NullToken);
                }

                continue;
            }

            var qualifier = settings.TextQualifier;
            var qualify = settings.ForceQualifiers || text.IndexOfAny(riskyChars) >= 0 || (forceQualifierTypes?.ContainsKey(typeof(string)) ?? false);
            if (qualify)
            {
                dest.Write(qualifier);
            }

            var rest = text.AsSpan();
            int at;
            while ((at = rest.IndexOf(qualifier)) >= 0)
            {
                dest.Write(rest.Slice(0, at + 1));
                dest.Write(qualifier);
                rest = rest.Slice(at + 1);
            }

            dest.Write(rest);
            if (qualify)
            {
                dest.Write(qualifier);
            }
        }

        dest.Write(settings.LineSeparator);
    }

    /// <summary>
    /// <see cref="ICsvFields"/> 的列逐欄寫進這裡：整列先在自己的緩衝組好（數字直接格式化、不建字串），
    /// <see cref="EndRow"/> 才一次交給 StreamWriter，所以列中途擲例外時這一列一個字都不會寫出（同 ToCsv 先建陣列的寫法）。
    /// 分隔、引號、null 的規則同 <see cref="WriteLine"/>。每次存檔建一個，不可跨執行緒共用。
    /// </summary>
    internal sealed class FieldWriter
    {
        private readonly StreamWriter _dest;
        private readonly CSVSettings _settings;
        private readonly char[] _riskyChars;
        private readonly Dictionary<Type, int>? _forceQualifierTypes;
        private char[] _row = new char[512];
        private char[] _scratch = new char[128];
        private int _length;
        private bool _first = true;

        public FieldWriter(StreamWriter dest, CSVSettings settings, char[] riskyChars, Dictionary<Type, int>? forceQualifierTypes)
        {
            _dest = dest;
            _settings = settings;
            _riskyChars = riskyChars;
            _forceQualifierTypes = forceQualifierTypes;
        }

        public void Text(string? text)
        {
            var start = BeginField();
            if (text == null)
            {
                if (_settings.AllowNull)
                {
                    Append(_settings.NullToken);
                }

                return;
            }

            Append(text);
            EndField(start);
        }

        /// <summary>同 <c>value.ToString(provider)</c>；provider 為 null＝無參數的 <c>ToString()</c>（目前文化）。</summary>
        public void Formatted<TValue>(TValue value, IFormatProvider? provider = null) where TValue : ISpanFormattable
        {
            var start = BeginField();
            AppendFormatted(value, provider);
            EndField(start);
        }

        /// <summary>同 <c>String.Join(";", values.Select(c => c.ToString()))</c>。</summary>
        public void Joined(uint[] values)
        {
            var start = BeginField();
            for (var i = 0; i < values.Length; i++)
            {
                if (i != 0)
                {
                    Append(';');
                }

                AppendFormatted(values[i], null);
            }

            EndField(start);
        }

        /// <summary>同 <c>String.Join(";", values.Select(c => c.ToString()))</c>，元素是 null 時同樣擲 NullReferenceException。</summary>
        public void Joined(string[] values)
        {
            var start = BeginField();
            for (var i = 0; i < values.Length; i++)
            {
                if (i != 0)
                {
                    Append(';');
                }

                Append(values[i] ?? throw new NullReferenceException());
            }

            EndField(start);
        }

        public void EndRow()
        {
            Append(_settings.LineSeparator);
            _dest.Write(_row, 0, _length);
            _length = 0;
            _first = true;
        }

        private int BeginField()
        {
            if (!_first)
            {
                Append(_settings.FieldDelimiter);
            }

            _first = false;
            return _length;
        }

        // 把剛寫進緩衝的欄位改寫成 WriteLine 的樣子：需要時前後加引號、每個引號加倍。
        private void EndField(int start)
        {
            var qualifier = _settings.TextQualifier;
            var text = _row.AsSpan(start, _length - start);
            var qualify = _settings.ForceQualifiers || text.IndexOfAny(_riskyChars) >= 0 || (_forceQualifierTypes?.ContainsKey(typeof(string)) ?? false);
            var quotes = text.Count(qualifier);
            if (!qualify && quotes == 0)
            {
                return;
            }

            var length = text.Length;
            if (_scratch.Length < length)
            {
                _scratch = new char[Math.Max(length, _scratch.Length * 2)];
            }

            text.CopyTo(_scratch);
            _length = start;
            Reserve(length + quotes + 2);
            if (qualify)
            {
                _row[_length++] = qualifier;
            }

            for (var i = 0; i < length; i++)
            {
                var c = _scratch[i];
                _row[_length++] = c;
                if (c == qualifier)
                {
                    _row[_length++] = qualifier;
                }
            }

            if (qualify)
            {
                _row[_length++] = qualifier;
            }
        }

        private void AppendFormatted<TValue>(TValue value, IFormatProvider? provider) where TValue : ISpanFormattable
        {
            int written;
            while (!value.TryFormat(_row.AsSpan(_length), out written, default, provider))
            {
                Reserve(_row.Length - _length + 1);
            }

            _length += written;
        }

        private void Append(char c)
        {
            Reserve(1);
            _row[_length++] = c;
        }

        private void Append(string? s)
        {
            if (s == null)
            {
                return;
            }

            Reserve(s.Length);
            s.CopyTo(0, _row, _length, s.Length);
            _length += s.Length;
        }

        private void Reserve(int extra)
        {
            if (_length + extra > _row.Length)
            {
                Array.Resize(ref _row, Math.Max(_length + extra, _row.Length * 2));
            }
        }
    }
}
