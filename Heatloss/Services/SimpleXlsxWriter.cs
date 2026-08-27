using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace QOVETER.Services
{
    /// <summary>Один лист простой книги: имя и строки ячеек.</summary>
    public class SheetData
    {
        public SheetData(string name) { Name = name; }

        public string Name { get; }

        /// <summary>Строки. Ячейка — число (double/int) либо строка; null — пустая.</summary>
        public List<object[]> Rows { get; } = new List<object[]>();

        /// <summary>Сколько первых строк оформить жирным (шапка таблицы).</summary>
        public int BoldRows { get; set; }

        /// <summary>Строки, которые тоже жирные, помимо первых <see cref="BoldRows"/>.</summary>
        public HashSet<int> ExtraBoldRows { get; } = new HashSet<int>();

        public SheetData Add(params object[] cells)
        {
            Rows.Add(cells ?? new object[0]);
            return this;
        }

        /// <summary>Пустая строка-разделитель.</summary>
        public SheetData Blank()
        {
            Rows.Add(new object[0]);
            return this;
        }

        /// <summary>Сделать последнюю добавленную строку жирной.</summary>
        public SheetData Bold()
        {
            if (Rows.Count > 0) ExtraBoldRows.Add(Rows.Count - 1);
            return this;
        }
    }

    /// <summary>
    /// Запись книги .xlsx БЕЗ единой сторонней сборки — только то, что есть
    /// в .NET Framework: <see cref="ZipArchive"/> и генерация XML строками.
    ///
    /// <para><b>Зачем понадобилось.</b> 2026-08-26 на большом проекте расчёт
    /// отработал, а кнопка «Excel» ответила «Не удалось загрузить файл или сборку
    /// ClosedXML… либо одну из их зависимостей»: рядом с DLL плагина не оказалось
    /// библиотек экспорта. Аварийный CSV результат спасал, но инженер просил
    /// именно книгу — CSV не держит ни листов, ни ширины колонок, а «По квартирам»
    /// и «Параметры» в нём терялись совсем.</para>
    ///
    /// <para><b>Что это НЕ заменяет.</b> Оформление отчёта (заливки, рамки,
    /// условное выделение спорных ячеек, примечания) остаётся за
    /// <see cref="ExcelExportService"/> и ClosedXML. Здесь — тот же состав листов
    /// и те же числа, но простым текстом: жирная шапка, ширина колонок и всё.
    /// Это запасной путь, который обязан работать всегда, а не второй отчёт.</para>
    ///
    /// <para><b>Почему inline-строки, а не sharedStrings.</b> Общая таблица строк
    /// экономит размер, но добавляет второй проход и ещё одну часть пакета,
    /// в которой можно ошибиться. Отчёт на 2 350 помещений — это единицы мегабайт;
    /// цена размера здесь ниже цены ошибки в формате, после которой Excel
    /// откажется открывать файл целиком.</para>
    /// </summary>
    public static class SimpleXlsxWriter
    {
        /// <summary>Числа пишутся в файл ТОЛЬКО инвариантной культурой: это формат, а не отображение.</summary>
        private static readonly CultureInfo Xml = CultureInfo.InvariantCulture;

        /// <summary>Предел ширины колонки в символах — чтобы состав слоёв не растянул лист.</summary>
        private const double MaxColumnWidth = 60;

        public static void Write(string path, IList<SheetData> sheets)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            if (sheets == null || sheets.Count == 0)
                throw new ArgumentException("Книга без листов", nameof(sheets));

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var names = UniqueSheetNames(sheets);

            // Пишем во временный файл рядом и подменяем: прерванная запись не должна
            // оставить полуготовую книгу под именем готового отчёта.
            string temp = path + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Put(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
                Put(zip, "_rels/.rels", RootRels());
                Put(zip, "xl/workbook.xml", Workbook(names));
                Put(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
                Put(zip, "xl/styles.xml", Styles());

                for (int i = 0; i < sheets.Count; i++)
                    Put(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i]));
            }

            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);

            Logger.Info($"Книга собрана без сторонних библиотек: {sheets.Count} листов, файл {path}");
        }

        private static void Put(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                writer.Write(content);
        }

        /// <summary>
        /// Имена листов Excel: до 31 символа, без <c>[ ] : * ? / \</c> и без повторов.
        /// Нарушение любого из правил — книга, которую Excel откажется открыть.
        /// </summary>
        private static List<string> UniqueSheetNames(IList<SheetData> sheets)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var sheet in sheets)
            {
                var clean = new string((sheet.Name ?? "Лист")
                    .Select(c => "[]:*?/\\".IndexOf(c) >= 0 ? ' ' : c).ToArray()).Trim();
                if (clean.Length == 0) clean = "Лист";
                if (clean.Length > 31) clean = clean.Substring(0, 31);

                string candidate = clean;
                for (int n = 2; used.Contains(candidate); n++)
                {
                    string suffix = " " + n;
                    candidate = clean.Length + suffix.Length > 31
                        ? clean.Substring(0, 31 - suffix.Length) + suffix
                        : clean + suffix;
                }

                used.Add(candidate);
                result.Add(candidate);
            }

            return result;
        }

        private static string ContentTypes(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string RootRels()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                   "</Relationships>";
        }

        private static string Workbook(IList<string> names)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
            sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (int i = 0; i < names.Count; i++)
                sb.Append($"<sheet name=\"{Escape(names[i])}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string WorkbookRels(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
            // Стили идут ПОСЛЕ листов: их rId не должен занять номер листа,
            // иначе книга ссылается на лист, которого нет.
            sb.Append($"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        /// <summary>
        /// Минимальные стили: обычный текст и жирный. Индекс 1 в <c>cellXfs</c> —
        /// это <c>s="1"</c> в ячейке.
        /// </summary>
        private static string Styles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                   "<fonts count=\"2\">" +
                   "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
                   "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
                   "</fonts>" +
                   "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill>" +
                   "<fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                   "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                   "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                   "<cellXfs count=\"2\">" +
                   "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                   "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
                   "</cellXfs>" +
                   "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                   "</styleSheet>";
        }

        private static string Sheet(SheetData sheet)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            sb.Append(Columns(sheet));
            sb.Append("<sheetData>");

            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                var cells = sheet.Rows[r] ?? new object[0];
                if (cells.Length == 0) continue;   // пустую строку Excel рисует сам

                bool bold = r < sheet.BoldRows || sheet.ExtraBoldRows.Contains(r);

                sb.Append($"<row r=\"{r + 1}\">");
                for (int c = 0; c < cells.Length; c++)
                {
                    string reference = ColumnName(c) + (r + 1);
                    sb.Append(Cell(reference, cells[c], bold));
                }
                sb.Append("</row>");
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        /// <summary>
        /// Ширины колонок по самому длинному содержимому. Без них лист открывается
        /// со стандартными 8,43 символа, и все числа читаются как «#####».
        /// </summary>
        private static string Columns(SheetData sheet)
        {
            int columnCount = sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r?.Length ?? 0);
            if (columnCount == 0) return "";

            var sb = new StringBuilder("<cols>");
            for (int c = 0; c < columnCount; c++)
            {
                double width = 8.43;
                foreach (var row in sheet.Rows)
                {
                    if (row == null || c >= row.Length || row[c] == null) continue;
                    width = Math.Max(width, Text(row[c]).Length + 2);
                }
                sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{Math.Min(width, MaxColumnWidth).ToString("0.##", Xml)}\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
            return sb.ToString();
        }

        private static string Cell(string reference, object value, bool bold)
        {
            string style = bold ? " s=\"1\"" : "";

            if (value == null) return $"<c r=\"{reference}\"{style}/>";

            if (value is double || value is float || value is decimal ||
                value is int || value is long || value is short)
            {
                double number = Convert.ToDouble(value, Xml);

                // NaN и бесконечность в книгу писать нельзя: Excel считает такой
                // файл повреждённым и отказывается открывать его ЦЕЛИКОМ — то есть
                // одно испорченное число уносит весь отчёт.
                if (double.IsNaN(number) || double.IsInfinity(number))
                    return $"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t>—</t></is></c>";

                return $"<c r=\"{reference}\"{style}><v>{number.ToString("0.############", Xml)}</v></c>";
            }

            string text = Text(value);
            if (text.Length == 0) return $"<c r=\"{reference}\"{style}/>";

            // xml:space="preserve" — иначе Excel съедает ведущие пробелы отступов.
            return $"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(text)}</t></is></c>";
        }

        private static string Text(object value)
        {
            if (value == null) return "";
            if (value is double || value is float || value is decimal)
                return Convert.ToDouble(value, Xml).ToString("0.##", Xml);
            return value.ToString();
        }

        /// <summary>Имя колонки: 0 → A, 25 → Z, 26 → AA.</summary>
        internal static string ColumnName(int index)
        {
            var sb = new StringBuilder();
            for (int i = index; i >= 0; i = i / 26 - 1)
            {
                sb.Insert(0, (char)('A' + i % 26));
                if (i < 26) break;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Экранирование для XML. Имена помещений в моделях содержат и «&amp;»,
        /// и кавычки, и угловые скобки; вдобавок встречаются управляющие символы,
        /// которых в XML 1.0 не существует вовсе — их приходится выбрасывать,
        /// иначе книга не откроется.
        /// </summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '&':  sb.Append("&amp;");  break;
                    case '<':  sb.Append("&lt;");   break;
                    case '>':  sb.Append("&gt;");   break;
                    case '"':  sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        if (c == '\t' || c == '\n' || c == '\r' || c >= ' ') sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
