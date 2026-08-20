using System;
using Autodesk.Revit.DB;

namespace QOVETER.Services
{
    /// <summary>
    /// Единственное место, где ПАРАМЕТР Revit превращается в теплотехническую
    /// величину СИ: сопротивление R [м²·К/Вт] или коэффициент теплопередачи
    /// U [Вт/(м²·К)].
    ///
    /// Зачем отдельный класс. Чтений было четыре, и они разошлись:
    ///   • <c>WallThermalCalculator.TryAnalytical</c> — конвертировал через UnitUtils ✔
    ///   • <c>WallThermalCalculator.TryCustomParameter</c> — брал <c>AsDouble()</c> КАК ЕСТЬ ✘
    ///   • <c>ElementCollectorService</c> (окно) — конвертировал ✔
    ///   • <c>LevelElementScanner.TryReadUValue</c> — брал <c>1.0 / AsDouble()</c> ✘
    /// Внутренняя единица сопротивления у Revit — ft²·h·°F/BTU, это ровно в 5,678 раза
    /// больше числа в СИ. То есть непреобразованное чтение занижало U в 5,7 раза,
    /// причём результат выглядел правдоподобно (R = 4,26 вместо 0,75) и ни в один
    /// диапазон-сторож не упирался. Поймать это можно было только чтением кода.
    ///
    /// Почему мало просто «всегда конвертировать»: в российских проектах параметр
    /// «Сопротивление теплопередаче» заводят и как типизированный (спецификация
    /// «Термическое сопротивление» — тогда конвертировать НАДО), и как безразмерное
    /// число (тогда конвертировать НЕЛЬЗЯ: инженер уже вписал значение в СИ).
    /// Различить их можно только по <c>Definition.GetDataType()</c> — что здесь
    /// и делается, а выбранная трактовка пишется в журнал, чтобы её было видно
    /// на реальной модели, а не выводить по числам.
    /// </summary>
    public static class RevitThermalParameter
    {
        /// <summary>Правдоподобный диапазон сопротивления ограждения, м²·К/Вт.</summary>
        public const double MinResistance = 0.05;
        public const double MaxResistance = 20.0;

        /// <summary>Правдоподобный диапазон коэффициента теплопередачи, Вт/(м²·К).</summary>
        public const double MinUValue = 0.05;
        public const double MaxUValue = 10.0;

        /// <summary>
        /// Сопротивление теплопередаче из параметра, м²·К/Вт.
        /// </summary>
        /// <param name="owner">Кому принадлежит параметр — только для журнала.</param>
        public static bool TryReadResistance(Parameter parameter, string owner, out double rSI)
        {
            return TryRead(parameter, owner, SpecTypeId.ThermalResistance,
                           UnitTypeId.SquareMeterKelvinsPerWatt,
                           MinResistance, MaxResistance, "R", "м²·К/Вт", out rSI);
        }

        /// <summary>
        /// Коэффициент теплопередачи из параметра, Вт/(м²·К).
        /// </summary>
        public static bool TryReadUValue(Parameter parameter, string owner, out double uSI)
        {
            return TryRead(parameter, owner, SpecTypeId.HeatTransferCoefficient,
                           UnitTypeId.WattsPerSquareMeterKelvin,
                           MinUValue, MaxUValue, "U", "Вт/(м²·К)", out uSI);
        }

        private static bool TryRead(Parameter parameter, string owner,
                                    ForgeTypeId expectedSpec, ForgeTypeId siUnit,
                                    double min, double max,
                                    string quantity, string units, out double value)
        {
            value = 0;
            if (parameter == null || !parameter.HasValue) return false;
            if (parameter.StorageType != StorageType.Double) return false;

            double raw = parameter.AsDouble();
            if (raw <= 0) return false;

            string name = parameter.Definition?.Name ?? "без имени";

            ForgeTypeId spec = null;
            try { spec = parameter.Definition?.GetDataType(); }
            catch (Exception ex) { Logger.Debug($"[{owner}] тип параметра «{name}» не прочитан: {ex.Message}"); }

            string interpretation;
            if (spec != null && spec.Equals(expectedSpec))
            {
                value = UnitUtils.ConvertFromInternalUnits(raw, siUnit);
                interpretation = "типизированный параметр, внутренние единицы → СИ";
            }
            else if (spec != null && spec.Equals(SpecTypeId.Number))
            {
                // Безразмерное число: конвертировать нечего и НЕЛЬЗЯ — значение
                // уже в СИ, так его и вводит инженер.
                value = raw;
                interpretation = "безразмерный параметр, значение принято как СИ";
            }
            else
            {
                Logger.Debug(
                    $"[{owner}] параметр «{name}» пропущен: спецификация не подходит " +
                    $"для {quantity} (ожидалась «{expectedSpec?.TypeId}» либо безразмерное число)");
                return false;
            }

            if (value < min || value > max)
            {
                Logger.Warn(
                    $"[{owner}] {quantity} из «{name}» = {value:F3} {units} вне правдоподобного " +
                    $"диапазона {min}…{max} — значение отброшено ({interpretation}). " +
                    "Проверьте единицы измерения параметра в модели.");
                value = 0;
                return false;
            }

            Logger.Debug($"[{owner}] {quantity} из «{name}» = {value:F3} {units} ({interpretation})");
            return true;
        }
    }
}
