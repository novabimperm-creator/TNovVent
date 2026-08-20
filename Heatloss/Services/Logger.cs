using System;
using System.IO;

namespace QOVETER.Services
{
    /// <summary>
    /// Файловый логгер. Пишет в <c>%APPDATA%\QOVETER\logs\log_yyyy-MM-dd.txt</c>
    /// (запасной путь — temp процесса). Точный путь всегда доступен через
    /// <see cref="LogFilePath"/> и печатается на листе «Параметры» Excel-отчёта.
    /// Console.WriteLine в Revit-аддине не виден, поэтому все диагностические
    /// сообщения должны идти через этот класс.
    /// </summary>
    public static class Logger
    {
        private static readonly object _sync = new object();

        /// <summary>
        /// Основной каталог журнала — рядом с пользовательскими настройками плагина,
        /// а НЕ в %TEMP%.
        ///
        /// Почему так: <c>Path.GetTempPath()</c> отдаёт temp ПРОЦЕССА. Если Revit
        /// запущен не в обычном пользовательском контексте (повышение прав, запуск
        /// от другой учётной записи, терминальная сессия), это оказывается
        /// C:\WINDOWS\TEMP — каталог, куда запись проходит, но куда инженер обычно
        /// не может даже заглянуть. 2026-08-06 на этом потерялся журнал целой рабочей
        /// сессии: файл писался успешно, а найти его было негде, и отсутствие файла
        /// сначала приняли за поломку логгера.
        ///
        /// %APPDATA% привязан к учётной записи, а не к способу запуска, и там уже
        /// лежит пользовательский каталог Ψ (<c>thermal_bridges.json</c>).
        /// </summary>
        private static string _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QOVETER", "logs");

        /// <summary>
        /// Префикс имени файла. Тесты пишут в отдельный файл: 2026-08-06 прогон
        /// автотестов (12 тысяч отладочных строк синтетики) лежал в том же логе,
        /// что и рабочие сессии, и был по ошибке принят за лог расчёта на модели.
        /// Диагностический канал не должен смешивать синтетику с реальностью.
        /// </summary>
        private static string _filePrefix = "";

        /// <summary>Перенаправляет лог — для автотестов, чтобы не засорять рабочий файл.</summary>
        public static void UseTestLog()
        {
            lock (_sync)
            {
                _filePrefix = "tests_";
            }
        }

        public enum Level
        {
            Debug,
            Info,
            Warn,
            Error
        }

        public static void Debug(string message)   => Write(Level.Debug, message, null);
        public static void Info(string message)    => Write(Level.Info,  message, null);
        public static void Warn(string message)    => Write(Level.Warn,  message, null);
        public static void Warn(string message, Exception ex)  => Write(Level.Warn,  message, ex);
        public static void Error(string message)   => Write(Level.Error, message, null);
        public static void Error(string message, Exception ex) => Write(Level.Error, message, ex);

        /// <summary>Файл, в который лог пишется ФАКТИЧЕСКИ (каталог мог смениться на запасной).</summary>
        public static string LogFilePath
        {
            get
            {
                lock (_sync)
                {
                    return Path.Combine(_logDir, $"log_{_filePrefix}{DateTime.Now:yyyy-MM-dd}.txt");
                }
            }
        }

        /// <summary>
        /// Буфер строк. Раньше каждая строка писалась отдельным
        /// <c>File.AppendAllText</c> — то есть открытие, запись и закрытие файла.
        /// На модели 76-СУЗДАЛ.23 сбор помещений даёт десятки тысяч Debug-строк
        /// (по строке на каждый сегмент границы), и это заметная часть времени,
        /// на которое подвисает окно.
        /// </summary>
        private static readonly System.Text.StringBuilder _buffer = new System.Text.StringBuilder();

        /// <summary>Порог сброса буфера на диск, строк.</summary>
        private const int FlushThreshold = 200;

        private static int _buffered;

        private static void Write(Level level, string message, Exception ex)
        {
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} [{level,-5}] {message}";
                if (ex != null)
                    line += $" | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";

                lock (_sync)
                {
                    _buffer.AppendLine(line);
                    _buffered++;

                    // Warn и Error сбрасываются немедленно: именно их читают, когда
                    // Revit упал и буфер до диска не доехал.
                    if (_buffered >= FlushThreshold || level >= Level.Warn)
                        FlushUnsafe();
                }
            }
            catch
            {
                // Логгер не должен ронять приложение.
            }
        }

        /// <summary>
        /// Сбрасывает накопленные строки на диск. Вызывать после долгих операций
        /// (сбор помещений, расчёт), чтобы лог был полным ещё до закрытия окна.
        /// </summary>
        public static void Flush()
        {
            lock (_sync)
            {
                FlushUnsafe();
            }
        }

        /// <summary>
        /// Последняя ошибка записи лога, либо null. Показывается в главном окне:
        /// молчащий логгер хуже отсутствующего — 2026-08-06 рабочая сессия на модели
        /// не записала НИ ОДНОЙ строки, и понять это удалось только по отсутствию
        /// файла, потратив на диагностику полчаса.
        /// </summary>
        public static string LastError { get; private set; }

        /// <summary>
        /// Предел накопления при неудачных записях, строк. Если диск недоступен долго,
        /// буфер не должен расти бесконечно — но и молча выбрасываться при первом же
        /// сбое он тоже не должен.
        /// </summary>
        private const int MaxBufferedLines = 20000;

        /// <summary>Сброс без блокировки — вызывающий уже держит <c>_sync</c>.</summary>
        private static void FlushUnsafe()
        {
            if (_buffer.Length == 0) return;

            if (TryWrite(_logDir))
            {
                _buffer.Clear();
                _buffered = 0;
                LastError = null;
                return;
            }

            // Основной путь недоступен — пробуем запасной в профиле пользователя.
            // %TEMP% может быть перенаправлен политикой или недоступен на запись,
            // и терять из-за этого весь лог нельзя.
            if (!string.Equals(_logDir, FallbackDir, StringComparison.OrdinalIgnoreCase) &&
                TryWrite(FallbackDir))
            {
                _logDir = FallbackDir;
                _buffer.Clear();
                _buffered = 0;
                return;
            }

            // Записать не удалось никуда: буфер СОХРАНЯЕМ и пробуем в следующий раз.
            // Прежняя версия чистила его в finally — один неудачный сброс уносил
            // всю накопленную диагностику, и о самом сбое никто не узнавал.
            if (_buffered > MaxBufferedLines)
            {
                _buffer.Clear();
                _buffered = 0;
                LastError += " | буфер переполнен, часть строк потеряна";
            }
        }

        /// <summary>Запасной каталог — temp процесса, если %APPDATA% недоступен.</summary>
        private static string FallbackDir => Path.Combine(Path.GetTempPath(), "QOVETER");

        private static bool TryWrite(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(
                    Path.Combine(directory, $"log_{_filePrefix}{DateTime.Now:yyyy-MM-dd}.txt"),
                    _buffer.ToString());
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"{directory}: {ex.GetType().Name} — {ex.Message}";
                return false;
            }
        }
    }
}
