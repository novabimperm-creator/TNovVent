using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace QOVETER.Services
{
    /// <summary>
    /// Поиск библиотек Excel-экспорта рядом с плагином.
    ///
    /// <para><b>Что случилось 2026-08-26.</b> На машине сетевика расчёт прошёл,
    /// а кнопка «Excel» ответила «Не удалось загрузить файл или сборку ClosedXML…
    /// либо одну из их зависимостей». Плагин — это НЕ одна DLL: рядом с ним должны
    /// лежать двенадцать файлов, и при ручной передаче или неполном развёртывании
    /// они теряются.</para>
    ///
    /// <para><b>Почему одной проверки мало.</b> Даже когда файлы лежат рядом,
    /// загрузка не гарантирована: <see cref="Assembly.Load(string)"/> по короткому
    /// имени ищет в каталоге ПРИЛОЖЕНИЯ — то есть рядом с Revit.exe, — а не рядом
    /// с вызывающей сборкой. Прежняя проверка доступности экспорта делала именно
    /// такой вызов и потому могла объявить экспорт недоступным при полностью
    /// исправной установке. Здесь и проверка, и загрузка идут ОТ ФАЙЛА.</para>
    ///
    /// <para>Обработчик <see cref="AppDomain.AssemblyResolve"/> закрывает и случай,
    /// когда зависимости разложены подпапкой рядом с плагином, — так их кладут
    /// некоторые установщики.</para>
    /// </summary>
    public static class ExportLibraries
    {
        /// <summary>
        /// Файлы, без которых книга Excel не соберётся. Порядок — как в сообщении
        /// инженеру: сначала то, что ищет он сам, потом транзитивные зависимости.
        /// </summary>
        public static readonly IReadOnlyList<string> RequiredFiles = new[]
        {
            "ClosedXML.dll",
            "ClosedXML.Parser.dll",
            "DocumentFormat.OpenXml.dll",
            "DocumentFormat.OpenXml.Framework.dll",
            "ExcelNumberFormat.dll",
            "SixLabors.Fonts.dll",
            "Microsoft.Bcl.HashCode.dll",
            "System.Memory.dll",
            "System.Buffers.dll",
            "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll"
        };

        private static readonly object Sync = new object();
        private static bool _resolverInstalled;

        /// <summary>Каталог, из которого загружена сборка плагина.</summary>
        public static string PluginDirectory
        {
            get
            {
                try
                {
                    return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[Экспорт] каталог плагина не определён: {ex.Message}");
                    return "";
                }
            }
        }

        /// <summary>
        /// Ставит обработчик разрешения сборок. Вызывать один раз при открытии окна:
        /// повторная подписка добавила бы второй обработчик на тот же домен.
        /// </summary>
        public static void EnsureResolverInstalled()
        {
            lock (Sync)
            {
                if (_resolverInstalled) return;
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                _resolverInstalled = true;
                Logger.Debug($"[Экспорт] поиск сборок рядом с плагином включён: {PluginDirectory}");
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string simpleName = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(simpleName)) return null;

                // Ресурсные сателлиты сюда приходят постоянно и никогда не находятся —
                // отвечать на них поиском по диску значит греть диск впустую.
                if (simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

                foreach (string dir in ProbeDirectories())
                {
                    string candidate = Path.Combine(dir, simpleName + ".dll");
                    if (!File.Exists(candidate)) continue;

                    var assembly = Assembly.LoadFrom(candidate);
                    Logger.Debug($"[Экспорт] сборка {simpleName} загружена из {candidate}");
                    return assembly;
                }
            }
            catch (Exception ex)
            {
                // Обработчик разрешения не имеет права бросать: исключение отсюда
                // уронило бы загрузку любой сборки в процессе Revit.
                Logger.Debug($"[Экспорт] поиск сборки не удался: {ex.Message}");
            }

            return null;
        }

        /// <summary>Где искать: каталог плагина и его подпапки на один уровень.</summary>
        private static IEnumerable<string> ProbeDirectories()
        {
            string root = PluginDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;

            yield return root;

            string[] children;
            try { children = Directory.GetDirectories(root); }
            catch (Exception ex)
            {
                Logger.Debug($"[Экспорт] подпапки плагина не прочитаны: {ex.Message}");
                yield break;
            }

            foreach (string child in children) yield return child;
        }

        /// <summary>
        /// Файлы из <see cref="RequiredFiles"/>, которых рядом с плагином нет.
        /// Пустой список — экспорт в Excel должен работать.
        /// </summary>
        public static List<string> MissingFiles()
        {
            var missing = new List<string>();
            var dirs = ProbeDirectories().ToList();
            if (dirs.Count == 0) return missing;   // каталог не определён — не пугаем зря

            foreach (string file in RequiredFiles)
            {
                if (!dirs.Any(d => File.Exists(Path.Combine(d, file))))
                    missing.Add(file);
            }

            return missing;
        }
    }
}
