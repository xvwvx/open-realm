using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MdxPreview.Core.Sheet
{
    /// <summary>
    /// One SLK data row: the row key (its first column, <c>X=1</c>) plus the
    /// column-name → value pairs of every populated cell.
    ///
    /// Column lookups are case-insensitive, matching FS_FindSheetCell's
    /// <c>strcasecmp</c> in games/warcraft-3/sheet/sheet.c. Row lookups on the
    /// table are case-insensitive too (the C lookup is <c>strcmp</c>, but the
    /// engine's own consumers compare row names with strcasecmp, e.g.
    /// R_W3EventRow in renderer/r_game.c).
    /// </summary>
    public sealed class SlkRow
    {
        readonly Dictionary<string, string> _fields;

        internal SlkRow(int rowNumber, string key)
        {
            RowNumber = rowNumber;
            Key = key ?? string.Empty;
            _fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>1-based authored row number (row 1 is the header row).</summary>
        public int RowNumber { get; }

        /// <summary>Row key: the value of the first column (<c>X=1</c>).</summary>
        public string Key { get; }

        public IReadOnlyDictionary<string, string> Fields => _fields;

        internal void Add(string column, string value)
        {
            // FS_MakeRowsFromSheet prepends each field node, so FS_FindSheetCell
            // returns the LAST authored value for a repeated (row, column).
            _fields[column] = value;
        }

        /// <summary>Raw cell text; <c>null</c> when the column is absent.</summary>
        public string GetStringOrNull(string column)
        {
            if (string.IsNullOrEmpty(column)) return null;
            return _fields.TryGetValue(column, out string value) ? value : null;
        }

        /// <summary>Raw cell text, or <c>fallback</c> when the column is absent.</summary>
        public string TryGetString(string column, string fallback = "")
            => GetStringOrNull(column) ?? fallback;

        /// <summary>
        /// Cell text parsed as a float with the invariant culture (the C loader
        /// uses <c>atof</c>); <c>fallback</c> when the column is absent or the
        /// text is not a number.
        /// </summary>
        public float GetFloat(string column, float fallback = 0f)
        {
            string text = GetStringOrNull(column);
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : fallback;
        }

        /// <summary>
        /// Cell text parsed as an int with the invariant culture (the C loader
        /// uses <c>atoi</c>, which also tolerates a trailing tail); <c>fallback</c>
        /// when the column is absent or the text has no leading number.
        /// </summary>
        public int GetInt(string column, int fallback = 0)
        {
            string text = GetStringOrNull(column);
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            text = text.Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return value;
            // atoi() stops at the first non-numeric character: "3.5" -> 3, "12abc" -> 12.
            int end = 0;
            if (end < text.Length && (text[end] == '-' || text[end] == '+')) end++;
            int digits = end;
            while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
            if (end == digits) return fallback;
            return int.TryParse(text.AsSpan(0, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        /// <summary>True when the cell exists and holds text that parses as a number.</summary>
        public bool HasColumn(string column) => GetStringOrNull(column) != null;

        public override string ToString() => $"{Key} ({_fields.Count} fields)";
    }

    /// <summary>
    /// Classic SYLK (SLK) spreadsheet reader, a port of
    /// <c>FS_ParseSLK_Buffer</c> / <c>ScanSLKField</c> / <c>FS_FillSheetCell</c> /
    /// <c>FS_MakeRowsFromSheet</c> in games/warcraft-3/sheet/sheet.c.
    ///
    /// Record handling (see docs/games/warcraft-3/file-formats/slk.md):
    ///  - <c>ID;P…</c> is ignored (the C parser never validates the magic line);
    ///  - <c>B;Y…;X…</c> is advisory only and never sizes any storage — the
    ///    shipped tables declare more rows/columns than they populate;
    ///  - <c>C</c> records create a cell, <c>F</c> records only advance the
    ///    current <c>X</c>/<c>Y</c> state;
    ///  - every other record type (and blank lines) is skipped;
    ///  - <c>X</c> and <c>Y</c> are stateful across records and lines: a record
    ///    that omits either coordinate keeps the previous value, including one
    ///    advanced by an <c>F</c> record. Both start at 1 and are re-seeded only
    ///    per buffer;
    ///  - a field is delimited by the next <c>;</c>, except that a <c>K</c>
    ///    field whose value starts with <c>"</c> is quote-aware: semicolons
    ///    inside the quotes are ordinary characters, <c>""</c> decodes to one
    ///    literal quote, and the scan stops at the closing quote (the rest of
    ///    the field up to the next <c>;</c> is discarded, as in ScanSLKField);
    ///  - <c>X&lt;1</c> or <c>Y&lt;1</c> cells are rejected before storage;
    ///  - a repeated (row, column) resolves to the last authored value, because
    ///    FS_MakeRowsFromSheet prepends field nodes and FS_FindSheetCell returns
    ///    the first node of that prepended list.
    ///
    /// Row 1 supplies the column headers; every later populated row becomes a
    /// <see cref="SlkRow"/> keyed by its first column. Rows without a first
    /// column are dropped, exactly like FS_MakeRowsFromSheet.
    /// </summary>
    public sealed class SlkFile
    {
        /// <summary>MAX_SHEET_COLUMNS in games/warcraft-3/sheet/sheet.c.</summary>
        public const int MaxColumns = 256;

        readonly Dictionary<long, string> _cells = new Dictionary<long, string>();
        readonly List<SlkRow> _rows = new List<SlkRow>();
        readonly Dictionary<string, SlkRow> _rowsByKey = new Dictionary<string, SlkRow>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, string> _headers = new Dictionary<int, string>();

        SlkFile(string source = null)
        {
            Source = source ?? string.Empty;
        }

        /// <summary>Diagnostic label (file name or "&lt;buffer&gt;").</summary>
        public string Source { get; private set; }

        /// <summary>Data rows in authored order (row 1, the header row, is excluded).</summary>
        public IReadOnlyList<SlkRow> Rows => _rows;

        public int RowCount => _rows.Count;

        /// <summary>Column headers from row 1, indexed by 1-based column number.</summary>
        public IReadOnlyDictionary<int, string> Headers => _headers;

        /// <summary>False when the buffer held no usable cell (missing file / empty table).</summary>
        public bool IsEmpty => _rows.Count == 0;

        /// <summary>True when the buffer carried a usable <c>ID;…</c> first line.</summary>
        public bool HadIdRecord { get; private set; }

        public static SlkFile Parse(string text, string source = null)
        {
            var file = new SlkFile(source);
            if (string.IsNullOrEmpty(text)) return file;
            file.ParseRecords(text);
            return file;
        }

        /// <summary>Decodes UTF-8/ASCII bytes; a UTF-8 BOM is not treated as line content.</summary>
        public static SlkFile Parse(byte[] data, string source = null)
        {
            if (data == null || data.Length == 0) return new SlkFile(source);
            int offset = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) offset = 3;
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                return Parse(Encoding.Unicode.GetString(data, 2, data.Length - 2), source);
            return Parse(Encoding.UTF8.GetString(data, offset, data.Length - offset), source);
        }

        // ---- lookup ----------------------------------------------------------------------

        /// <summary>Case-insensitive row lookup by key.</summary>
        public bool TryGetRow(string key, out SlkRow row)
        {
            row = null;
            if (string.IsNullOrEmpty(key)) return false;
            return _rowsByKey.TryGetValue(key.Trim(), out row);
        }

        public SlkRow GetRow(string key) => TryGetRow(key, out SlkRow row) ? row : null;

        /// <summary>Raw cell text of one row/column pair, or <c>null</c>.</summary>
        public string GetString(SlkRow row, string column) => row?.GetStringOrNull(column);

        /// <summary>Cell text of a row key / column pair, or <c>fallback</c>.</summary>
        public string GetString(string key, string column, string fallback = "")
            => TryGetRow(key, out SlkRow row) ? row.TryGetString(column, fallback) : fallback;

        public float GetFloat(SlkRow row, string column, float fallback = 0f)
            => row?.GetFloat(column, fallback) ?? fallback;

        public float GetFloat(string key, string column, float fallback = 0f)
            => TryGetRow(key, out SlkRow row) ? row.GetFloat(column, fallback) : fallback;

        public int GetInt(SlkRow row, string column, int fallback = 0)
            => row?.GetInt(column, fallback) ?? fallback;

        public int GetInt(string key, string column, int fallback = 0)
            => TryGetRow(key, out SlkRow row) ? row.GetInt(column, fallback) : fallback;

        public override string ToString()
            => $"{Source}: {_rows.Count} rows, {_cells.Count} cells";

        /// <summary>Debug helper: every stored cell as <c>Y&lt;row&gt;X&lt;col&gt;=value</c>.</summary>
        public string DumpCells()
        {
            var keys = new List<long>(_cells.Keys);
            keys.Sort();
            var text = new StringBuilder();
            foreach (long key in keys)
                text.Append($"Y{key >> 32}X{key & 0xFFFFFFFF}=\"{_cells[key]}\" ");
            return text.ToString().TrimEnd();
        }

        // ---- parser ----------------------------------------------------------------------

        void ParseRecords(string text)
        {
            int position = 0;
            int length = text.Length;
            int x = 1, y = 1;
            bool firstLine = true;

            while (position < length)
            {
                int lineStart = position;
                while (position < length && text[position] != '\n' && text[position] != '\r') position++;
                int lineEnd = position;
                // FS_ParseSLK_Buffer skips every consecutive CR/LF, so a CRLF or
                // LFCR pair advances past both characters.
                while (position < length && (text[position] == '\r' || text[position] == '\n')) position++;

                if (lineEnd <= lineStart) continue;

                char recordType = text[lineStart];
                if (firstLine)
                {
                    firstLine = false;
                    // Observed-coordinate parsing does not need the magic line, but
                    // report whether it was the familiar ID record.
                    HadIdRecord = recordType == 'I';
                }

                // ID/B/E/P/... neither create cells nor move the cursor.
                if (recordType != 'C' && recordType != 'F') continue;

                // FS_ParseSLK_Buffer keeps X and Y in function scope: both are
                // stateful across records and lines, so a record that omits a
                // coordinate keeps the previous value (including one advanced by
                // an F record).
                int cursor = lineStart + 1;
                if (cursor < lineEnd && text[cursor] == ';') cursor++;

                var field = new StringBuilder(64);
                while (ScanField(text, ref cursor, lineEnd, field))
                {
                    if (field.Length == 0) continue;
                    switch (field[0])
                    {
                        case 'X':
                            x = Atoi(field, 1);
                            break;
                        case 'Y':
                            y = Atoi(field, 1);
                            break;
                        case 'K':
                            // An F record advances X/Y but never stores a cell.
                            if (recordType == 'C' && x >= 1 && y >= 1)
                                StoreCell(x, y, field.ToString(1, field.Length - 1));
                            break;
                    }
                }
            }

            BuildRows();
        }

        /// <summary>
        /// ScanSLKField: reads one semicolon-delimited field, with the quote-aware
        /// branch for <c>K"…"</c> values. Returns false when the line is exhausted.
        /// </summary>
        static bool ScanField(string text, ref int cursor, int lineEnd, StringBuilder field)
        {
            field.Clear();
            if (cursor >= lineEnd) return false;
            char first = text[cursor];
            if (first == '\r' || first == '\n') return false;
            field.Append(first);
            cursor++;

            if (first == 'K' && cursor < lineEnd && text[cursor] == '"')
            {
                cursor++; // opening quote
                while (cursor < lineEnd)
                {
                    char c = text[cursor];
                    if (c == '\r' || c == '\n') break;
                    if (c == '"')
                    {
                        if (cursor + 1 < lineEnd && text[cursor + 1] == '"')
                        {
                            field.Append('"'); // "" -> literal quote
                            cursor += 2;
                            continue;
                        }
                        cursor++; // closing quote; trailing text before ';' is dropped
                        break;
                    }
                    field.Append(c);
                    cursor++;
                }
            }
            else
            {
                while (cursor < lineEnd)
                {
                    char c = text[cursor];
                    if (c == ';' || c == '\r' || c == '\n') break;
                    field.Append(c);
                    cursor++;
                }
            }

            if (cursor < lineEnd && text[cursor] == ';') cursor++;
            return true;
        }

        /// <summary>atoi(): leading whitespace, optional sign, then digits; 0 when none.</summary>
        static int Atoi(StringBuilder text, int start)
        {
            int i = start;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int sign = 1;
            if (i < text.Length && (text[i] == '-' || text[i] == '+'))
            {
                if (text[i] == '-') sign = -1;
                i++;
            }
            long value = 0;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                value = value * 10 + (text[i] - '0');
                if (value > int.MaxValue) return sign < 0 ? int.MinValue : int.MaxValue;
                i++;
            }
            return (int)(sign * value);
        }

        /// <summary>
        /// FS_FillSheetCell: every C record appends a cell node, including a
        /// second write to the same (row, column).
        /// </summary>
        void StoreCell(int x, int y, string value)
        {
            long key = ((long)y << 32) | (uint)x;
            _cells[key] = value;
        }

        /// <summary>FS_MakeRowsFromSheet: header row + one row per named data row.</summary>
        void BuildRows()
        {
            if (_cells.Count == 0) return;

            int maxRow = 0;
            foreach (long key in _cells.Keys)
            {
                int row = (int)(key >> 32);
                int column = (int)(key & 0xFFFFFFFF);
                if (row == 1 && column < MaxColumns) _headers[column] = _cells[key];
                if (row > maxRow) maxRow = row;
            }
            if (maxRow < 2) return;

            // Rows are emitted in authored row number order, like the
            // rows_by_number walk in FS_MakeRowsFromSheet.
            for (int rowNumber = 2; rowNumber <= maxRow; rowNumber++)
            {
                string name = null;
                var fields = new List<KeyValuePair<string, string>>();
                for (int column = 1; column < MaxColumns; column++)
                {
                    long cellKey = ((long)rowNumber << 32) | (uint)column;
                    if (!_cells.TryGetValue(cellKey, out string value)) continue;
                    if (column == 1)
                    {
                        name = value;
                    }
                    else if (_headers.TryGetValue(column, out string header))
                    {
                        fields.Add(new KeyValuePair<string, string>(header, value));
                    }
                }
                if (name == null) continue; // rows without a first column are dropped

                var row = new SlkRow(rowNumber, name);
                foreach (KeyValuePair<string, string> field in fields) row.Add(field.Key, field.Value);
                _rows.Add(row);
                // FS_LoadSheetTyped skips a row whose name it already emitted, but
                // FS_FindSheetCell still scans the whole cell list, so a repeated
                // row key resolves to the last authored row's fields.
                _rowsByKey[name] = row;
            }
        }
    }
}
