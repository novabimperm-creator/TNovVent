using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;

namespace QOVETER.Services
{
    /// <summary>
    /// Список городов с расчётной температурой наиболее холодной пятидневки.
    /// Источник данных — embedded resource Resources/Cities.json. При сбое чтения
    /// ресурса возвращается минимальный fallback (Москва), плагин остаётся работоспособным.
    ///
    /// ⚠ ЗНАЧЕНИЯ НЕ СВЕРЕНЫ С ТЕКСТОМ НОРМАТИВА, и проблем тут ДВЕ.
    ///
    /// 1. Ни у одного из 43 городов нет ссылки на строку таблицы — при том что список
    ///    подавался как «СП 131.13330.2020». Следов сверки в проекте нет.
    /// 2. Редакция 2020 года БОЛЬШЕ НЕ ДЕЙСТВУЕТ: с 9 сентября 2025 введён
    ///    СП 131.13330.2025 (приказ Минстроя от 08.08.2025 № 470/пр), и в нём
    ///    климатические параметры пересмотрены по ряду наблюдений 1973–2022 годов,
    ///    а число пунктов выросло с 330 до 440. То есть таблицу надо не «проверить»,
    ///    а перебазировать на действующую редакцию целиком.
    ///
    /// Попытка сверить автоматически (2026-08-06) не удалась: обе доступные публикации
    /// СП 131.13330.2025 — сканы без текстового слоя, а брать числа из чужих
    /// онлайн-калькуляторов и подписывать их номером норматива нельзя. Это ровно тот
    /// приём, от которого проект уже отказался с Ψ в <see cref="ThermalBridgeCatalog"/>:
    /// расчёт разваливается в экспертизе именно на неподтверждённой ссылке.
    ///
    /// Поэтому температура города подаётся как ПРЕДВАРИТЕЛЬНАЯ: у каждого значения
    /// есть <see cref="CityData.IsVerified"/> (по умолчанию false), а в отчёте на листе
    /// «Параметры» печатается требование сверить её для площадки строительства.
    /// </summary>
    public class CityService
    {
        /// <summary>Текст предупреждения о непроверенных климатических данных — идёт в отчёт.</summary>
        public const string UnverifiedWarning =
            "Расчётная температура наружного воздуха взята из встроенной таблицы плагина. " +
            "Значения НЕ сверены с текстом норматива и восходят к редакции СП 131.13330.2020, " +
            "которая с 09.09.2025 заменена на СП 131.13330.2025 — там климатические параметры " +
            "пересмотрены. Проверьте t наруж для площадки строительства по действующей " +
            "редакции и при расхождении введите значение вручную.";

        private const string ResourceName = "QOVETER.Resources.Cities.json";
        private static List<CityData> _cached;
        private static readonly object _sync = new object();

        public List<CityData> GetAllCities()
        {
            lock (_sync)
            {
                if (_cached != null) return _cached;
                _cached = LoadFromEmbeddedResource() ?? GetFallback();
                return _cached;
            }
        }

        public CityData GetCityByName(string cityName)
        {
            return GetAllCities().FirstOrDefault(c =>
                c.Name.Equals(cityName, StringComparison.OrdinalIgnoreCase));
        }

        public List<CityData> GetCitiesByRegion(string region)
        {
            return GetAllCities()
                .Where(c => c.Region.Equals(region, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name)
                .ToList();
        }

        private static List<CityData> LoadFromEmbeddedResource()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        Logger.Warn($"CityService: ресурс {ResourceName} не найден, использую fallback");
                        return null;
                    }
                    var serializer = new DataContractJsonSerializer(typeof(List<CityData>));
                    var data = serializer.ReadObject(stream) as List<CityData>;

                    int verified = data?.Count(c => c.IsVerified) ?? 0;
                    Logger.Info($"CityService: загружено {data?.Count ?? 0} городов из ресурса, " +
                                $"сверено с нормативом {verified}");
                    if (verified < (data?.Count ?? 0))
                        Logger.Warn("CityService: часть климатических значений не сверена — " +
                                    "в отчёт идёт предупреждение о проверке t наруж");

                    return data;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("CityService: ошибка чтения Cities.json", ex);
                return null;
            }
        }

        private static List<CityData> GetFallback()
        {
            return new List<CityData>
            {
                new CityData { Name = "Москва", Region = "Центральный", Temperature = -25.0,
                    WindSpeed = 5.0, ClimateZone = "II", Humidity = 83 },
                new CityData { Name = "Другой город", Region = "", Temperature = -25.0,
                    WindSpeed = 5.0, ClimateZone = "II", Humidity = 80 }
            };
        }
    }
}
