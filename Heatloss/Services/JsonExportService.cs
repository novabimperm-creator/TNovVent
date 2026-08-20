using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace QOVETER.Services
{
    /// <summary>
    /// Экспорт результатов расчёта в JSON для интеграции с другими системами
    /// (например, расчётом отопления, BIM-координацией, аналитикой).
    /// </summary>
    public class JsonExportService
    {
        public void Export(
            List<CalculationResult> results,
            string filePath,
            ExcelExportParams parameters = null)
        {
            if (results == null || results.Count == 0)
                throw new ArgumentException("Список результатов пуст", nameof(results));

            var dto = new HeatLossReportDto
            {
                GeneratedAt  = DateTime.Now,
                City         = parameters?.City ?? "",
                InternalTemp = parameters?.InternalTemp ?? 20,
                ExternalTemp = parameters?.ExternalTemp ?? -25,
                Rooms = results
                    .Where(r => !r.IsSummary && string.IsNullOrEmpty(r.ErrorMessage))
                    .Select(MapRoom)
                    .ToList()
            };

            var summary = results.FirstOrDefault(r => r.IsSummary);
            if (summary != null)
            {
                dto.Total = new TotalDto
                {
                    Area    = summary.Area,
                    Q_ogr   = summary.Q_ogr,
                    Q_vent  = summary.Q_vent,
                    Q_inf   = summary.Q_inf,
                    Q_total = summary.Q_total,
                    Q_final = summary.Q_final
                };
            }

            EnsureDirectoryExists(filePath);

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var serializer = new DataContractJsonSerializer(
                typeof(HeatLossReportDto),
                new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true,
                    DateTimeFormat = new DateTimeFormat("yyyy-MM-ddTHH:mm:ss")
                });

            using (var memory = new MemoryStream())
            {
                serializer.WriteObject(memory, dto);
                string raw = Encoding.UTF8.GetString(memory.ToArray());
                // DataContractJsonSerializer пишет в одну строку — для читаемости форматируем
                File.WriteAllText(filePath, PrettyPrint(raw), Encoding.UTF8);
            }

            Logger.Info($"JsonExport: отчёт сохранён в {filePath} ({dto.Rooms.Count} помещений)");
        }

        private static RoomDto MapRoom(CalculationResult r) => new RoomDto
        {
            Name        = r.RoomData?.Name ?? r.RoomName,
            Number      = r.RoomData?.Number,
            Type        = r.RoomData?.Type,
            // Номер квартиры есть в Excel-отчёте, но не было в JSON — а JSON как раз
            // и предназначен для передачи в смежные системы, где группировка по
            // квартирам нужна в первую очередь.
            Apartment   = r.Apartment ?? "",
            Floor       = r.FloorNumber,
            Orientation = r.Orientation,
            Area        = r.Area,
            WallArea    = r.WallArea,
            WindowArea  = r.WindowArea,
            Q_ogr       = r.Q_ogr,
            Q_vent      = r.Q_vent,
            Q_inf       = r.Q_inf,
            Q_vn        = r.Q_vn,
            Q_total     = r.Q_total,
            Q_final     = r.Q_final
        };

        private static void EnsureDirectoryExists(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// Минимальная форматтер для JSON: расставляет переводы строк после {, [, ; чтобы файл
        /// был хоть как-то читаем. Полноценный pretty-print не нужен — целевой потребитель —
        /// машина (другая система); человек может открыть в любом онлайн-форматтере.
        /// </summary>
        private static string PrettyPrint(string raw)
        {
            var sb = new StringBuilder(raw.Length + 64);
            int indent = 0;
            bool inString = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"' && (i == 0 || raw[i - 1] != '\\')) inString = !inString;

                if (!inString)
                {
                    if (c == '{' || c == '[')
                    {
                        sb.Append(c);
                        indent++;
                        sb.Append('\n').Append(' ', indent * 2);
                        continue;
                    }
                    if (c == '}' || c == ']')
                    {
                        indent--;
                        sb.Append('\n').Append(' ', indent * 2);
                        sb.Append(c);
                        continue;
                    }
                    if (c == ',')
                    {
                        sb.Append(c);
                        sb.Append('\n').Append(' ', indent * 2);
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    [DataContract]
    public class HeatLossReportDto
    {
        [DataMember(Order = 1)] public DateTime GeneratedAt { get; set; }
        [DataMember(Order = 2)] public string City { get; set; }
        [DataMember(Order = 3)] public double InternalTemp { get; set; }
        [DataMember(Order = 4)] public double ExternalTemp { get; set; }
        [DataMember(Order = 5)] public List<RoomDto> Rooms { get; set; } = new List<RoomDto>();
        [DataMember(Order = 6)] public TotalDto Total { get; set; }
    }

    [DataContract]
    public class RoomDto
    {
        [DataMember(Order = 1)] public string Name { get; set; }
        [DataMember(Order = 2)] public string Number { get; set; }
        [DataMember(Order = 3)] public string Type { get; set; }
        /// <summary>Номер квартиры; пусто — общедомовое помещение.</summary>
        [DataMember(Order = 4)] public string Apartment { get; set; }
        [DataMember(Order = 5)]  public int Floor { get; set; }
        [DataMember(Order = 6)]  public string Orientation { get; set; }
        [DataMember(Order = 7)]  public double Area { get; set; }
        [DataMember(Order = 8)]  public double WallArea { get; set; }
        [DataMember(Order = 9)]  public double WindowArea { get; set; }
        [DataMember(Order = 10)] public double Q_ogr { get; set; }
        [DataMember(Order = 11)] public double Q_vent { get; set; }
        [DataMember(Order = 12)] public double Q_inf { get; set; }
        [DataMember(Order = 13)] public double Q_vn { get; set; }
        [DataMember(Order = 14)] public double Q_total { get; set; }
        [DataMember(Order = 15)] public double Q_final { get; set; }
    }

    [DataContract]
    public class TotalDto
    {
        [DataMember(Order = 1)] public double Area { get; set; }
        [DataMember(Order = 2)] public double Q_ogr { get; set; }
        [DataMember(Order = 3)] public double Q_vent { get; set; }
        [DataMember(Order = 4)] public double Q_inf { get; set; }
        [DataMember(Order = 5)] public double Q_total { get; set; }
        [DataMember(Order = 6)] public double Q_final { get; set; }
    }
}
