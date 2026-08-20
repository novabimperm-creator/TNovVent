using System.Runtime.Serialization;

namespace QOVETER.Models
{
    [DataContract]
    public class CityData
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Region { get; set; }

        /// <summary>Температура наиболее холодной пятидневки, °C, обеспеченностью 0,92.</summary>
        [DataMember] public double Temperature { get; set; }

        [DataMember] public double WindSpeed { get; set; }
        [DataMember] public string ClimateZone { get; set; }
        [DataMember] public double Humidity { get; set; }

        /// <summary>Абсолютная минимальная температура воздуха, °C.</summary>
        [DataMember] public double AbsoluteMin { get; set; }

        /// <summary>
        /// Продолжительность периода со средней суточной температурой воздуха
        /// не более 8 °C, сут. Это ОТОПИТЕЛЬНЫЙ ПЕРИОД жилых и общественных
        /// зданий; вместе с <see cref="HeatingTemp8"/> даёт ГСОП, а без ГСОП
        /// нельзя посчитать нормируемое сопротивление по СП 50.13330 таблица 3.
        ///
        /// <para>До 2026-08-17 этих двух величин в таблице не было вовсе, и это
        /// стояло в плане отдельным блокером.</para>
        /// </summary>
        [DataMember] public int HeatingDays8 { get; set; }

        /// <summary>Средняя температура периода со средней суточной ≤ 8 °C, °C.</summary>
        [DataMember] public double HeatingTemp8 { get; set; }

        /// <summary>
        /// То же для периода ≤ 10 °C — отопительный период лечебных, детских
        /// и учебных зданий (СП 50.13330 п. 5.3 отсылает к назначению здания).
        /// </summary>
        [DataMember] public int HeatingDays10 { get; set; }

        /// <inheritdoc cref="HeatingDays10"/>
        [DataMember] public double HeatingTemp10 { get; set; }

        /// <summary>Данных отопительного периода достаточно для расчёта ГСОП.</summary>
        public bool HasHeatingPeriod => HeatingDays8 > 0 && HeatingTemp8 < 8;

        /// <summary>
        /// Откуда значение: редакция СП и номер таблицы. Пусто — источник не указан.
        /// Поле добавлено 2026-08-06: до него у 43 городов не было НИ ОДНОЙ ссылки
        /// на строку норматива, при том что список подавался как «СП 131.13330.2020».
        /// </summary>
        [DataMember] public string Source { get; set; }

        /// <summary>
        /// Значение сверено с текстом действующей редакции СП 131.13330 и может
        /// идти в отчёт как нормативное. Умолчание — false, и это осознанно:
        /// отсутствующее в JSON поле должно означать «не сверено», а не наоборот.
        /// Та же логика, что у <c>ThermalBridgeEntry.IsVerified</c>.
        /// </summary>
        [DataMember] public bool IsVerified { get; set; }

        public override string ToString()
        {
            return $"{Name} ({Temperature}°C)";
        }
    }
}