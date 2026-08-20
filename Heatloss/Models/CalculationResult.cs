using System.Collections.Generic;

namespace QOVETER.Models
{
    public class CalculationResult
    {
        // Основные свойства
        public string RoomName { get; set; }

        /// <summary>Номер квартиры; пусто — общедомовое помещение или признак не найден.</summary>
        public string Apartment { get; set; } = "";

        public double Q_ogr { get; set; }          // Трансмиссионные теплопотери
        public double Q_vent { get; set; }         // Вентиляционные теплопотери
        public double Q_inf { get; set; }          // Инфильтрация
        public double Q_vn { get; set; }           // Внутренние тепловыделения
        public double Q_total { get; set; }        // Итого без запаса
        public double Q_final { get; set; }        // Итоговые теплопотери с запасом
        
        // Детализация трансмиссионных потерь
        public double Q_walls { get; set; }
        public double Q_windows { get; set; }
        public double Q_doors { get; set; }
        public double Q_floor { get; set; } = 0;   // Пол первого этажа
        public double Q_roof { get; set; } = 0;    // Покрытие последнего этажа
        
        // Сопротивление теплопередаче наружных стен.
        // Инженеру нужны обе величины: условное — то, что даёт слоёный пирог,
        // приведённое — с учётом мостиков. Их отношение r показывает, сколько
        // теряется на неоднородностях.
        /// <summary>R условное наружных стен, м²·К/Вт.</summary>
        public double R_conditional { get; set; }
        /// <summary>R приведённое наружных стен, м²·К/Вт.</summary>
        public double R_reduced { get; set; }
        /// <summary>Коэффициент теплотехнической однородности r = R_усл / R_пр.</summary>
        public double Homogeneity { get; set; } = 1.0;
        /// <summary>R_пр посчитан по несверенным Ψ — в отчёте помечается как предварительный.</summary>
        public bool IsReducedProvisional { get; set; }
        /// <summary>Узлов учтено по таблицам СП 230.</summary>
        public int BridgeNodesCounted { get; set; }
        /// <summary>
        /// Узлов распознано геометрией, но НЕ учтено: таблицы СП 230 для этой
        /// конструкции нет. Теплопотери на них занижены, и отчёт обязан это сказать.
        /// </summary>
        public int BridgeNodesSkipped { get; set; }
        /// <summary>Тип конструкции наружной стены, по которому подбирались таблицы СП 230.</summary>
        public string WallConstructionName { get; set; }
        /// <summary>
        /// Конструкция ПРИНЯТА допущением, а не прочитана из слоёв модели: либо
        /// преобладающая по объекту (в уличном ограждении остался отделочный слой),
        /// либо взятая с ограждения к лоджии или шахте (уличных стен нет вовсе).
        /// Ψ остаются нормативными, но описывают соседнюю стену, а не эту.
        /// </summary>
        public bool IsWallConstructionAssumed { get; set; }
        /// <summary>
        /// У помещения есть наружные ограждения. Ложь — узлов нет ПО ПОСТРОЕНИЮ,
        /// и отчёт не должен числить это недолётом.
        /// </summary>
        public bool HasEnclosures { get; set; }
        /// <summary>
        /// Добавка к U от тарельчатых анкеров, Вт/(м²·К) — СП 230 таблица Г.4.
        /// Ноль означает, что плотность крепежа не задана и анкеры НЕ УЧТЕНЫ:
        /// это названный недолёт, и отчёт обязан о нём сказать.
        /// </summary>
        public double AnchorU { get; set; }

        // Коэффициенты β
        public Dictionary<string, double> BetaCoefficients { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> BetaDetails { get; set; } = new Dictionary<string, double>();
        
        // Геометрические параметры
        public string Orientation { get; set; }
        public RoomData RoomData { get; set; }
        public double Area { get; set; }
        public double WallArea { get; set; }
        public double WindowArea { get; set; }
        public double DoorArea { get; set; }
        public int FloorNumber { get; set; }

        // Дебаг-свойства для проверки площадей в UI
        /// <summary>Площадь НАРУЖНЫХ стен (м²), без вычета окон/дверей</summary>
        public double ExtWallsArea { get; set; }
        /// <summary>Площадь окон в наружных стенах (м²)</summary>
        public double ExtWindowsArea { get; set; }
        
        // Служебные свойства
        public bool IsSummary { get; set; }
        public string ErrorMessage { get; set; }
        
        // Удельные теплопотери
        public double SpecificHeatLoss
        {
            get => Area > 0 ? Q_final / Area : 0;
            set { } // Сеттер для сериализации
        }
        
        // Свойство для отображения в UI
        public string DisplayName => RoomName;
    }
}