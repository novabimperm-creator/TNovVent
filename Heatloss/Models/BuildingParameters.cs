using QOVETER.Services;
using System.Collections.Generic;
using System.Linq;

namespace QOVETER.Models
{
    /// <summary>
    /// Параметры здания для расчета теплопотерь
    /// </summary>
    public class BuildingParameters
    {
        /// <summary>Общая высота здания, м. Используется в расчёте β для наружных дверей.</summary>
        public double TotalHeight { get; set; } = 10.0;
        public string SelectedCity { get; set; } = "Москва";
        public bool IsManualOrientation { get; set; } = false;

        /// <summary>
        /// Ключ <see cref="CommonOrientationKey"/> = ориентация, заданная на всё здание.
        /// Прочие ключи — <c>LevelId</c> уровня, если ориентация задана поуровнево.
        /// Идентификаторы уровней Revit нулём не бывают, коллизии с общим ключом нет.
        /// </summary>
        public Dictionary<int, string> LevelOrientations { get; set; } = new Dictionary<int, string>();

        /// <summary>Ключ «общей» ориентации на всё здание в <see cref="LevelOrientations"/>.</summary>
        public const int CommonOrientationKey = 0;

        /// <summary>
        /// Ориентация для уровня: сначала собственная запись уровня, затем общая по зданию.
        /// Раньше движок всегда брал ключ 0, из-за чего поуровневая настройка молча
        /// игнорировалась бы, как только её начали бы заполнять.
        /// </summary>
        public string GetOrientation(int levelId, string fallback)
        {
            string orientation;
            if (levelId != CommonOrientationKey &&
                LevelOrientations.TryGetValue(levelId, out orientation) &&
                !string.IsNullOrWhiteSpace(orientation))
            {
                return orientation;
            }

            if (LevelOrientations.TryGetValue(CommonOrientationKey, out orientation) &&
                !string.IsNullOrWhiteSpace(orientation))
            {
                return orientation;
            }

            return fallback;
        }
        public int TopFloorNumber { get; set; } = 1;
        public int GroundFloorNumber { get; set; } = 1;

        /// <summary>
        /// Границы этажности заданы инженером вручную («Настройки ориентации»).
        /// Пока false — они определяются автоматически при каждом сборе помещений.
        /// </summary>
        public bool IsFloorRangeManual { get; set; } = false;

        /// <summary>
        /// Определяет номера нижнего и верхнего этажей по фактически собранным помещениям.
        ///
        /// Зачем: оба поля по умолчанию равны 1 и до 2026-08-06 задавались ТОЛЬКО через
        /// диалог «Настройки ориентации». Если его не открыть, этаж 1 оказывался
        /// одновременно первым и последним — получал теплопотери и через пол в грунт,
        /// и через кровлю, которой над ним нет, а настоящий верхний этаж не получал
        /// кровельных потерь вовсе. На прогоне Секции 2 это давало 189,6 Вт/м² на
        /// первом этаже против 46-51 Вт/м² на типовых.
        ///
        /// Границы берутся по ОТМЕТКЕ уровня, а не по min/max <c>FloorNumber</c>:
        /// номер этажа вытаскивается регуляркой из имени уровня, и у «Подвала» или
        /// «Кровли» он вырождается в 1 — сортировка по числу поставила бы их вровень
        /// с первым этажом.
        ///
        /// ПОДЗЕМНЫЕ этажи нижней границей не становятся. Теплопотери подвала в этом
        /// расчёте не считаются (<see cref="RoomData.IsUnderground"/> снимает их
        /// с расчёта в MainWindow.LoadData), поэтому низом расчётного объёма служит
        /// пол первого НАДЗЕМНОГО этажа — он и есть ограждение, отделяющее расчёт
        /// от грунта либо от неотапливаемого подвала. Оба случая покрываются
        /// <c>ThermalConstants.FloorUDefault</c> = 0.23 по СП 50.13330.
        ///
        /// Без этого правила ловушка захлопывалась молча: автоопределение ставило
        /// GroundFloorNumber = −1 (журнал 2026-08-06, 16:29 «нижний этаж -1»),
        /// IsFirstFloor доставался только подвалу, подвал был снят с расчёта — и
        /// теплопотери через пол не получал НИКТО. Для первого этажа Секции 2
        /// (350 м²) это около 1,2 кВт, ~4–5% его теплопотерь.
        /// </summary>
        /// <returns>true, если границы были определены по помещениям.</returns>
        public bool AutoDetectFloorRange(IEnumerable<RoomData> rooms)
        {
            if (IsFloorRangeManual || rooms == null) return false;

            var levels = rooms
                .GroupBy(r => r.LevelId)
                .Select(g => g.First())
                .OrderBy(r => r.Elevation)
                .ToList();

            if (levels.Count == 0) return false;

            // Нижний этаж — самый нижний НАДЗЕМНЫЙ уровень. Если надземных нет вовсе
            // (чисто подземный фрагмент модели), берём самый нижний, какой есть:
            // молча оставлять здание без пола хуже, чем посчитать подвал приближённо.
            var lowestAboveGround = levels.FirstOrDefault(r => !r.IsUnderground);
            if (lowestAboveGround == null)
            {
                lowestAboveGround = levels.First();
                Logger.Warn(
                    "AutoDetectFloorRange: надземных уровней не найдено, нижним этажом " +
                    $"взят «{lowestAboveGround.LevelName}» (этаж {lowestAboveGround.FloorNumber}). " +
                    "Заглублённые ограждения считаются по температуре наружного воздуха " +
                    "и без зонального метода — числа будут завышены.");
            }
            else if (levels.Any(r => r.IsUnderground))
            {
                Logger.Info(
                    $"Нижний этаж расчёта — {lowestAboveGround.FloorNumber} " +
                    $"(«{lowestAboveGround.LevelName}»): подземные уровни пропущены, " +
                    "теплопотери через пол считаются по нему");
            }

            GroundFloorNumber = lowestAboveGround.FloorNumber;
            TopFloorNumber    = levels.Last().FloorNumber;
            return true;
        }

        /// <summary>Высота типового этажа, м. Используется как fallback для UnboundedHeight помещений.</summary>
        public double FloorHeight { get; set; } = 3.0;
        
        // Коэффициенты для наружных дверей по ТЗ
        public Dictionary<string, double> DoorBetaCoefficients { get; set; } = new Dictionary<string, double>
        {
            { "Тройные двери с двумя тамбурами", 0.2 },
            { "Двойные двери с тамбуром", 0.27 },
            { "Двойные двери без тамбура", 0.34 },
            { "Одинарные двери", 0.22 }
        };
        
        // Тип двери по умолчанию
        public string SelectedDoorType { get; set; } = "Одинарные двери";
        
        public BuildingParameters()
        {
            // Общая ориентация по умолчанию — самая неблагоприятная (β = 0.10)
            LevelOrientations[CommonOrientationKey] = "Север";
        }
    }
}