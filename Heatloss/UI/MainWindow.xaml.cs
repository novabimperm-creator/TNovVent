using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using QOVETER.Models;
using QOVETER.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

// ✅ РЕШЕНИЕ: Добавлены псевдонимы для устранения неоднозначности
using WpfGrid = System.Windows.Controls.Grid;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace QOVETER.UI
{
    public partial class MainWindow : Window
    {
        private readonly Document _document;
        private readonly UIDocument _uiDocument;
        private List<RoomData> _allRooms = new List<RoomData>();
        private List<LevelInfo> _levels = new List<LevelInfo>();
        private List<CalculationResult> _lastResults = new List<CalculationResult>();

        /// <summary>Температуры неотапливаемых объёмов последнего расчёта — для отчёта.</summary>
        private List<UnheatedRoomTemperature> _lastUnheatedTemperatures = new List<UnheatedRoomTemperature>();
        private List<Polygon> _roomPolygons = new List<Polygon>();

        /// <summary>Выделенное на плане помещение — чтобы корректно снимать выделение.</summary>
        private Polygon _selectedPolygon;
        private RoomData _selectedRoom;
        private List<CityData> _cities = new List<CityData>();
        private BuildingParameters _buildingParams = new BuildingParameters();
        private RoomTypeTemperatures _roomTemperatures = new RoomTypeTemperatures();
        private LevelElementScanner _levelScanner;

        /// <summary>
        /// Имя параметра с номером квартиры, если в проекте оно нестандартное.
        /// Пусто — <see cref="GeometryCollector"/> перебирает типовые имена
        /// и «Комментарии». Ввод в настройках появится вместе с сохранением
        /// настроек между сессиями (этап 6 плана).
        /// </summary>
        private string _apartmentParameterName = "";

        /// <summary>
        /// Откуда взято исполнение узлов фасада в последнем расчёте: путь к файлу
        /// объекта либо пометка про умолчания. Идёт на лист «Параметры» отчёта.
        /// </summary>
        private string _nodeSettingsSource = "";

        /// <summary>Как считался воздухообмен в последнем расчёте — на лист «Параметры».</summary>
        private string _ventilationSource = "";

        public MainWindow(Document document)
        {
            _document = document;
            InitializeComponent();
            _uiDocument = new UIDocument(document);
            _levelScanner = new LevelElementScanner(document);

            // Значения по умолчанию ставятся ДО LoadCities(): город подставляет свою
            // расчётную температуру наружного воздуха, и затирать её нельзя.
            // Раньше UpdateUI() вызывался последним и жёстко писал «-35» поверх −25 °C,
            // подставленных для Москвы, — инженер, не трогавший город, считал здание
            // при ΔT примерно на 40% больше реальной.
            UpdateUI();

            // Сбор помещений при ОТКРЫТИИ окна не запускается — намеренно.
            //
            // Раньше здесь стоял LoadData(), то есть полный обход модели
            // (GeometryCollector.CollectRoomsFromCurrentModel) ещё до того, как окно
            // появилось на экране. На 76-СУЗДАЛ.23 это 20-25 минут, а на большом
            // проекте сетевиков (отзыв 2026-08-26) — около трёх часов, в течение
            // которых Revit выглядит зависшим: инженер нажал кнопку плагина и не
            // видит ни окна, ни причины ждать.
            //
            // Этот проход был не только долгим, но и БЕСПОЛЕЗНЫМ: город на момент
            // открытия окна не выбран, ГСОП = 0, нижняя граница нормируемого R
            // не применяется — и любой инженер, работающий по инструкции
            // «город → собрать → рассчитать», тут же нажимал «Собрать помещения»
            // и запускал тот же обход второй раз. Из двух одинаковых обходов
            // в отчёт шёл только второй.
            //
            // Теперь окно открывается мгновенно, а модель читается ровно один раз —
            // по кнопке «Собрать помещения», уже с выбранным городом.
            ShowNotCollectedYet();
            WarnIfExcelExportUnavailable();
            LoadCities();

            LevelsCombo.SelectionChanged += LevelsCombo_SelectionChanged;
            CitiesCombo.SelectionChanged += CitiesCombo_SelectionChanged;

            // Закрыть окно посреди сбора нельзя: обход модели идёт в потоке Revit
            // и после закрытия окна продолжился бы вслепую. Крестик во время сбора
            // означает «отменить» — и окно закроется, когда сбор остановится.
            Closing += (s, e) =>
            {
                if (!_collecting) return;

                e.Cancel = true;
                _collectCancelled = true;
                StatusText.Text = "Останавливаюсь на ближайшем помещении…";
            };
        }

        /// <summary>
        /// Стартовое состояние окна: модель ещё не читалась.
        ///
        /// <para>Список уровней заполняется из <see cref="LevelService"/> — это один
        /// дешёвый запрос по категории «Уровни», а не обход геометрии. Он нужен,
        /// чтобы фильтр этажей не был пустым до сбора.</para>
        /// </summary>
        private void ShowNotCollectedYet()
        {
            RebuildLevelsFromRooms();   // _allRooms пуст → уровни берутся из модели

            RoomCountText.Text    = "Помещения не собраны";
            StatusText.Text       = "Порядок работы: 1) выберите ГОРОД, " +
                                    "2) нажмите «Собрать помещения», 3) «Рассчитать теплопотери». " +
                                    "Без выбранного города ГСОП = 0 и нормируемое сопротивление " +
                                    "не применяется.";
            StatusText.Foreground = Brushes.DarkOrange;
        }

        /// <summary>
        /// Проверить СРАЗУ, соберётся ли книга Excel.
        ///
        /// <para>2026-08-26 на большом проекте инженер узнал, что библиотеки
        /// экспорта рядом с плагином нет, только нажав «Excel» — после многочасового
        /// сбора и расчёта. Проверка стоит миллисекунды и должна стоять в начале,
        /// а не в конце рабочего дня.</para>
        /// </summary>
        private void WarnIfExcelExportUnavailable()
        {
            string missing = null;
            foreach (string name in new[] { "ClosedXML", "DocumentFormat.OpenXml" })
            {
                try
                {
                    System.Reflection.Assembly.Load(name);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Сборка {name} не загрузилась — выгрузка в Excel будет " +
                                $"заменена на CSV: {ex.Message}");
                    missing = name;
                    break;
                }
            }

            if (missing == null) return;

            StatusText.Text += $"  ⚠ Выгрузка в Excel недоступна: рядом с плагином нет " +
                               $"{missing}.dll. Результаты сохранятся в CSV.";
        }

        private void LoadData()
        {
            BeginCollectUi();
            try
            {
                var collector = new GeometryCollector(_document, _apartmentParameterName)
                {
                    // ГСОП площадки — по выбранному городу и температуре внутри.
                    // Без него нормируемое сопротивление не применяется, и
                    // ограждение с непросчитанным фасадом уходит в отчёт голым
                    // бетоном: на 76-СУЗДАЛ.23 это U = 3,73 против нормы 0,32.
                    DegreeDays = ResolveDegreeDays()
                };

                collector.Progress = OnCollectProgress;
                collector.CancelRequested = () => _collectCancelled;

                // Кэш сканера уровней держит результаты по СТАРЫМ объектам RoomData.
                // Без сброса повторное «Собрать помещения» не перепроверяло аномалии
                // и не уточняло площади окон: скан считался уже выполненным.
                _levelScanner.ClearCache();

                // Работаем напрямую в АР-модели (без связанных файлов)
                _allRooms = collector.CollectRoomsFromCurrentModel();

                RebuildLevelsFromRooms();

                // Нижний и верхний этажи — по факту собранных помещений, а не по
                // умолчанию «оба = 1». Без этого этаж 1 считался одновременно первым
                // и последним (пол в грунт + несуществующая кровля), а настоящий
                // верхний этаж кровельных потерь не получал вовсе.
                // Ручная установка через «Настройки ориентации» имеет приоритет.
                if (_buildingParams.AutoDetectFloorRange(_allRooms))
                {
                    Logger.Info($"Этажность определена автоматически: нижний этаж " +
                                $"{_buildingParams.GroundFloorNumber}, верхний " +
                                $"{_buildingParams.TopFloorNumber}");
                }

                // Балконы, лестницы, лифтовые холлы и тамбуры снимаем с расчёта сразу:
                // это неотапливаемые и общедомовые помещения, они идут отдельным
                // расчётом. На реальном доме их под две сотни — руками это не снять.
                //
                // Коридор в этот список категориями не входит специально: коридор внутри
                // квартиры (прихожая) — обычное отапливаемое помещение квартиры. Но
                // межквартирный/внеквартирный коридор (Apartment пуст) — такое же
                // общедомовое помещение, как лестница и лифтовой холл, и должно сниматься
                // тем же правилом.
                int autoExcluded = 0;
                int undergroundExcluded = 0;
                foreach (var room in _allRooms)
                {
                    bool isCommonCorridor = room.Category == RoomCategory.Corridor &&
                                             string.IsNullOrWhiteSpace(room.Apartment);

                    // Подземные этажи снимаются целиком, по номеру уровня. Проектировщики
                    // теплопотери подвала в этом расчёте не считают (подтверждено
                    // 2026-08-06), а по категориям такие помещения не отсекаются:
                    // кладовые подвала имеют категорию Storage, а не Basement.
                    // Плюс расчёт для них всё равно был бы неверен: заглублённые стены
                    // и пол контактируют с ГРУНТОМ, а движок считает их по температуре
                    // наружного воздуха и без зонального метода (см. PLAN.md, 5.0.6).
                    //
                    // Признак — RoomData.IsUnderground, тот же, по которому
                    // AutoDetectFloorRange пропускает подвал при выборе нижнего этажа.
                    // Порознь эти правила уже успели схлопнуться: подвал снимался
                    // с расчёта, а нижним этажом назначался он же, и пол первого
                    // этажа не считался ни у кого.
                    bool isUnderground = room.IsUnderground;

                    if (ThermalConstants.UnheatedOrCommonCategories.Contains(room.Category) ||
                        isCommonCorridor || isUnderground)
                    {
                        room.IsSelected = false;
                        autoExcluded++;
                        if (isUnderground) undergroundExcluded++;
                    }
                }
                if (undergroundExcluded > 0)
                    Logger.Info(
                        $"Снято с расчёта {undergroundExcluded} помещений подземных этажей — " +
                        "по умолчанию, потому что проектировщик теплопотери подвала считает " +
                        "отдельно (подтверждено 2026-08-06), и сверка идёт без них. " +
                        "ПОСЧИТАТЬ их теперь можно: с 2026-08-19 заглублённые ограждения " +
                        "считаются зональным методом СП 50.13330.2024 Г.7, а не по температуре " +
                        "наружного воздуха. Чтобы включить — поставьте галочки в списке помещений.");
                if (autoExcluded > 0)
                    Logger.Info($"Автоматически исключено из расчёта {autoExcluded} " +
                                "неотапливаемых и общедомовых помещений");

                // Помещения, имя которых не опознал классификатор. Раньше они молча
                // становились жилыми комнатами; теперь считаются нежилыми
                // неопределёнными, и об этом надо сказать прямо — имя в модели
                // может быть просто нестандартным.
                var undetected = _allRooms
                    .Where(r => r.Category == RoomCategory.Other && r.IsSelected)
                    .ToList();
                if (undetected.Count > 0)
                {
                    Logger.Warn($"Категория не определена у {undetected.Count} помещений: " +
                                string.Join(", ", undetected.Select(r => $"{r.Number} «{r.Name}»").Take(20)));
                }

                int withApartment = _allRooms.Count(r => !string.IsNullOrWhiteSpace(r.Apartment));
                StatusText.Text       = $"Загружено {_allRooms.Count} помещений";
                StatusText.Foreground = Brushes.Green;
                RoomCountText.Text    = $"Помещений: {_allRooms.Count}, Этажей: {_levels.Count - 1}";

                // Без номера квартиры воздухообмен считается покомнатно — приближение,
                // а не методика ТЗ. Инженер должен об этом узнать сразу, а не из чисел.
                string excludedNote = autoExcluded > 0
                    ? $", исключено общедомовых и неотапливаемых: {autoExcluded}"
                    : "";
                if (undetected.Count > 0)
                    excludedNote += $", тип не определён у {undetected.Count} " +
                                    "(считаются нежилыми — проверьте имена в модели)";

                // Подставленные габариты окон — не мелочь: площадь остекления входит
                // и в потери через окна, и в вычет из площади стен. Раньше подстановка
                // 1,2 × 1,5 м проходила молча, вообще без следа.
                if (collector.WindowsWithDefaultSize > 0)
                    excludedNote += $", габариты не прочитаны у {collector.WindowsWithDefaultSize} окон " +
                                    "(принято типовое 1,2 × 1,5 м)";

                // Непривязанное окно = двойной счёт остекления. Это стоило +6,4% на
                // 76-СУЗДАЛ.23 и до 2026-08-10 не было видно нигде, кроме нулевой
                // колонки «Окна, м²», на которую никто не смотрел.
                if (collector.WindowsNotAttached > 0)
                    excludedNote += $", ⚠ {collector.WindowsNotAttached} окон не привязаны к стенам " +
                                    "(остекление считается дважды — теплопотери завышены)";

                // Шахты. Здесь не предупреждение, а отчёт о принятом решении: эти
                // ограждения раньше считались уличными, теперь у них своя ΔT, и она
                // зависит от НАСТРОЙКИ — инженер должен узнать о ней сразу.
                if (collector.ShaftFaces > 0)
                {
                    double? tShaft = _roomTemperatures.GetTemperature(RoomCategory.Shaft);
                    excludedNote += $", {collector.ShaftFaces} ограждений выходят в шахты " +
                                    $"({collector.ShaftAreaM2:F0} м², принято " +
                                    (tShaft.HasValue ? $"{tShaft.Value:0.#} °C" : "по улице") + ")";
                }

                if (withApartment == 0)
                {
                    Logger.Info("Номер квартиры не найден ни у одного помещения — " +
                                "расчёт воздухообмена будет покомнатным");
                    StatusText.Text = $"Загружено {_allRooms.Count} помещений{excludedNote}. " +
                                      "Номер квартиры не найден — воздухообмен считается покомнатно";
                    StatusText.Foreground = Brushes.DarkOrange;
                }
                else
                {
                    Logger.Info($"Номер квартиры найден у {withApartment} из {_allRooms.Count} помещений");
                    StatusText.Text = $"Загружено {_allRooms.Count} помещений{excludedNote}. " +
                                      $"Номер квартиры найден у {withApartment}";
                    if (undetected.Count > 0)
                        StatusText.Foreground = Brushes.DarkOrange;
                }

                UpdateRoomsList();
                DrawFloorPlan();
            }
            catch (OperationCanceledException)
            {
                // Прерванный сбор — это НЕ половина модели в отчёте. Собранное
                // выбрасывается целиком: помещение, разобранное до отмены,
                // ничем не отличается на вид от разобранного полностью, и
                // посчитать по нему здание значило бы выдать неверные числа
                // за верные.
                _allRooms = new List<RoomData>();
                RebuildLevelsFromRooms();
                UpdateRoomsList();
                DrawFloorPlan();

                StatusText.Text = "Сбор отменён. Помещения не собраны — нажмите " +
                                  "«Собрать помещения», когда будете готовы ждать.";
                StatusText.Foreground = Brushes.DarkOrange;
                RoomCountText.Text = "Помещения не собраны";
            }
            catch (Exception ex)
            {
                ShowErrorMessage($"Ошибка загрузки данных:\n{ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                EndCollectUi();

                // Сбор помещений — самая «болтливая» операция: досбрасываем лог,
                // чтобы он был полным сразу, не дожидаясь закрытия окна.
                Logger.Flush();

                // Если лог не пишется, инженер должен узнать об этом СРАЗУ, а не когда
                // его попросят прислать файл, которого нет. 2026-08-06 рабочая сессия
                // на модели не записала ни строки, и выяснилось это постфактум.
                if (!string.IsNullOrEmpty(Logger.LastError))
                {
                    StatusText.Text += $"  ⚠ Журнал не пишется ({Logger.LastError})";
                    StatusText.Foreground = Brushes.DarkOrange;
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        //  ХОД СБОРА И ОТМЕНА
        //
        //  Сбор идёт в потоке Revit — иначе нельзя, Revit API работает только
        //  в своём потоке. Значит, окно во время сбора не перерисовывается само,
        //  и «плагин думает» выглядит как зависший Revit: сетевики 2026-08-26
        //  ждали около трёх часов, не имея ни строки о том, что происходит,
        //  и не имея способа прекратить, кроме как снять Revit из диспетчера
        //  задач вместе с несохранённой моделью.
        //
        //  Поэтому окно прокачивается вручную (PumpUi) — не чаще четырёх раз
        //  в секунду, чтобы сама перерисовка не стала статьёй расхода.
        // ─────────────────────────────────────────────────────────

        private bool _collecting;
        private bool _collectCancelled;
        private System.Diagnostics.Stopwatch _collectWatch;
        private DateTime _lastProgressShownAt = DateTime.MinValue;

        /// <summary>Реже — незаметно для глаза; чаще — тратится на перерисовку.</summary>
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

        private void BeginCollectUi()
        {
            _collecting = true;
            _collectCancelled = false;
            _collectWatch = System.Diagnostics.Stopwatch.StartNew();
            _lastProgressShownAt = DateTime.MinValue;

            CollectProgress.Value = 0;
            CollectProgress.IsIndeterminate = true;
            CollectProgress.Visibility = System.Windows.Visibility.Visible;
            CancelCollectButton.Visibility = System.Windows.Visibility.Visible;
            CancelCollectButton.IsEnabled = true;
            CancelCollectButton.Content = "Отменить сбор";

            // Пока идёт сбор, вторая тяжёлая операция запускаться не должна:
            // прокачка окна делает кнопки живыми, а Revit API повторного входа
            // не прощает.
            LoadDataButton.IsEnabled = false;
            CalculateButton.IsEnabled = false;
            ModelAuditButton.IsEnabled = false;

            StatusText.Text = "Читаю модель…";
            StatusText.Foreground = Brushes.DarkOrange;
            PumpUi();
        }

        private void EndCollectUi()
        {
            _collecting = false;
            CollectProgress.Visibility = System.Windows.Visibility.Collapsed;
            CollectProgress.IsIndeterminate = false;
            CancelCollectButton.Visibility = System.Windows.Visibility.Collapsed;

            LoadDataButton.IsEnabled = true;
            CalculateButton.IsEnabled = true;
            ModelAuditButton.IsEnabled = true;

            if (_collectWatch != null)
            {
                _collectWatch.Stop();
                Logger.Info($"Сбор помещений занял {_collectWatch.Elapsed.TotalMinutes:F1} мин");
            }
        }

        /// <summary>
        /// Показать ход сбора. Вызывается из потока Revit между помещениями.
        /// Оценка остатка — по среднему времени на уже разобранные помещения:
        /// инженеру нужно решить, ждать ему или уйти, а для этого хватает
        /// и грубой оценки.
        /// </summary>
        private void OnCollectProgress(int done, int total, string what)
        {
            var now = DateTime.Now;
            if (now - _lastProgressShownAt < ProgressInterval) return;
            _lastProgressShownAt = now;

            if (total > 0)
            {
                CollectProgress.IsIndeterminate = false;
                CollectProgress.Maximum = total;
                CollectProgress.Value = done;

                string eta = "";
                double elapsedSec = _collectWatch?.Elapsed.TotalSeconds ?? 0;
                if (done > 0 && elapsedSec > 2)
                {
                    double leftSec = elapsedSec / done * (total - done);
                    eta = leftSec > 90
                        ? $", осталось ~{leftSec / 60:F0} мин"
                        : $", осталось ~{leftSec:F0} с";
                }

                StatusText.Text = $"Разбираю помещения: {done} из {total}{eta}. Сейчас: {what}";
            }
            else
            {
                CollectProgress.IsIndeterminate = true;
                StatusText.Text = what;
            }

            PumpUi();
        }

        private void CancelCollect_Click(object sender, RoutedEventArgs e)
        {
            if (!_collecting) return;

            _collectCancelled = true;
            CancelCollectButton.IsEnabled = false;
            CancelCollectButton.Content = "Останавливаюсь…";
            StatusText.Text = "Останавливаюсь на ближайшем помещении…";
        }

        /// <summary>
        /// Дать окну перерисоваться и обработать нажатия, не уходя из потока Revit.
        /// </summary>
        private static void PumpUi()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        /// <summary>
        /// Строит список уровней из реальных LevelName помещений.
        /// Гарантирует 100% совпадение имён в ComboBox и RoomData.LevelName,
        /// даже когда помещения приходят из связанной АР-модели.
        /// </summary>
        private void RebuildLevelsFromRooms()
        {
            _levels = new List<LevelInfo>();
            _levels.Add(new LevelInfo { Id = -1, Name = "Все этажи", Elevation = 0, FloorNumber = 0 });

            if (_allRooms.Count > 0)
            {
                // Группируем помещения по LevelName = имена уровней из АР-модели
                var roomLevels = _allRooms
                    .Where(r => !string.IsNullOrEmpty(r.LevelName))
                    .GroupBy(r => r.LevelName)
                    .OrderBy(g => g.First().Elevation)
                    .ToList();

                // FloorNumber берётся У ПОМЕЩЕНИЙ уровня, а не назначается порядковым
                // номером. Иначе в системе две несовместимые нумерации: у RoomData
                // это число из имени уровня («Этаж 15» → 15), а у LevelInfo был
                // порядковый индекс. Диалог «Настройки ориентации» пишет в
                // BuildingParameters именно LevelInfo.FloorNumber, а движок сравнивает
                // его с RoomData.FloorNumber — на модели с подвалом и техэтажами
                // числа расходятся, и вручную выставленный верхний этаж не совпадает
                // ни с одним помещением: кровельные потери молча теряются.
                foreach (var group in roomLevels)
                {
                    var sampleRoom = group.First();
                    _levels.Add(new LevelInfo
                    {
                        Id          = sampleRoom.LevelId,
                        Name        = group.Key,             // Точное имя из АР-модели
                        Elevation   = sampleRoom.Elevation,
                        FloorNumber = sampleRoom.FloorNumber
                    });
                }

                // Номер этажа вытаскивается регуляркой из имени уровня, поэтому
                // «Подвал» и «Кровля» (цифр нет) оба дают 1 и сливаются с первым
                // этажом. Молча это делать нельзя: расчёт пола и кровли зависит
                // именно от совпадения номеров.
                var duplicates = _levels
                    .Where(l => l.Id != -1)
                    .GroupBy(l => l.FloorNumber)
                    .Where(g => g.Count() > 1)
                    .ToList();
                foreach (var duplicate in duplicates)
                {
                    Logger.Warn($"Номер этажа {duplicate.Key} получили несколько уровней: " +
                                string.Join(", ", duplicate.Select(l => l.Name)) +
                                " — номер берётся из имени уровня, проверьте именование");
                }
            }
            else
            {
                // Если помещений нет — загружаем уровни из ОВ-модели как fallback
                var levelService = new LevelService(_document);
                var ovLevels = levelService.GetAllLevels();
                _levels.AddRange(ovLevels);
            }

            LevelsCombo.ItemsSource = _levels;
            if (_levels.Count > 0)
                LevelsCombo.SelectedIndex = 0;
        }

        private void LoadCities()
        {
            try
            {
                var cityService = new CityService();
                _cities = cityService.GetAllCities();
                _cities = _cities.OrderBy(c => c.Name).ToList();
                CitiesCombo.ItemsSource = _cities;
                
                var moscow = _cities.FirstOrDefault(c => c.Name.Contains("Москва"));
                if (moscow != null)
                {
                    CitiesCombo.SelectedItem = moscow;
                    ExternalTempBox.Text = ((int)Math.Round(moscow.Temperature)).ToString();
                    CityInfoText.Text = $"{moscow.Name}: {moscow.Temperature}°C, {moscow.Region}";
                    Logger.Info($"Город по умолчанию: {moscow.Name}, t наруж = {moscow.Temperature} °C");
                }
                else
                {
                    // Город не подставился — в поле осталось значение по умолчанию,
                    // и инженер должен знать, что оно НЕ из климатической таблицы.
                    Logger.Warn("Москва не найдена в списке городов — " +
                                "наружная температура осталась значением по умолчанию");
                    CityInfoText.Text = "Город не выбран — проверьте t наруж вручную";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Ошибка загрузки городов", ex);
            }
        }

        private bool IsAllLevelsSelected()
        {
            if (LevelsCombo.SelectedItem is LevelInfo sel)
                return sel.Id == -1;
            return true;
        }

        private List<RoomData> GetDisplayedRooms()
        {
            if (LevelsCombo.SelectedItem is LevelInfo selectedLevel && selectedLevel.Id != -1)
            {
                return _allRooms.Where(r => r.LevelName == selectedLevel.Name).ToList();
            }
            return _allRooms;
        }

        private void UpdateRoomsList()
        {
            try
            {
                RoomsListBox.Items.Clear();
                
                var displayedRooms = GetDisplayedRooms();
                if (displayedRooms.Count == 0)
                {
                    RoomsListBox.Items.Add(new TextBlock 
                    { 
                        Text = "Помещения не найдены", 
                        Foreground = Brushes.Gray, 
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(5)
                    });
                    return;
                }

                var stackPanel = new StackPanel();
                
                var selectAllCheckbox = new CheckBox
                {
                    Content = "ВЫБРАТЬ ВСЕ",
                    FontWeight = FontWeights.Bold,
                    IsChecked = true,
                    Margin = new Thickness(0, 0, 0, 10),
                    Foreground = Brushes.Black,
                    Cursor = Cursors.Hand
                };
                
                selectAllCheckbox.Checked += (s, e) => SelectAllRooms(true);
                selectAllCheckbox.Unchecked += (s, e) => SelectAllRooms(false);
                
                stackPanel.Children.Add(selectAllCheckbox);

                // Помещения сгруппированы по квартирам: по ТЗ воздухообмен нормируется
                // на квартиру, поэтому и выбирать помещения инженеру удобнее квартирами.
                // Общедомовые (без номера квартиры) — отдельной группой в конце:
                // нормативно они идут в отдельный расчёт на общедомовую систему.
                foreach (var group in GroupRoomsByApartment(displayedRooms))
                {
                    var groupRooms = group.Value;
                    var groupCheckboxes = new List<CheckBox>();

                    var header = new CheckBox
                    {
                        Content = $"{group.Key} — {groupRooms.Count} пом., "
                                + $"{groupRooms.Sum(r => r.Area):F1} м²",
                        FontWeight = FontWeights.SemiBold,
                        IsChecked = groupRooms.All(r => r.IsSelected),
                        Margin = new Thickness(0, 8, 0, 2),
                        Foreground = Brushes.Black,
                        Cursor = Cursors.Hand
                    };
                    stackPanel.Children.Add(header);

                    foreach (var room in groupRooms)
                    {
                        var roomCheckbox = new CheckBox
                        {
                            Content = $"{room.Number} - {room.Name} ({room.Area:F1} м²)",
                            Tag = room.Id,
                            IsChecked = room.IsSelected,
                            Margin = new Thickness(20, 2, 0, 2),
                            Foreground = Brushes.Black,
                            Cursor = Cursors.Hand
                        };

                        roomCheckbox.Checked += (s, e) => RoomCheckbox_Changed(s, e, room, true);
                        roomCheckbox.Unchecked += (s, e) => RoomCheckbox_Changed(s, e, room, false);

                        groupCheckboxes.Add(roomCheckbox);
                        stackPanel.Children.Add(roomCheckbox);
                    }

                    // Заголовок переключает всю квартиру: снять галочку с общедомовых
                    // помещений — самая частая операция инженера.
                    header.Checked   += (s, e) => { foreach (var cb in groupCheckboxes) cb.IsChecked = true; };
                    header.Unchecked += (s, e) => { foreach (var cb in groupCheckboxes) cb.IsChecked = false; };
                }

                var scrollViewer = new ScrollViewer
                {
                    Content = stackPanel,
                    MaxHeight = 300,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                
                RoomsListBox.Items.Add(scrollViewer);
                UpdateSelectedRoomsCount();
            }
            catch (Exception ex)
            {
                Logger.Error("Ошибка обновления списка помещений", ex);
            }
        }

        /// <summary>
        /// Группирует помещения по квартирам для дерева в UI. Числовые номера
        /// сортируются как числа, общедомовые помещения — последней группой.
        /// </summary>
        private static List<KeyValuePair<string, List<RoomData>>> GroupRoomsByApartment(
            List<RoomData> rooms)
        {
            var groups = rooms
                .Where(r => !string.IsNullOrWhiteSpace(r.Apartment))
                .GroupBy(r => r.Apartment.Trim())
                .Select(g => new
                {
                    Key = g.Key,
                    Rooms = g.ToList(),
                    Numeric = int.TryParse(g.Key, out int n) ? n : int.MaxValue
                })
                .OrderBy(g => g.Numeric)
                .ThenBy(g => g.Key, StringComparer.CurrentCulture)
                .Select(g => new KeyValuePair<string, List<RoomData>>($"Квартира {g.Key}", g.Rooms))
                .ToList();

            var common = rooms.Where(r => string.IsNullOrWhiteSpace(r.Apartment)).ToList();
            if (common.Count > 0)
            {
                groups.Add(new KeyValuePair<string, List<RoomData>>(
                    groups.Count > 0 ? "Вне квартир (общедомовые)" : "Помещения", common));
            }

            return groups;
        }

        private void SelectAllRooms(bool select)
        {
            var displayedRooms = GetDisplayedRooms();
            foreach (var room in displayedRooms)
            {
                room.IsSelected = select;
            }
            UpdateRoomsList();
        }

        private void RoomCheckbox_Changed(object sender, RoutedEventArgs e, RoomData room, bool isChecked)
        {
            room.IsSelected = isChecked;
            UpdateSelectedRoomsCount();
            RefreshRoomOnPlan(room);
        }

        private void UpdateSelectedRoomsCount()
        {
            var displayedRooms = GetDisplayedRooms();
            int selectedCount = displayedRooms.Count(r => r.IsSelected);
            SelectedRoomsCountText.Text = $"Выбрано: {selectedCount} из {displayedRooms.Count}";
        }

        private double _planZoom = 1.0;

        private void DrawFloorPlan()
        {
            FloorPlanCanvas.Children.Clear();
            _roomPolygons.Clear();
            _selectedPolygon = null;
            _selectedRoom = null;
            LegendPanel.Visibility = System.Windows.Visibility.Collapsed;
            _planZoom = 1.0;
            FloorPlanCanvas.RenderTransform = null;

            // ─── «Все этажи» → очистить Canvas и показать подсказку ───
            if (IsAllLevelsSelected())
            {
                NoRoomsText.Visibility = System.Windows.Visibility.Collapsed;
                SelectLevelHintPanel.Visibility = System.Windows.Visibility.Visible;
                return;
            }

            SelectLevelHintPanel.Visibility = System.Windows.Visibility.Collapsed;

            var displayedRooms = GetDisplayedRooms();
            if (displayedRooms.Count == 0)
            {
                NoRoomsText.Text = "На этом этаже помещения не найдены";
                NoRoomsText.Visibility = System.Windows.Visibility.Visible;
                return;
            }

            NoRoomsText.Visibility = System.Windows.Visibility.Collapsed;

            try
            {
                // ── 1. Найти габариты всех помещений этажа ──
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;

                foreach (var room in displayedRooms)
                {
                    if (room.BoundaryPoints == null || room.BoundaryPoints.Count == 0) continue;
                    foreach (var pt in room.BoundaryPoints)
                    {
                        minX = Math.Min(minX, pt.X);
                        minY = Math.Min(minY, pt.Y);
                        maxX = Math.Max(maxX, pt.X);
                        maxY = Math.Max(maxY, pt.Y);
                    }
                }

                if (minX == double.MaxValue)
                {
                    DrawSimplifiedFloorPlan();
                    return;
                }

                double modelW = maxX - minX;
                double modelH = maxY - minY;
                if (modelW < 0.001) modelW = 1;
                if (modelH < 0.001) modelH = 1;

                // ── 2. Авто-масштабирование: план занимает 90% Canvas ──
                // Canvas имеет фиксированный размер 2000×2000, используем его
                double canvasW = FloorPlanCanvas.Width > 0 ? FloorPlanCanvas.Width : 1800;
                double canvasH = FloorPlanCanvas.Height > 0 ? FloorPlanCanvas.Height : 1800;
                double fillFactor = 0.90;
                double scale = Math.Min(canvasW * fillFactor / modelW, canvasH * fillFactor / modelH);

                // Отступ — центрируем план
                double offsetX = (canvasW - modelW * scale) / 2.0;
                double offsetY = (canvasH - modelH * scale) / 2.0;

                // ── 3. Рисуем полигоны ──
                foreach (var room in displayedRooms)
                {
                    if (room.BoundaryPoints == null || room.BoundaryPoints.Count < 3) continue;

                    var polygon = new Polygon
                    {
                        Points = new PointCollection(),
                        Cursor = Cursors.Hand,
                        Tag = room.Id,
                        ToolTip = BuildRoomTooltip(room)
                    };
                    ApplyRoomStyle(polygon, room, RoomPlanState.Normal);

                    foreach (var pt in room.BoundaryPoints)
                    {
                        polygon.Points.Add(new System.Windows.Point(
                            offsetX + (pt.X - minX) * scale,
                            offsetY + (maxY - pt.Y) * scale   // Y инвертирован: Revit Y-up → WPF Y-down
                        ));
                    }

                    polygon.MouseEnter          += (s, e) => RoomPolygon_MouseEnter(s, e, room);
                    polygon.MouseLeave          += (s, e) => RoomPolygon_MouseLeave(s, e, room);
                    polygon.MouseLeftButtonDown += (s, e) => RoomPolygon_Click(s, e, room);

                    Canvas.SetZIndex(polygon, 1);
                    FloorPlanCanvas.Children.Add(polygon);
                    _roomPolygons.Add(polygon);

                    AddRoomLabel(polygon, room, scale);
                }

                UpdateLegend(displayedRooms);
            }
            catch (Exception ex)
            {
                ShowErrorMessage($"Ошибка отрисовки плана:\n{ex.Message}");
            }
        }

        /// <summary>Масштабирование плана колесиком мыши (Ctrl+Scroll).</summary>
        private void FloorPlanCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            double delta = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
            _planZoom = Math.Max(0.2, Math.Min(_planZoom * delta, 8.0));

            var transform = FloorPlanCanvas.RenderTransform as ScaleTransform
                            ?? new ScaleTransform(1, 1);
            transform.ScaleX = _planZoom;
            transform.ScaleY = _planZoom;
            FloorPlanCanvas.RenderTransform = transform;
            FloorPlanCanvas.RenderTransformOrigin = new System.Windows.Point(0, 0);
        }

        private void DrawSimplifiedFloorPlan()
        {
            double offsetX = 50;
            double offsetY = 50;
            double roomSize = 80;
            double spacing = 20;

            var roomTypeColors = new Dictionary<string, Brush>
            {
                { "Жилая комната", Brushes.LightBlue },
                { "Спальня", Brushes.LightSkyBlue },
                { "Детская", Brushes.LightPink },
                { "Гостиная", Brushes.LightCyan },
                { "Кухня", Brushes.LightGreen },
                { "Санузел", Brushes.LightCoral },
                { "Ванная", Brushes.LightSeaGreen },
                { "Коридор", Brushes.LightGray },
                { "Прихожая", Brushes.LightSteelBlue },
                { "Кладовая", Brushes.LightGoldenrodYellow },
                { "Гардероб", Brushes.LightYellow },
                { "Балкон/Лоджия", Brushes.LightCyan },
                { "Офис", Brushes.Lavender }
            };

            var displayedRooms = GetDisplayedRooms();

            foreach (var room in displayedRooms)
            {
                var rect = new WpfRectangle
                {
                    Width = roomSize,
                    Height = roomSize,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    Fill = roomTypeColors.ContainsKey(room.Type ?? "") ? 
                           roomTypeColors[room.Type] : Brushes.LightGray,
                    Tag = room.Id,
                    Cursor = Cursors.Hand
                };

                Canvas.SetLeft(rect, offsetX);
                Canvas.SetTop(rect, offsetY);
                FloorPlanCanvas.Children.Add(rect);

                var label = new TextBlock
                {
                    Text = $"{room.Number ?? ""}\n{room.Area:F1}м²",
                    FontSize = 9,
                    Foreground = Brushes.Black,
                    TextAlignment = TextAlignment.Center,
                    Width = roomSize,
                    Tag = room.Id
                };

                Canvas.SetLeft(label, offsetX);
                Canvas.SetTop(label, offsetY + roomSize + 2);
                FloorPlanCanvas.Children.Add(label);

                rect.MouseEnter += (s, e) => RoomPolygon_MouseEnter(s, e, room);
                rect.MouseLeave += (s, e) => RoomPolygon_MouseLeave(s, e, room);
                rect.MouseLeftButtonDown += (s, e) => RoomPolygon_Click(s, e, room);

                offsetX += roomSize + spacing;
                if (offsetX > 700)
                {
                    offsetX = 50;
                    offsetY += roomSize + 40;
                }
            }
        }

        /// <summary>
        /// Синхронизирует вид помещения на плане со снятой или поставленной галочкой
        /// в списке. Без этого план показывал бы состав расчёта на момент отрисовки,
        /// а не текущий — и инженер увидел бы в отчёте не то, что видел на плане.
        /// </summary>
        private void RefreshRoomOnPlan(RoomData room)
        {
            var polygon = _roomPolygons.FirstOrDefault(p => p.Tag is int id && id == room.Id);
            if (polygon == null) return;

            ApplyRoomStyle(polygon, room,
                polygon == _selectedPolygon ? RoomPlanState.Selected : RoomPlanState.Normal);
            polygon.ToolTip = BuildRoomTooltip(room);

            int excluded = GetDisplayedRooms().Count(r => !r.IsSelected);
            ExcludedHint.Text = excluded > 0
                ? $"Штриховкой — исключено из расчёта: {excluded}"
                : "";
            ExcludedHint.Visibility = excluded > 0
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        /// <summary>Состояние помещения на плане — от него зависит заливка и контур.</summary>
        private enum RoomPlanState { Normal, Hover, Selected }

        /// <summary>
        /// Единая точка, где помещению назначается вид. Раньше стиль задавался
        /// в трёх местах разными значениями, и после первого же наведения контуры
        /// «съезжали»: MouseLeave возвращал не то, что было изначально.
        ///
        /// Помещение, снятое с расчёта, гасится: инженеру важно видеть на плане,
        /// что именно уйдёт в отчёт, а что он исключил.
        /// </summary>
        private static void ApplyRoomStyle(Shape shape, RoomData room, RoomPlanState state)
        {
            bool included = room.IsSelected;

            shape.Fill = included ? RoomPalette.Fill(room.Category) : RoomPalette.Excluded;
            shape.Opacity = included ? 1.0 : 0.55;
            shape.StrokeDashArray = included ? null : new DoubleCollection { 3, 2 };

            switch (state)
            {
                case RoomPlanState.Selected:
                    shape.Stroke = RoomPalette.Selected;
                    shape.StrokeThickness = 2.6;
                    break;
                case RoomPlanState.Hover:
                    shape.Stroke = RoomPalette.Hover;
                    shape.StrokeThickness = 2.0;
                    break;
                default:
                    shape.Stroke = included ? RoomPalette.Outline : RoomPalette.ExcludedOutline;
                    shape.StrokeThickness = 1.1;
                    break;
            }
        }

        private static ToolTip BuildRoomTooltip(RoomData room)
        {
            string apartment = string.IsNullOrWhiteSpace(room.Apartment)
                ? "вне квартир"
                : $"кв. {room.Apartment}";

            return new ToolTip
            {
                Content =
                    $"{room.Number} — {room.Name}\n" +
                    $"{room.Type} · {apartment}\n" +
                    $"Площадь: {room.Area:F1} м²\n" +
                    $"Ориентация: {room.Orientation ?? "—"}\n" +
                    $"Наружных стен: {room.NumberOfExternalWalls}" +
                    (room.IsCorner ? " (угловое)" : "") +
                    (room.IsSelected ? "" : "\n⚠ Исключено из расчёта"),
                FontSize = 12
            };
        }

        /// <summary>
        /// Подпись помещения: номер и площадь. Площадь показывается только там,
        /// где помещение достаточно крупное, — иначе подписи налезают друг на друга
        /// и план перестаёт читаться.
        /// </summary>
        private void AddRoomLabel(Polygon polygon, RoomData room, double scale)
        {
            if (polygon.Points.Count == 0) return;

            double centerX = polygon.Points.Average(p => p.X);
            double centerY = polygon.Points.Average(p => p.Y);
            double roomPixelSize = Math.Sqrt(Math.Max(room.Area, 0.1)) * scale;

            // Мелкие помещения не подписываем вовсе: нечитаемая подпись хуже её отсутствия.
            if (roomPixelSize < 26) return;

            double fontSize = Math.Max(9, Math.Min(13, roomPixelSize * 0.16));
            bool showArea = roomPixelSize > 60;

            var label = new StackPanel { IsHitTestVisible = false };
            label.Children.Add(new TextBlock
            {
                Text = room.Number ?? "",
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x3E, 0x50)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            if (showArea)
            {
                label.Children.Add(new TextBlock
                {
                    Text = $"{room.Area:F1} м²",
                    FontSize = fontSize * 0.82,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0x8C, 0x8D)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, centerX - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, centerY - label.DesiredSize.Height / 2);
            Canvas.SetZIndex(label, 2);
            FloorPlanCanvas.Children.Add(label);
        }

        /// <summary>
        /// Легенда лежит НЕ на Canvas, а поверх него: раньше она была его дочерним
        /// элементом и масштабировалась вместе с планом — при увеличении уезжала
        /// за экран. Показываются только те категории, что есть на этаже.
        /// </summary>
        private void UpdateLegend(List<RoomData> rooms)
        {
            LegendItems.Children.Clear();

            var categories = rooms
                .Select(r => r.Category)
                .Distinct()
                .OrderBy(c => RoomCategoryHelper.GetRussianLabel(c), StringComparer.CurrentCulture)
                .ToList();

            foreach (var category in categories)
            {
                var item = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 1.5, 0, 1.5)
                };
                item.Children.Add(new WpfRectangle
                {
                    Width = 11,
                    Height = 11,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = RoomPalette.Fill(category),
                    Stroke = RoomPalette.Outline,
                    StrokeThickness = 0.7,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                item.Children.Add(new TextBlock
                {
                    Text = RoomCategoryHelper.GetRussianLabel(category),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0x49, 0x5E)),
                    VerticalAlignment = VerticalAlignment.Center
                });
                LegendItems.Children.Add(item);
            }

            int excluded = rooms.Count(r => !r.IsSelected);
            ExcludedHint.Text = excluded > 0
                ? $"Штриховкой — исключено из расчёта: {excluded}"
                : "";
            ExcludedHint.Visibility = excluded > 0
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

            LegendPanel.Visibility = categories.Count > 0
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        private void RoomPolygon_MouseEnter(object sender, MouseEventArgs e, RoomData room)
        {
            if (sender is Shape shape && shape != _selectedPolygon)
                ApplyRoomStyle(shape, room, RoomPlanState.Hover);
        }

        private void RoomPolygon_MouseLeave(object sender, MouseEventArgs e, RoomData room)
        {
            if (sender is Shape shape && shape != _selectedPolygon)
                ApplyRoomStyle(shape, room, RoomPlanState.Normal);
        }

        private void RoomPolygon_Click(object sender, MouseButtonEventArgs e, RoomData room)
        {
            // Снимаем прежнее выделение, возвращая помещению его обычный вид.
            if (_selectedPolygon != null && _selectedRoom != null)
                ApplyRoomStyle(_selectedPolygon, _selectedRoom, RoomPlanState.Normal);

            if (sender is Polygon polygon)
            {
                _selectedPolygon = polygon;
                _selectedRoom = room;
                ApplyRoomStyle(polygon, room, RoomPlanState.Selected);
                Canvas.SetZIndex(polygon, 3);

                ScrollRoomIntoView(room.Id);
                UpdateSelectedRoomInfo(room);
            }
        }

        /// <summary>
        /// Прокручивает список к помещению, по которому щёлкнули на плане.
        /// Галочку НЕ трогает.
        ///
        /// Здесь стояло <c>checkBox.IsChecked = true</c> — «выделить в списке».
        /// Но галочка в этом списке означает не выделение, а участие в расчёте:
        /// присвоение поднимало событие <c>Checked</c>, а его обработчик ставил
        /// <c>room.IsSelected = true</c>. То есть щелчок по лестничной клетке,
        /// чтобы просто посмотреть её на плане, молча возвращал её в расчёт —
        /// ровно то помещение, которое инженер только что снял.
        /// </summary>
        private void ScrollRoomIntoView(int roomId)
        {
            if (RoomsListBox.Items.Count == 0) return;

            var scrollViewer = RoomsListBox.Items[0] as ScrollViewer;
            var stackPanel = scrollViewer?.Content as StackPanel;
            if (stackPanel == null) return;

            foreach (var child in stackPanel.Children)
            {
                if (child is CheckBox checkBox && checkBox.Tag is int id && id == roomId)
                {
                    checkBox.BringIntoView();
                    break;
                }
            }
        }

        private void UpdateSelectedRoomInfo(RoomData room)
        {
            StatusText.Text = $"Выбрано: {room.DisplayName}";
            StatusText.Foreground = Brushes.Orange;
        }

        private void LevelsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_document == null || !this.IsLoaded) return;

            if (LevelsCombo.SelectedItem is LevelInfo selectedLevel)
            {
                FloorPlanTitle.Text = selectedLevel.Name;
                
                UpdateRoomsList();
                DrawFloorPlan();

                // Автоматический скан элементов этажа
                if (selectedLevel.Id != -1)
                {
                    try
                    {
                        var levelElementId = new ElementId(selectedLevel.Id);
                        var levelRooms = _allRooms.Where(r => r.LevelId == selectedLevel.Id).ToList();
                        var scanResult = _levelScanner.ScanLevel(levelElementId, levelRooms);

                        string scanInfo = $"Скан: {scanResult.GetSummary()}";
                        StatusText.Text = scanInfo;
                        StatusText.Foreground = scanResult.Anomalies.Count > 0 ? Brushes.Orange : Brushes.Green;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("[LevelScanner] Ошибка вызова из UI", ex);
                    }
                }
                
                if (_lastResults != null && _lastResults.Count > 0)
                {
                    ShowResults(_lastResults);
                }
            }
        }

        private void CitiesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CitiesCombo.SelectedItem is CityData selectedCity)
            {
                ExternalTempBox.Text = ((int)Math.Round(selectedCity.Temperature)).ToString();
                CityInfoText.Text = $"{selectedCity.Name}: {selectedCity.Temperature}°C, {selectedCity.Region}";
                StatusText.Text = $"Выбран город: {selectedCity.Name}";
                StatusText.Foreground = Brushes.Blue;
                _buildingParams.SelectedCity = selectedCity.Name;
            }
        }

        // КНОПКА НАСТРОЙКИ ОРИЕНТАЦИИ
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsWindow = new OrientationWindow();
                settingsWindow.Owner = this;
                settingsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                
                // Передаем текущие параметры
                settingsWindow.SetBuildingParameters(_buildingParams);
                settingsWindow.SetLevels(_levels.Where(l => l.Id != -1).ToList());
                
                if (settingsWindow.ShowDialog() == true)
                {
                    // Получаем обновленные параметры
                    _buildingParams = settingsWindow.GetBuildingParameters();
                    
                    StatusText.Text = "Параметры ориентации обновлены";
                    StatusText.Foreground = Brushes.Green;
                }
            }
            catch (Exception ex)
            {
                ShowErrorMessage($"Ошибка открытия настроек:\n{ex.Message}");
            }
        }

        private void RoomTempSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new RoomTemperaturesWindow(_roomTemperatures);
                win.Owner = this;
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;

                if (win.ShowDialog() == true)
                {
                    _roomTemperatures = win.Result;
                    StatusText.Text = "Расчётные температуры помещений обновлены";
                    StatusText.Foreground = Brushes.Green;
                }
            }
            catch (Exception ex)
            {
                ShowErrorMessage($"Ошибка открытия настроек температур:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Открывает файл исполнения узлов фасада ДЛЯ ЭТОЙ МОДЕЛИ, создав шаблон,
        /// если его ещё нет.
        ///
        /// <para>Отдельная кнопка нужна потому, что это единственные величины
        /// расчёта, которых нет ни в модели, ни в нормативе: положение оконной
        /// рамы относительно утеплителя, нахлёст, зуб, перфорация плиты — чертёж
        /// узла. Пока они не заданы, СП-каталог берёт худшую по потерям таблицу
        /// из равных, и Ψ оконного откоса отличается от заданного всемеро.</para>
        /// </summary>
        private void NodeSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = ThermalNodeSettings.SaveTemplate(
                    _document?.PathName, new CalculationParameters().NodeDetails);

                if (string.IsNullOrEmpty(path))
                {
                    ShowErrorMessage(
                        "Не удалось создать файл узлов ни рядом с моделью, ни в профиле пользователя.\n" +
                        "Проверьте права на запись.");
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });

                StatusText.Text = "Файл узлов фасада открыт: " + System.IO.Path.GetFileName(path);
                StatusText.Foreground = Brushes.Green;
            }
            catch (Exception ex)
            {
                ShowErrorMessage($"Не удалось открыть файл узлов:\n{ex.Message}");
            }
        }

        private void CalculateAll_Click(object sender, RoutedEventArgs e)
        {
            if (_collecting) return;

            try
            {
                if (!TryParseFlexible(InternalTempBox.Text, out double tIn))
                {
                    ShowErrorMessage("Введите корректное значение внутренней температуры");
                    InternalTempBox.Focus();
                    InternalTempBox.SelectAll();
                    return;
                }
                if (!ValidateInternalTemp(tIn)) { InternalTempBox.Focus(); return; }

                if (!TryParseFlexible(ExternalTempBox.Text, out double tOut))
                {
                    ShowErrorMessage("Введите корректное значение наружной температуры");
                    ExternalTempBox.Focus();
                    ExternalTempBox.SelectAll();
                    return;
                }
                if (!ValidateExternalTemp(tOut)) { ExternalTempBox.Focus(); return; }

                // Плотность заселения и запас РАЗБИРАЮТСЯ СТРОГО. Раньше при
                // нечитаемом вводе оба молча подменялись умолчаниями, причём запас —
                // не нулём, а 10%: стёртое или испорченное поле «Запас» тихо
                // добавляло 10% к каждому помещению, и в интерфейсе это никак
                // не отражалось.
                if (!TryParseFlexible(OccupancyDensityBox.Text, out double occupancyDensity) ||
                    occupancyDensity <= 0)
                {
                    ShowErrorMessage("Введите положительное значение плотности заселения, м²/чел");
                    OccupancyDensityBox.Focus();
                    OccupancyDensityBox.SelectAll();
                    return;
                }

                if (!TryParseFlexible(SafetyFactorBox.Text, out double safetyPercent))
                {
                    ShowErrorMessage("Введите коэффициент запаса в процентах (0 — без запаса)");
                    SafetyFactorBox.Focus();
                    SafetyFactorBox.SelectAll();
                    return;
                }
                if (safetyPercent < 0 || safetyPercent > 100)
                {
                    ShowErrorMessage($"Запас {safetyPercent}% вне разумного диапазона 0…100%");
                    SafetyFactorBox.Focus();
                    SafetyFactorBox.SelectAll();
                    return;
                }

                UpdateSelectedRoomsCount();
                
                var displayedRooms = GetDisplayedRooms();
                var selectedRooms = displayedRooms.Where(r => r.IsSelected).ToList();
                if (selectedRooms.Count == 0)
                {
                    ShowErrorMessage("Выберите хотя бы одно помещение для расчета");
                    return;
                }

                var parameters = new CalculationParameters
                {
                    InternalTemperature = tIn,
                    ExternalTemperature = tOut,
                    OccupancyDensity = occupancyDensity,
                    UseSafetyFactor = SafetyFactorCheck.IsChecked ?? true,
                    SafetyFactor = 1.0 + safetyPercent / 100.0,
                    SelectedRooms = selectedRooms,
                    DetailedCalculation = DetailedCalcCheck.IsChecked ?? true,
                    // До 2026-08-26 вычет бытовых был доступен только из кода, то есть
                    // фактически всегда выключен. Инженер сравнивал наш итог с расчётом
                    // проектировщика, у которого формула (1) идёт С вычетом, и разница
                    // в бытовых (на реальном этаже это около 16% итога) выглядела
                    // необъяснимой.
                    SubtractInternalHeatGains = SubtractInternalGainsCheck.IsChecked ?? false,
                    BuildingParams = _buildingParams,
                    RoomTemperatures = _roomTemperatures,
                    // Воздухообмен по ТЗ — на квартиру. Помещения без номера квартиры
                    // движок сам посчитает покомнатно.
                    UseApartmentGrouping = true,
                    ApartmentParameterName = _apartmentParameterName,
                    // Норма воздухообмена квартиры определяется её СОСТАВОМ, а не
                    // тем, что инженер оставил в отчёте: передаём все собранные
                    // помещения, иначе снятая галочка с санузла срезала бы мощность
                    // у жилых комнат той же квартиры.
                    ApartmentComposition = _allRooms,
                    // По той же причине — все помещения, а не выбранные: температура
                    // лоджии считается балансом по ЕЁ остеклению (СП 50.13330 п. 5.2),
                    // а сама лоджия снята с расчёта и в selectedRooms не входит.
                    AllRooms = _allRooms,
                    // Считается только то, что выбрано в списке, а список отфильтрован
                    // выбранным этажом. Подпись итога должна это отражать, иначе сумма
                    // по одному этажу уходит в отчёт как «итого по зданию».
                    ScopeName = IsAllLevelsSelected()
                        ? "ПО ЗДАНИЮ"
                        : $"ПО ЭТАЖУ «{(LevelsCombo.SelectedItem as LevelInfo)?.Name}»"
                };

                // Исполнение узлов фасада — свойство ОБЪЕКТА, а не программы:
                // положение оконной рамы относительно утеплителя, нахлёст, зуб,
                // перфорация плиты из модели не вытаскиваются, а на другом доме
                // будут другими. Поэтому читается файл, привязанный к модели;
                // нет файла — остаются умолчания «в запас» (худшее из равных),
                // и об этом пишет и журнал, и лист «Параметры» отчёта.
                parameters.NodeDetails = ThermalNodeSettings.Load(
                    _document?.PathName, parameters.NodeDetails, out _nodeSettingsSource);
                Logger.Info($"[Узлы] исполнение узлов: {_nodeSettingsSource}");

                // Метод воздухообмена — тоже свойство ОБЪЕКТА: у разных заказчиков
                // и смежников методики расходятся, и разница в числах велика.
                VentilationMethod ventilation;
                double airChangeRate;
                string ventilationNote;
                if (ThermalNodeSettings.TryLoadVentilation(
                        _document?.PathName, out ventilation, out airChangeRate, out ventilationNote))
                {
                    parameters.Ventilation = ventilation;
                    parameters.AirChangeRatePerHour = airChangeRate;
                    _ventilationSource = ventilationNote;
                }
                else
                {
                    _ventilationSource = "на квартиру по ТЗ (умолчание расчёта)";
                }

                // Последняя проверка — самим объектом параметров. До 2026-08-06
                // CalculationParameters.Validate не вызывался в приложении НИ РАЗУ:
                // класс носил валидатор, который срабатывал только в тестах.
                string validationError;
                if (!parameters.Validate(out validationError))
                {
                    ShowErrorMessage($"Параметры расчёта некорректны:\n{validationError}");
                    return;
                }

                if (CitiesCombo.SelectedItem is CityData selectedCity)
                {
                    parameters.SelectedCity = selectedCity.Name;
                    parameters.CityTemperature = selectedCity.Temperature;
                    parameters.Region = selectedCity.Region;
                    _buildingParams.SelectedCity = selectedCity.Name;
                }

                StatusText.Text = "Выполняется расчет...";
                StatusText.Foreground = Brushes.Orange;
                Mouse.OverrideCursor = Cursors.Wait;

                var engine = new CalculationEngine(_document);
                _lastResults = engine.Calculate(selectedRooms, parameters, _buildingParams);

                // Температуры неотапливаемых объёмов — вход расчёта, а не его деталь:
                // отчёт обязан показать, посчитаны они балансом или взяты из таблицы.
                _lastUnheatedTemperatures = engine.UnheatedTemperatures;

                ShowResults(_lastResults);

                StatusText.Text = "Расчет завершен";
                StatusText.Foreground = Brushes.Green;
                ResultsSummary.Text = $"Рассчитано {selectedRooms.Count} помещений";

                if (_lastResults.Count > 0 && _lastResults.Last().IsSummary)
                {
                    ShowSuccessMessage($"Расчет завершен!\nОбщая тепловая нагрузка: {_lastResults.Last().Q_final:F0} Вт\n" +
                                      $"Удельная нагрузка: {_lastResults.Last().SpecificHeatLoss:F1} Вт/м²");
                }
            }
            catch (Exception ex)
            {
                ShowErrorMessage($"Ошибка расчета:\n{ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                Mouse.OverrideCursor = null;
                Logger.Flush();
            }
        }

        private void ShowResults(List<CalculationResult> results)
        {
            if (results == null || results.Count == 0)
            {
                ResultsDataGrid.ItemsSource = null;
                return;
            }

            if (LevelsCombo.SelectedItem is LevelInfo selectedLevel && selectedLevel.Id != -1)
            {
                var filteredResults = results.Where(r => 
                    r.IsSummary || 
                    (r.RoomData != null && r.RoomData.LevelName == selectedLevel.Name)
                ).ToList();
                ResultsDataGrid.ItemsSource = filteredResults;
            }
            else
            {
                ResultsDataGrid.ItemsSource = results;
            }
        }

        private void ResultsDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ResultsDataGrid.SelectedItem is CalculationResult result && !result.IsSummary && result.RoomData != null)
            {
                ShowRoomDetails(result.RoomData, result);
            }
        }

        private void ShowRoomDetails(RoomData room, CalculationResult result)
        {
            try
            {
                var detailsWindow = new RoomDetailsWindow();
                detailsWindow.Owner = this;
                detailsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                detailsWindow.SetData(room, result);
                detailsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                ShowErrorMessage($"Не удалось открыть детали:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Считает, какая часть ограждений посчитана ПО ДАННЫМ МОДЕЛИ, а какая
        /// по оценке, и собирает список конструкций без теплотехники.
        ///
        /// Это главный вопрос доверия к отчёту: плагин читает слои, аналитическое
        /// сопротивление и параметры стены, но при пустых материалах подставляет
        /// типовое значение — и тогда числа не про этот дом. На 76-СУЗДАЛ.23
        /// так шло 99% площади, и увидеть это было негде.
        ///
        /// Берутся только ВЫБРАННЫЕ помещения: отчёт описывает их, а не всё,
        /// что удалось собрать.
        /// </summary>
        private void FillThermalCoverage(ExcelExportParams p)
        {
            try
            {
                var walls = _allRooms
                    .Where(r => r.IsSelected)
                    .SelectMany(r => r.Walls ?? new List<WallInfo>())
                    .Where(w => w.IsExternal)
                    .ToList();

                // Три источника, а не два. «По данным модели» и «по таблице
                // норматива» — разные по весу вещи, и в отчёте они разведены;
                // здесь считаются раздельно, чтобы туда попасть.
                p.WallAreaFromModelM2 = walls.Where(w => w.ThermalFromModel).Sum(w => w.Area);
                p.WallAreaNormativeM2 = walls.Where(w => w.ThermalNormative).Sum(w => w.Area);
                p.WallAreaEstimatedM2 = walls
                    .Where(w => !w.ThermalFromModel && !w.ThermalNormative)
                    .Sum(w => w.Area);

                p.WallTypesNormative   = DescribeTypes(walls.Where(w => w.ThermalNormative));
                p.WallTypesWithoutData = DescribeTypes(
                    walls.Where(w => !w.ThermalFromModel && !w.ThermalNormative));

                Logger.Info(
                    $"[Теплотехника] по данным модели {p.WallAreaFromModelM2:F0} м², " +
                    $"по λ из СП 50 прил. Т {p.WallAreaNormativeM2:F0} м², " +
                    $"по типовому U {p.WallAreaEstimatedM2:F0} м²");

                // Ограждения в шахты: площадь и принятая температура. В отчёт идут
                // обе величины — за ними стоит НАСТРОЙКА, а не норматив, и инженер
                // должен видеть, к чему она приложена.
                p.ShaftAreaM2      = walls.Where(w => w.AdjacentCategory == RoomCategory.Shaft).Sum(w => w.Area);
                p.ShaftTemperature = _roomTemperatures.GetTemperature(RoomCategory.Shaft);
                p.NodeSettingsSource = _nodeSettingsSource;
                p.VentilationSource = _ventilationSource;

                // Ограждения в грунте: площади, посчитанные зональным методом.
                // Берутся по ВСЕМ собранным помещениям, а не только выбранным:
                // подвал по умолчанию снят с расчёта, но инженер должен видеть,
                // что метод к нему применён и его можно вернуть галочкой.
                var withGround = (_allRooms ?? new List<RoomData>()).Where(r => r.IsSelected).ToList();
                p.GroundFloorAreaM2 = withGround
                    .Where(r => r.GroundFloorZones != null)
                    .Sum(r => r.GroundFloorZones.Sum(z => z.AreaM2));
                p.GroundWallAreaM2 = withGround
                    .Where(r => r.GroundWallZones != null)
                    .Sum(r => r.GroundWallZones.Sum(z => z.AreaM2));
                p.UnheatedTemperatures = _lastUnheatedTemperatures;

                // Шаблон для ручного ввода создаётся сам и только при необходимости:
                // о файле, про который не сказали, инженер не узнает никогда.
                // Существующий файл не трогается — в нём уже может быть его работа.
                // В шаблон идёт ВСЁ, что посчитано не по данным модели, включая
                // посчитанное по таблице норматива: таблица даёт материал вообще,
                // а инженер знает марку. Ручной ввод стоит выше каталога именно
                // для этого, и не показать ему список значит запереть эту дверь.
                if (p.WallAreaEstimatedM2 > 0 || p.WallAreaNormativeM2 > 0)
                {
                    var byType = walls
                        .Where(w => !w.ThermalFromModel)
                        .GroupBy(w => string.IsNullOrWhiteSpace(w.TypeName) ? "без имени типа" : w.TypeName)
                        .Select(g => new KeyValuePair<string, double>(g.Key, g.Sum(w => w.Area)))
                        .ToList();

                    WallTypeOverrides.SaveTemplate(byType);
                }

                // Тот же приём для узлов: о файле, про который не сказали,
                // инженер не узнает никогда. Шаблон кладётся РЯДОМ С МОДЕЛЬЮ,
                // потому что узел — свойство объекта, и настройка не должна
                // переезжать на следующий дом вместе с профилем пользователя.
                ThermalNodeSettings.SaveTemplate(
                    _document?.PathName, new CalculationParameters().NodeDetails);
            }
            catch (Exception ex)
            {
                Logger.Debug($"FillThermalCoverage: {ex.Message}");
            }
        }

        /// <summary>
        /// Градусо-сутки отопительного периода площадки, °С·сут/год.
        /// СП 50.13330.2012 формула (5.2): ГСОП = (t_в − t_от)·z_от, где t_от и
        /// z_от — по СП 131.13330 таблица 3.1 для выбранного города.
        ///
        /// <para>Ноль означает «климатических данных нет»: город не выбран либо
        /// в таблице у него нет отопительного периода. Тогда нормируемое
        /// сопротивление не применяется вовсе — подставлять его наугад значит
        /// снова выдумывать число.</para>
        /// </summary>
        private double ResolveDegreeDays()
        {
            try
            {
                var city = CitiesCombo?.SelectedItem as CityData;
                if (city == null || !city.HasHeatingPeriod) return 0;

                double indoor;
                if (!TryParseFlexible(InternalTempBox?.Text, out indoor)) indoor = 20.0;

                double gsop = NormativeResistance.DegreeDays(
                    indoor, city.HeatingTemp8, city.HeatingDays8);

                Logger.Info(
                    $"[Норматив] {city.Name}: t_от = {city.HeatingTemp8:F1} °C, " +
                    $"z_от = {city.HeatingDays8} сут (СП 131.13330 табл. 3.1) → " +
                    $"ГСОП = {gsop:F0}; требуемое R стен по СП 50.13330 табл. 3 = " +
                    $"{NormativeResistance.Required(NormativeResistance.Enclosure.Wall, gsop):F2} м²·°С/Вт");

                return gsop;
            }
            catch (Exception ex)
            {
                Logger.Debug($"ResolveDegreeDays: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Типы конструкций с площадями, самые крупные первыми — для отчёта.</summary>
        private static string DescribeTypes(IEnumerable<WallInfo> walls)
        {
            return string.Join("; ", walls
                .GroupBy(w => string.IsNullOrWhiteSpace(w.TypeName) ? "без имени типа" : w.TypeName)
                .OrderByDescending(g => g.Sum(w => w.Area))
                .Take(12)
                .Select(g => $"«{g.Key}» {g.Sum(w => w.Area):F0} м²"));
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_lastResults == null || _lastResults.Count == 0)
                {
                    ShowErrorMessage("Сначала выполните расчёт теплопотерь.");
                    return;
                }

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Экспорт результатов расчёта",
                    Filter = "Excel-книга (*.xlsx)|*.xlsx|JSON (*.json)|*.json",
                    FileName = $"QOVETER_теплопотери_{DateTime.Now:yyyy-MM-dd_HH-mm}",
                    DefaultExt = ".xlsx"
                };
                if (dialog.ShowDialog(this) != true) return;

                Mouse.OverrideCursor = Cursors.Wait;

                // Параметры отчёта собираются ДО попытки записи: их же берёт
                // запасной CSV, если книга Excel не соберётся.
                var exportParams = new ExcelExportParams
                {
                    City         = (CitiesCombo.SelectedItem as CityData)?.Name ?? "",
                    InternalTemp = TryParseFlexible(InternalTempBox.Text, out var ti) ? ti : 20,
                    ExternalTemp = TryParseFlexible(ExternalTempBox.Text, out var te) ? te : -25
                };

                FillThermalCoverage(exportParams);

                try
                {
                    string ext = System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant();
                    if (ext == ".json")
                        new JsonExportService().Export(_lastResults, dialog.FileName, exportParams);
                    else
                        new ExcelExportService().ExportToExcel(_lastResults, dialog.FileName, exportParams);

                    MessageBox.Show(
                        $"Отчёт сохранён:\n{dialog.FileName}",
                        "Готово",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex) when (IsMissingExportLibrary(ex))
                {
                    // Библиотеки Excel-экспорта нет рядом с DLL плагина. Расчёт при
                    // этом уже сделан и лежит в памяти — терять его из-за отсутствующего
                    // файла нельзя: 2026-08-26 на большом проекте это означало бы
                    // выбросить часы работы Revit. Пишем те же строки в CSV.
                    SaveAsCsvFallback(dialog.FileName, exportParams, ex);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Export_Click", ex);
                ShowErrorMessage($"Ошибка при экспорте:\n{ex.Message}");
            }
        }

        /// <summary>
        /// Это отсутствующая сборка экспорта, а не ошибка данных?
        ///
        /// <para>Отличать обязательно: при ошибке данных запасной CSV даст ту же
        /// ошибку, а при отсутствующей библиотеке — спасёт результат расчёта.
        /// .NET сообщает об этом двумя разными исключениями, и оба приходят
        /// завёрнутыми в <see cref="System.Reflection.TargetInvocationException"/>
        /// или в наш собственный <c>catch</c>, поэтому проверяется вся цепочка.</para>
        /// </summary>
        private static bool IsMissingExportLibrary(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is System.IO.FileNotFoundException ||
                    current is System.IO.FileLoadException ||
                    current is BadImageFormatException ||
                    current is TypeLoadException)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Сохранить результаты в CSV, когда Excel-экспорт недоступен, и объяснить
        /// инженеру, что именно случилось и что с этим делать.
        /// </summary>
        private void SaveAsCsvFallback(string requestedPath, ExcelExportParams exportParams, Exception cause)
        {
            Logger.Error("Excel-экспорт недоступен: не загрузилась сборка ClosedXML " +
                         "или её зависимость. Результаты сохраняются в CSV", cause);

            string csvPath = System.IO.Path.ChangeExtension(requestedPath, ".csv");

            try
            {
                new CsvExportService().Export(_lastResults, csvPath, exportParams);
            }
            catch (Exception csvEx)
            {
                Logger.Error("Не удался и запасной CSV", csvEx);
                ShowErrorMessage(
                    "Excel-экспорт недоступен (нет библиотеки ClosedXML рядом с плагином), " +
                    $"и запасной CSV тоже не записался:\n{csvEx.Message}");
                return;
            }

            string pluginDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);

            MessageBox.Show(
                $"Результаты сохранены в CSV:\n{csvPath}\n\n" +
                "Книга Excel не собралась: рядом с библиотекой плагина нет ClosedXML " +
                "или одной из её зависимостей. Расчёт при этом верный — потеряно только " +
                "оформление отчёта и листы «Параметры» и «По квартирам».\n\n" +
                "Чтобы вернуть выгрузку в Excel, нужно положить рядом с плагином файлы:\n" +
                "ClosedXML.dll, ClosedXML.Parser.dll, DocumentFormat.OpenXml.dll, " +
                "DocumentFormat.OpenXml.Framework.dll, ExcelNumberFormat.dll, " +
                "SixLabors.Fonts.dll, RBush.dll, Microsoft.Bcl.HashCode.dll, " +
                "System.Memory.dll, System.Buffers.dll, System.Numerics.Vectors.dll, " +
                "System.Runtime.CompilerServices.Unsafe.dll.\n\n" +
                $"Папка плагина:\n{pluginDir}",
                "Отчёт сохранён в CSV",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        /// <summary>
        /// Выгружает собранные помещения (ВХОД расчёта) в JSON-фикстуру.
        /// Расчёт для этого не нужен — достаточно «Собрать помещения».
        /// Фикстура кладётся в QOVETER.Tests\Fixtures и гоняется в автотестах без Revit.
        /// </summary>
        private void ExportFixture_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var rooms = GetDisplayedRooms();
                if (rooms == null || rooms.Count == 0)
                {
                    ShowErrorMessage("Сначала соберите помещения.");
                    return;
                }

                // Стены собираются при «Собрать помещения» — отдельного шага нет.
                // Поэтому пустая коллекция стен у ВСЕХ помещений означает не забытую
                // кнопку, а реальный сбой чтения границ: обычно граница помещения не
                // замкнута или образована линиями разделения, а не стенами.
                int withWalls = rooms.Count(r => r.Walls != null && r.Walls.Count > 0);
                if (withWalls == 0)
                {
                    var answer = MessageBox.Show(
                        "Ни у одного помещения не собрано ни одной наружной стены.\n\n" +
                        "Это не пропущенный шаг — стены читаются при «Собрать помещения». " +
                        "Скорее всего, границы помещений образованы линиями разделения " +
                        "либо не замкнуты.\n\n" +
                        "В такой выгрузке не будет:\n" +
                        "  • реального U стен — расчёт возьмёт типовое значение;\n" +
                        "  • состава конструкции — таблицы СП 230 не подберутся;\n" +
                        "  • ориентаций — все помещения окажутся северными;\n" +
                        "  • привязки окон к стенам — площадь окон не вычтется из стен.\n\n" +
                        "Всё равно выгрузить?",
                        "Наружные стены не найдены",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) return;
                }

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Выгрузка фикстуры (входные данные расчёта)",
                    Filter = "Фикстура QOVETER (*.json)|*.json",
                    FileName = $"QOVETER_фикстура_{DateTime.Now:yyyy-MM-dd_HH-mm}",
                    DefaultExt = ".json"
                };
                if (dialog.ShowDialog(this) != true) return;

                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    double tOut = TryParseFlexible(ExternalTempBox.Text, out var te) ? te : -25;
                    string levelName = (LevelsCombo.SelectedItem as LevelInfo)?.Name ?? "все уровни";

                    new RoomFixtureService().Save(rooms, dialog.FileName, tOut,
                        $"Выгрузка из модели: {levelName}, помещений {rooms.Count}");

                    MessageBox.Show(
                        $"Фикстура сохранена ({rooms.Count} помещений):\n{dialog.FileName}",
                        "Готово",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("ExportFixture_Click", ex);
                ShowErrorMessage($"Ошибка при выгрузке фикстуры:\n{ex.Message}");
            }
        }

        private void WriteToRevit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_lastResults == null || _lastResults.Count == 0)
                {
                    ShowErrorMessage("Сначала выполните расчёт теплопотерь.");
                    return;
                }

                var roomResults = _lastResults.Where(r => !r.IsSummary).ToList();
                if (roomResults.Count == 0)
                {
                    ShowErrorMessage("Нет результатов по помещениям для записи.");
                    return;
                }

                string targetParameter = RevitParameterNameBox.Text.Trim();
                if (string.IsNullOrEmpty(targetParameter))
                {
                    ShowErrorMessage("Введите имя параметра для записи в Revit.");
                    return;
                }

                // Подтверждение
                var confirm = MessageBox.Show(
                    $"Будет выполнена запись Q_final в параметр «{targetParameter}»\n" +
                    $"для {roomResults.Count} помещений текущей модели.\n\n" +
                    $"Расчёт ведётся в АР-файле, поэтому значения пишутся в помещения (Room). " +
                    $"Если помещений в документе нет, плагин запишет их в пространства (Space).\n\n" +
                    $"Продолжить?",
                    "Подтверждение записи",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes) return;

                Mouse.OverrideCursor = Cursors.Wait;

                var writer = new RevitParameterWriter(_document);
                writer.TargetParameterName = targetParameter;
                
                var result = writer.WriteHeatLossToSpaces(_lastResults);

                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    ShowErrorMessage($"Ошибка записи:\n{result.ErrorMessage}");
                    return;
                }

                // Отображаем итоговое сообщение через красивый UI
                string summary = result.GetSummaryMessage();

                try 
                {
                    // Попытка использовать красивый TaskDialog из Revit API
                    Autodesk.Revit.UI.TaskDialog dialog = new Autodesk.Revit.UI.TaskDialog("Интеграция с Revit");
                    dialog.MainInstruction = result.WrittenCount > 0 ? "Запись успешно завершена!" : "Обратите внимание";
                    dialog.MainContent = summary;
                    dialog.TitleAutoPrefix = false;
                    dialog.CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Ok;
                    if (result.WrittenCount > 0)
                        dialog.MainIcon = Autodesk.Revit.UI.TaskDialogIcon.TaskDialogIconInformation;
                    else
                        dialog.MainIcon = Autodesk.Revit.UI.TaskDialogIcon.TaskDialogIconWarning;
                        
                    dialog.Show();
                }
                catch
                {
                    // Запасной вариант, если вызвано вне контекста
                    MessageBox.Show(summary,
                        "Запись в модель завершена",
                        MessageBoxButton.OK,
                        result.WrittenCount > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }

                StatusText.Text = $"Успешно записано в модель: {result.WrittenCount} пространств";
                StatusText.Foreground = result.WrittenCount > 0 ? Brushes.Green : Brushes.Orange;
            }
            catch (Exception ex)
            {
                ShowErrorMessage($"Ошибка записи в модель:\n{ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        /// <summary>
        /// Значения полей по умолчанию. Наружную температуру здесь НЕ трогаем:
        /// её ставит <see cref="LoadCities"/> по выбранному городу из
        /// <c>Resources/Cities.json</c>, а <see cref="CitiesCombo_SelectionChanged"/> —
        /// при смене города. Хардкод «-35» в этом методе затирал значение города
        /// (у Москвы −25 °C) и молча завышал ΔT по всему зданию.
        /// </summary>
        private void UpdateUI()
        {
            InternalTempBox.Text = "20";
            OccupancyDensityBox.Text = "20";
            SafetyFactorCheck.IsChecked = true;
            DetailedCalcCheck.IsChecked = true;
            SubtractInternalGainsCheck.IsChecked = false;
        }

        private void ShowErrorMessage(string message)
        {
            MessageBox.Show(message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Ошибка";
            StatusText.Foreground = Brushes.Red;
        }

        /// <summary>
        /// Парсит число, принимая ОБА десятичных разделителя ("," и ".") независимо от культуры.
        /// </summary>
        private static bool TryParseFlexible(string text, out double value)
        {
            if (string.IsNullOrWhiteSpace(text)) { value = 0; return false; }
            string normalized = text.Trim().Replace(',', '.');
            return double.TryParse(
                normalized,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out value);
        }

        // ─── Валидация числовых полей с пределами ───────────────────────

        private bool ValidateInternalTemp(double value)
        {
            if (value >= 5 && value <= 40) return true;
            ShowErrorMessage($"Внутренняя температура {value}°C вне допустимого диапазона 5…40°C");
            return false;
        }

        private bool ValidateExternalTemp(double value)
        {
            if (value >= -60 && value <= 15) return true;
            ShowErrorMessage($"Наружная температура {value}°C вне допустимого диапазона −60…+15°C");
            return false;
        }

        private void ShowSuccessMessage(string message)
        {
            MessageBox.Show(message, "Успешно", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Автоматические действия при загрузке
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // Во время сбора окно живое (его прокачивает PumpUi), поэтому повторное
            // нажатие технически возможно — а войти в Revit API второй раз нельзя.
            if (_collecting) return;

            LoadData();
        }

        /// <summary>
        /// Справочный обзор модели: какие типы стен, окон и дверей в ней есть и с
        /// какими характеристиками. На расчёт НЕ влияет — ни одного поля
        /// <see cref="RoomData"/> здесь не меняется.
        ///
        /// Раньше кнопка называлась «Сканировать стены», а документация требовала
        /// нажимать её между «Собрать помещения» и «Рассчитать». Это была устаревшая
        /// инструкция: стены, их U, ориентации и площадь окон собираются в
        /// <c>GeometryCollector.CollectRoomsFromCurrentModel</c> при сборе помещений.
        /// </summary>
        private void ModelAudit_Click(object sender, RoutedEventArgs e)
        {
            if (_collecting) return;

            try
            {
                StatusText.Text = "Аудит элементов модели...";
                StatusText.Foreground = Brushes.Orange;

                // ══════════════════════════════════════════════════════════════
                // 1. СТЕНЫ
                // ══════════════════════════════════════════════════════════════
                var allWalls = new FilteredElementCollector(_document)
                    .OfClass(typeof(Wall))
                    .WhereElementIsNotElementType()
                    .Cast<Wall>()
                    .ToList();

                // Единая реализация R/U — WallThermalCalculator
                var wallCalculator = new WallThermalCalculator(_document);

                var wallTypeGroups = allWalls
                    .GroupBy(w => w.WallType?.Id?.IntegerValue ?? -1)
                    .Select(g =>
                    {
                        var sample = g.First();
                        var wt = sample.WallType;
                        string funcName = "—";
                        try { funcName = wt?.Function.ToString() ?? "—"; }
                        catch (Exception ex) { Logger.Debug($"WallType.Function: {ex.Message}"); }

                        double widthMm = 0;
                        try { widthMm = UnitUtils.ConvertFromInternalUnits(wt?.Width ?? 0, UnitTypeId.Millimeters); }
                        catch (Exception ex) { Logger.Debug($"WallType.Width: {ex.Message}"); }

                        var thermal = wallCalculator.Calculate(sample);

                        return new
                        {
                            TypeName = wt?.Name ?? "Неизвестно",
                            Function = funcName,
                            WidthMm = Math.Round(widthMm),
                            LayerCount = thermal.LayerCount,
                            RValue = Math.Round(thermal.RValue, 3),
                            UValue = Math.Round(thermal.UValue, 3),
                            Count = g.Count(),
                            Layers = thermal.LayerDescription
                        };
                    })
                    .OrderByDescending(x => x.WidthMm)
                    .ToList();

                // ══════════════════════════════════════════════════════════════
                // 2. ОКНА
                // ══════════════════════════════════════════════════════════════
                var allWindows = new FilteredElementCollector(_document)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Windows)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var windowGroups = allWindows
                    .GroupBy(w => w.Symbol?.Id?.IntegerValue ?? -1)
                    .Select(g =>
                    {
                        var sample = g.First();
                        var sym = sample.Symbol;
                        string familyName = sym?.FamilyName ?? "—";
                        string typeName = sym?.Name ?? "—";

                        double widthMm = 0, heightMm = 0;
                        try
                        {
                            var wp = sample.LookupParameter("Ширина") ?? sample.get_Parameter(BuiltInParameter.WINDOW_WIDTH);
                            var hp = sample.LookupParameter("Высота") ?? sample.get_Parameter(BuiltInParameter.WINDOW_HEIGHT);
                            if (wp != null) widthMm = UnitUtils.ConvertFromInternalUnits(wp.AsDouble(), UnitTypeId.Millimeters);
                            if (hp != null) heightMm = UnitUtils.ConvertFromInternalUnits(hp.AsDouble(), UnitTypeId.Millimeters);
                        }
                        catch (Exception ex) { Logger.Debug($"[Диагностика] Габариты окна не прочитаны: {ex.Message}"); }

                        double areaSqM = (widthMm / 1000.0) * (heightMm / 1000.0);

                        // U-value из аналитических свойств
                        double uVal = 0;
                        try
                        {
                            var uParam = sample.LookupParameter("Heat Transfer Coefficient (U)")
                                ?? sample.LookupParameter("Коэффициент теплопередачи");
                            if (uParam != null && uParam.HasValue) uVal = uParam.AsDouble();
                        }
                        catch (Exception ex) { Logger.Debug($"[Диагностика] U окна не прочитан: {ex.Message}"); }

                        // Кол-во камер из названия
                        string fullName = $"{familyName} {typeName}".ToLowerInvariant();
                        string chambers = "?";
                        if (fullName.Contains("однокамерн") || fullName.Contains("1-камерн") || fullName.Contains("single"))
                            chambers = "1";
                        else if (fullName.Contains("двухкамерн") || fullName.Contains("2-камерн") || fullName.Contains("double") || fullName.Contains("трёхслойн"))
                            chambers = "2";

                        // Ориентация (Host wall direction)
                        string orientation = "—";
                        try
                        {
                            var hostWall = sample.Host as Wall;
                            if (hostWall != null)
                            {
                                var locCurve = hostWall.Location as LocationCurve;
                                if (locCurve != null)
                                {
                                    var dir = ((Autodesk.Revit.DB.Line)locCurve.Curve).Direction;
                                    var normal = new XYZ(-dir.Y, dir.X, 0).Normalize();
                                    double angle = Math.Atan2(normal.Y, normal.X) * 180.0 / Math.PI;
                                    if (angle < 0) angle += 360;
                                    if (angle >= 337.5 || angle < 22.5) orientation = "В";
                                    else if (angle >= 22.5 && angle < 67.5) orientation = "СВ";
                                    else if (angle >= 67.5 && angle < 112.5) orientation = "С";
                                    else if (angle >= 112.5 && angle < 157.5) orientation = "СЗ";
                                    else if (angle >= 157.5 && angle < 202.5) orientation = "З";
                                    else if (angle >= 202.5 && angle < 247.5) orientation = "ЮЗ";
                                    else if (angle >= 247.5 && angle < 292.5) orientation = "Ю";
                                    else if (angle >= 292.5 && angle < 337.5) orientation = "ЮВ";
                                }
                            }
                        }
                        catch (Exception ex) { Logger.Debug($"[Диагностика] Ориентация окна не определена: {ex.Message}"); }

                        return new
                        {
                            Family = familyName,
                            TypeName = typeName,
                            WidthMm = Math.Round(widthMm),
                            HeightMm = Math.Round(heightMm),
                            AreaSqM = Math.Round(areaSqM, 2),
                            Chambers = chambers,
                            UValue = Math.Round(uVal, 3),
                            Orientation = orientation,
                            Count = g.Count()
                        };
                    })
                    .OrderByDescending(x => x.AreaSqM)
                    .ToList();

                // ══════════════════════════════════════════════════════════════
                // 3. ДВЕРИ
                // ══════════════════════════════════════════════════════════════
                var allDoors = new FilteredElementCollector(_document)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .ToList();

                var doorGroups = allDoors
                    .GroupBy(d => d.Symbol?.Id?.IntegerValue ?? -1)
                    .Select(g =>
                    {
                        var sample = g.First();
                        var sym = sample.Symbol;

                        double widthMm = 0, heightMm = 0;
                        try
                        {
                            var wp = sample.LookupParameter("Ширина") ?? sample.get_Parameter(BuiltInParameter.DOOR_WIDTH);
                            var hp = sample.LookupParameter("Высота") ?? sample.get_Parameter(BuiltInParameter.DOOR_HEIGHT);
                            if (wp != null) widthMm = UnitUtils.ConvertFromInternalUnits(wp.AsDouble(), UnitTypeId.Millimeters);
                            if (hp != null) heightMm = UnitUtils.ConvertFromInternalUnits(hp.AsDouble(), UnitTypeId.Millimeters);
                        }
                        catch (Exception ex) { Logger.Debug($"[Диагностика] Габариты двери не прочитаны: {ex.Message}"); }

                        return new
                        {
                            Family = sym?.FamilyName ?? "—",
                            TypeName = sym?.Name ?? "—",
                            WidthMm = Math.Round(widthMm),
                            HeightMm = Math.Round(heightMm),
                            AreaSqM = Math.Round((widthMm / 1000.0) * (heightMm / 1000.0), 2),
                            Count = g.Count()
                        };
                    })
                    .OrderByDescending(x => x.AreaSqM)
                    .ToList();

                // ══════════════════════════════════════════════════════════════
                // 4. ПОМЕЩЕНИЯ
                // ══════════════════════════════════════════════════════════════
                var allRooms = new FilteredElementCollector(_document)
                    .OfClass(typeof(SpatialElement))
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Autodesk.Revit.DB.Architecture.Room>()
                    .Where(r => r.Area > 0)
                    .ToList();

                var roomList = allRooms.Select(r =>
                {
                    double areaSqM = 0, volume = 0, height = 0;
                    try
                    {
                        areaSqM = UnitUtils.ConvertFromInternalUnits(r.Area, UnitTypeId.SquareMeters);
                        volume = UnitUtils.ConvertFromInternalUnits(r.Volume, UnitTypeId.CubicMeters);
                        height = r.UnboundedHeight > 0
                            ? UnitUtils.ConvertFromInternalUnits(r.UnboundedHeight, UnitTypeId.Meters) : 0;
                    }
                    catch (Exception ex) { Logger.Debug($"[Диагностика] Геометрия помещения не прочитана: {ex.Message}"); }

                    string levelName = "—";
                    try { levelName = r.Level?.Name ?? "—"; }
                    catch (Exception ex) { Logger.Debug($"[Диагностика] Уровень помещения не прочитан: {ex.Message}"); }

                    return new
                    {
                        Number = r.Number ?? "—",
                        Name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "—",
                        Level = levelName,
                        AreaSqM = Math.Round(areaSqM, 1),
                        Volume = Math.Round(volume, 1),
                        Height = Math.Round(height, 2)
                    };
                })
                .OrderBy(x => x.Level).ThenBy(x => x.Number)
                .ToList();

                // ══════════════════════════════════════════════════════════════
                // ОКНО С ВКЛАДКАМИ
                // ══════════════════════════════════════════════════════════════
                var scanWindow = new Window
                {
                    Title = $"📊 Аудит элементов модели — {allWalls.Count} стен, {allWindows.Count} окон, {allDoors.Count} дверей, {allRooms.Count} помещений",
                    Width = 1300,
                    Height = 650,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 246, 249))
                };

                var tabControl = new TabControl { Margin = new Thickness(8) };

                // ── Вкладка: Стены ──
                var wallsTab = new TabItem { Header = $"🧱 Стены ({wallTypeGroups.Count} типов)" };
                var wallGrid = CreateScanDataGrid();
                wallGrid.Columns.Add(MakeCol("Тип стены", "TypeName", 2));
                wallGrid.Columns.Add(MakeCol("Функция", "Function", 1));
                wallGrid.Columns.Add(MakeCol("Толщина мм", "WidthMm", 0.6));
                wallGrid.Columns.Add(MakeCol("R (м²·°C/Вт)", "RValue", 0.7));
                wallGrid.Columns.Add(MakeCol("U (Вт/м²·°C)", "UValue", 0.7));
                wallGrid.Columns.Add(MakeCol("Слоёв", "LayerCount", 0.4));
                wallGrid.Columns.Add(MakeCol("Кол-во", "Count", 0.4));
                wallGrid.Columns.Add(MakeCol("Состав слоёв", "Layers", 3));
                wallGrid.ItemsSource = wallTypeGroups;
                wallsTab.Content = wallGrid;
                tabControl.Items.Add(wallsTab);

                // ── Вкладка: Окна ──
                var windowsTab = new TabItem { Header = $"🪟 Окна ({windowGroups.Count} типов)" };
                var winGrid = CreateScanDataGrid();
                winGrid.Columns.Add(MakeCol("Семейство", "Family", 2));
                winGrid.Columns.Add(MakeCol("Тип", "TypeName", 1.5));
                winGrid.Columns.Add(MakeCol("Ширина мм", "WidthMm", 0.7));
                winGrid.Columns.Add(MakeCol("Высота мм", "HeightMm", 0.7));
                winGrid.Columns.Add(MakeCol("S (м²)", "AreaSqM", 0.5));
                winGrid.Columns.Add(MakeCol("Камер", "Chambers", 0.4));
                winGrid.Columns.Add(MakeCol("U (Вт/м²·°C)", "UValue", 0.7));
                winGrid.Columns.Add(MakeCol("Ориентация", "Orientation", 0.6));
                winGrid.Columns.Add(MakeCol("Кол-во", "Count", 0.4));
                winGrid.ItemsSource = windowGroups;
                windowsTab.Content = winGrid;
                tabControl.Items.Add(windowsTab);

                // ── Вкладка: Двери ──
                var doorsTab = new TabItem { Header = $"🚪 Двери ({doorGroups.Count} типов)" };
                var doorGrid = CreateScanDataGrid();
                doorGrid.Columns.Add(MakeCol("Семейство", "Family", 2));
                doorGrid.Columns.Add(MakeCol("Тип", "TypeName", 1.5));
                doorGrid.Columns.Add(MakeCol("Ширина мм", "WidthMm", 0.7));
                doorGrid.Columns.Add(MakeCol("Высота мм", "HeightMm", 0.7));
                doorGrid.Columns.Add(MakeCol("S (м²)", "AreaSqM", 0.5));
                doorGrid.Columns.Add(MakeCol("Кол-во", "Count", 0.4));
                doorGrid.ItemsSource = doorGroups;
                doorsTab.Content = doorGrid;
                tabControl.Items.Add(doorsTab);

                // ── Вкладка: Помещения ──
                var roomsTab = new TabItem { Header = $"🏠 Помещения ({roomList.Count})" };
                var roomGrid = CreateScanDataGrid();
                roomGrid.Columns.Add(MakeCol("Номер", "Number", 0.5));
                roomGrid.Columns.Add(MakeCol("Название", "Name", 2));
                roomGrid.Columns.Add(MakeCol("Этаж", "Level", 1));
                roomGrid.Columns.Add(MakeCol("S (м²)", "AreaSqM", 0.6));
                roomGrid.Columns.Add(MakeCol("V (м³)", "Volume", 0.6));
                roomGrid.Columns.Add(MakeCol("H (м)", "Height", 0.5));
                roomGrid.ItemsSource = roomList;
                roomsTab.Content = roomGrid;
                tabControl.Items.Add(roomsTab);

                scanWindow.Content = tabControl;
                scanWindow.ShowDialog();

                StatusText.Text = $"Аудит: {wallTypeGroups.Count} типов стен, {windowGroups.Count} типов окон, {roomList.Count} помещений";
                StatusText.Foreground = Brushes.Green;
            }
            catch (Exception ex)
            {
                ShowErrorMessage($"Ошибка сканирования:\n{ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>Создаёт стандартный DataGrid для окна сканирования</summary>
        private DataGrid CreateScanDataGrid()
        {
            return new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                HorizontalGridLinesBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 216, 224)),
                VerticalGridLinesBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(209, 216, 224)),
                Background = Brushes.White,
                Foreground = Brushes.Black,
                AlternatingRowBackground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245)),
                FontSize = 12,
                Margin = new Thickness(4)
            };
        }

        /// <summary>Создаёт колонку DataGrid со звёздочным размером</summary>
        private DataGridTextColumn MakeCol(string header, string binding, double starWidth)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new System.Windows.Data.Binding(binding),
                Width = new DataGridLength(starWidth, DataGridLengthUnitType.Star)
            };
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SelectAllRooms(true);
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SelectAllRooms(false);
        }
    }
}
