using Autodesk.Revit.DB;
using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QOVETER.Services
{
    /// <summary>
    /// Сервис получения уровней из активного документа Revit.
    /// Revit 2022 API: все конвертации единиц — через UnitUtils.
    /// </summary>
    public class LevelService
    {
        private readonly Document _document;

        public LevelService(Document document)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
        }

        /// <summary>
        /// Возвращает все уровни документа, отсортированные по отметке.
        /// Отметка конвертируется в метры через UnitUtils (Revit 2022 API).
        /// </summary>
        public List<LevelInfo> GetAllLevels()
        {
            var levels = new List<LevelInfo>();

            try
            {
                var revitLevels = new FilteredElementCollector(_document)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)  // сортировка по исходным единицам (футы) — порядок верен
                    .ToList();

                int floorIndex = 1;
                foreach (var level in revitLevels)
                {
                    // Конвертация отметки из внутренних единиц (футы) в метры
                    double elevationM = UnitUtils.ConvertFromInternalUnits(
                        level.Elevation, UnitTypeId.Meters);

                    levels.Add(new LevelInfo
                    {
                        Id          = level.Id.IntegerValue,
                        Name        = level.Name ?? $"Уровень {floorIndex}",
                        Elevation   = elevationM,
                        FloorNumber = floorIndex   // порядковый номер по сортировке, а не по отметке
                    });

                    floorIndex++;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Ошибка получения уровней", ex);
            }

            return levels;
        }

        public LevelInfo GetLevelById(int levelId)
        {
            return GetAllLevels().FirstOrDefault(l => l.Id == levelId);
        }

        public LevelInfo GetLevelByName(string levelName)
        {
            return GetAllLevels().FirstOrDefault(l =>
                l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
        }
    }
}