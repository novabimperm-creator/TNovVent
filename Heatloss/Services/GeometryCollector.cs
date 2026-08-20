using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QOVETER.Services
{
    /// <summary>
    /// \u0421\u0431\u043e\u0440 \u0433\u0435\u043e\u043c\u0435\u0442\u0440\u0438\u0438 \u043f\u043e\u043c\u0435\u0449\u0435\u043d\u0438\u0439 \u043d\u0430\u043f\u0440\u044f\u043c\u0443\u044e \u0438\u0437 \u0410\u0420-\u043c\u043e\u0434\u0435\u043b\u0438.
    /// \u0420\u0430\u0431\u043e\u0442\u0430\u0435\u0442 \u0441 \u0442\u0435\u043a\u0443\u0449\u0438\u043c \u043e\u0442\u043a\u0440\u044b\u0442\u044b\u043c \u0434\u043e\u043a\u0443\u043c\u0435\u043d\u0442\u043e\u043c. \u0421\u0432\u044f\u0437\u0430\u043d\u043d\u044b\u0435 \u043c\u043e\u0434\u0435\u043b\u0438 \u043d\u0435 \u0438\u0441\u043f\u043e\u043b\u044c\u0437\u0443\u044e\u0442\u0441\u044f.
    /// </summary>
    public class GeometryCollector
    {
        private readonly Document _document;
        private readonly ElementCollectorService _elementCollector;
        private readonly WallThermalCalculator _wallCalculator;
        private readonly OrientationCalculator _orientationCalculator;

        /// <summary>
        /// Имена параметров, из которых берётся номер квартиры, в порядке приоритета.
        /// Первый элемент — заданный пользователем, остальные — типовые для российских
        /// проектов. Если ничего не нашлось, читаются «Комментарии».
        /// </summary>
        private readonly string[] _apartmentParameterNames;

        /// <summary>
        /// Сколько окон получили типовые габариты вместо прочитанных из модели —
        /// чтобы главное окно могло сказать об этом инженеру, а не только журнал.
        /// </summary>
        public int WindowsWithDefaultSize => _elementCollector.WindowsWithDefaultSize;

        /// <summary>
        /// Окна, найденные у помещений, но не привязанные ни к одной наружной стене.
        /// Их площадь НЕ вычтена из площади стен, а потери через них движок считает —
        /// то есть остекление учтено дважды. Ноль здесь обязателен; всё остальное
        /// означает, что часть здания посчитана с завышением.
        /// </summary>
        public int WindowsNotAttached => _windowsNotAttached;

        private int _windowsNotAttached;

        /// <summary>
        /// То же, но в помещениях, которые по умолчанию сняты с расчёта (лоджии,
        /// балконы, лестницы). На итог не влияет — считается отдельно, чтобы
        /// предупреждение не тонуло в безобидных случаях.
        /// </summary>
        private int _windowsNotAttachedOutOfScope;

        /// <summary>
        /// Грани колонн, учтённые как ограждение.
        /// </summary>
        public int ColumnFacesCounted => _columnFacesCounted;

        /// <summary>
        /// Грани колонн, отброшенные пробой: за ними отапливаемый объём (колонна
        /// стоит внутри помещения) либо конструкция, за которой наружу не выйти
        /// (боковая грань колонны, утопленной в кладке).
        ///
        /// Ненулевое значение — норма, а не ошибка: именно эти грани до 2026-08-12
        /// молча считались наружными и давали площадь из воздуха.
        /// </summary>
        public int ColumnFacesInside => _columnFacesInside;

        /// <inheritdoc cref="ColumnFacesInside"/>
        public int ColumnFacesBuried => _columnFacesBuried;

        private int _columnFacesCounted;
        private int _columnFacesInside;
        private int _columnFacesBuried;

        /// <summary>
        /// Сегменты границы, за которыми не улица, а шахта: замкнутая пустота
        /// внутри отапливаемого контура. До 2026-08-13 они считались наружными
        /// с полной ΔT — на 76-СУЗДАЛ.23 это 25 помещений и 5,9% Q огр здания.
        /// </summary>
        public int ShaftFaces => _shaftFacesDetected;

        /// <summary>Их площадь (нетто), м² — чтобы масштаб правки был виден в числах.</summary>
        public double ShaftAreaM2 => _shaftAreaM2;

        /// <summary>
        /// Пустота за стеной нашлась, но шахтой не признана: слишком узкая (шов,
        /// прослойка фасада) либо слишком широкая (разрыв между секциями).
        /// Такие сегменты остаются наружными — ноль здесь не обязателен, но резкий
        /// рост означает, что пороги <see cref="EnclosedVoid"/> не про эту модель.
        /// </summary>
        public int VoidsRejected => _voidsRejected;

        private int    _shaftFacesDetected;
        private double _shaftAreaM2;
        private int    _voidsRejected;

        /// <summary>
        /// Сегменты, перед несущей стеной которых нашлась фасадная система.
        ///
        /// <para>Ключевая величина для доверия к U ограждений. Кладка без
        /// утеплителя — это R ≈ 0,46, кладка с утеплителем — R ≈ 3,37, разница
        /// в семь раз. Пока λ материалов в модели не заполнены, вопрос «нашли ли
        /// мы фасад» и есть вопрос «правильное ли у стены U».</para>
        ///
        /// <para>На прогоне 2026-08-13 фасад находился у 584 сегментов из 2 060,
        /// и НИ ОДИН не был посчитан: у слоёв не было λ. Теперь λ берётся
        /// из таблицы СП, поэтому счётчики стали значимыми.</para>
        /// </summary>
        /// <summary>
        /// Контур ЗДАНИЯ по уровням: отрезки границы, выходящие на наружный воздух.
        /// От него зональный метод отсчитывает полосы по 2 м (СП 50.13330.2024 Г.7).
        /// Ключ — Id уровня: у подвала обвод свой и обычно шире надземного.
        /// </summary>
        private readonly Dictionary<int, List<Segment2D>> _contourByLevel =
            new Dictionary<int, List<Segment2D>>();

        /// <summary>Сегментов, у которых утеплитель достроен по нормативу (в модели его нет).</summary>
        private int _normativeInsulationCount;

        private int _facadeStackFound;
        private int _facadeStackEmpty;
        private int _facadeStackSkippedShaft;

        /// <summary>
        /// Площадь ограждений по типу конструкции и источнику U. Печатается сводкой
        /// после сбора: до неё вопрос «сколько площади посчитано по чему» решался
        /// подсчётом строк в журнале на десятки тысяч записей.
        /// </summary>
        private readonly Dictionary<string, double> _areaByTypeAndSource =
            new Dictionary<string, double>();

        /// <summary>
        /// Промахи пробы фасада по вердикту сегмента и причине — сводится сам.
        ///
        /// <para>До 2026-08-17 эта таблица собиралась разбором сотен протоколов
        /// в журнале после каждого прогона, и из-за этого правки шли по одной
        /// гипотезе за запуск Revit. Диагноз обязан приходить вместе с прогоном,
        /// а не добываться из него.</para>
        /// </summary>
        private readonly Dictionary<string, int> _probeMissByCause =
            new Dictionary<string, int>();

        /// <summary>
        /// Градусо-сутки отопительного периода площадки, °С·сут/год. Ноль —
        /// климатических данных нет, и нормируемое сопротивление не применяется.
        ///
        /// <para>Задаётся главным окном по выбранному городу и температуре
        /// внутреннего воздуха. Нужно для того, чтобы ограждение, выходящее
        /// на наружный воздух, не оказалось хуже требований СП 50.13330
        /// таблица 3: на 76-СУЗДАЛ.23 фасадная система местами не смоделирована
        /// вовсе, и монолит 200 мм считался голым бетоном с U = 3,73.</para>
        /// </summary>
        public double DegreeDays { get; set; }

        /// <summary>Ограждения, поднятые до нормируемого R, и их площадь — для отчёта.</summary>
        public int NormativeFloorApplied => _normativeFloorCount;
        public double NormativeFloorAreaM2 => _normativeFloorAreaM2;

        private int    _normativeFloorCount;
        private double _normativeFloorAreaM2;

        /// <summary>
        /// Конструкции наружных стен объекта и площадь, которую каждая описывает.
        /// Ключ — <see cref="WallConstructionProfile.Signature"/>: тип плюс числовые
        /// оси таблиц СП, потому что две стены одного типа с разной λ основания
        /// попадают в разные строки приложения Г.
        /// </summary>
        private readonly Dictionary<string, Tuple<WallConstructionProfile, double>>
            _streetConstructionArea = new Dictionary<string, Tuple<WallConstructionProfile, double>>();

        /// <summary>
        /// Помещения, у которых ограждение на улицу есть, а конструкция по нему
        /// не разобралась. Ждут преобладающей по объекту — она известна только
        /// после того, как собраны ВСЕ помещения.
        /// </summary>
        private readonly List<int> _roomsAwaitingDominantConstruction = new List<int>();

        private int _constructionFromNonStreet;
        private int _constructionFromDominant;
        private int _constructionUnresolved;

        /// <summary>Конструкции, принятые допущением, — для отчёта.</summary>
        public int ConstructionFromNonStreet => _constructionFromNonStreet;
        public int ConstructionFromDominant  => _constructionFromDominant;
        public int ConstructionUnresolved    => _constructionUnresolved;

        public GeometryCollector(Document document, string apartmentParameterName = null)
        {
            _document               = document;
            _elementCollector       = new ElementCollectorService(document);
            _wallCalculator         = new WallThermalCalculator(document);
            _orientationCalculator  = new OrientationCalculator(document);
            _apartmentParameterNames = new[]
            {
                apartmentParameterName,
                // Имена из реальных проектов — проверяются до поиска по образцу.
                "N_Кв.Номер",                     // 76-СУЗДАЛ.23: сквозной номер квартиры
                "Т_Номер продаваемого помещения", // он же в формате секция-этаж-номер
                "Номер квартиры",
                "Квартира",
                "Apartment",
                "Apartment Number"
            };
        }

        // ─────────────────────────────────────────────────────────
        //  \u0421\u0411\u041e\u0420 \u041f\u041e\u041c\u0415\u0429\u0415\u041d\u0418\u0419
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// \u0415\u0434\u0438\u043d\u0441\u0442\u0432\u0435\u043d\u043d\u0430\u044f \u0442\u043e\u0447\u043a\u0430 \u0432\u0445\u043e\u0434\u0430: \u0441\u043e\u0431\u0440\u0430\u0442\u044c \u0432\u0441\u0435 \u043f\u043e\u043c\u0435\u0449\u0435\u043d\u0438\u044f \u0438\u0437 \u0442\u0435\u043a\u0443\u0449\u0435\u0433\u043e \u0434\u043e\u043a\u0443\u043c\u0435\u043d\u0442a.
        /// </summary>
        public List<RoomData> CollectRoomsFromCurrentModel()
        {
            var rooms = new List<RoomData>();
            try
            {
                rooms.AddRange(CollectRoomsFromDocument());
                ProcessRooms(rooms);
                AssignDominantConstruction(rooms);
                DetectRoomsUnderRoof(rooms);
                AssignGroundZones(rooms);
                Logger.Info($"\u0421\u043e\u0431\u0440\u0430\u043d\u043e {rooms.Count} \u043f\u043e\u043c\u0435\u0449\u0435\u043d\u0438\u0439 \u0438\u0437 \u0410\u0420-\u043c\u043e\u0434\u0435\u043b\u0438");

                if (WindowsWithDefaultSize > 0)
                {
                    Logger.Warn(
                        $"\u0413\u0430\u0431\u0430\u0440\u0438\u0442\u044b \u043d\u0435 \u043f\u0440\u043e\u0447\u0438\u0442\u0430\u043d\u044b \u0443 {WindowsWithDefaultSize} \u043e\u043a\u043e\u043d \u2014 \u0438\u043c \u043f\u043e\u0434\u0441\u0442\u0430\u0432\u043b\u0435\u043d\u043e " +
                        "\u0442\u0438\u043f\u043e\u0432\u043e\u0435 \u043e\u043a\u043d\u043e 1,2 \u00d7 1,5 \u043c. \u041f\u043b\u043e\u0449\u0430\u0434\u044c \u043e\u0441\u0442\u0435\u043a\u043b\u0435\u043d\u0438\u044f \u0443 \u044d\u0442\u0438\u0445 \u043f\u043e\u043c\u0435\u0449\u0435\u043d\u0438\u0439 " +
                        "\u0432\u044b\u0434\u0443\u043c\u0430\u043d\u0430: \u043e\u043d\u0430 \u0432\u0445\u043e\u0434\u0438\u0442 \u0438 \u0432 \u043f\u043e\u0442\u0435\u0440\u0438 \u0447\u0435\u0440\u0435\u0437 \u043e\u043a\u043d\u0430, \u0438 \u0432 \u0432\u044b\u0447\u0435\u0442 \u0438\u0437 \u043f\u043b\u043e\u0449\u0430\u0434\u0438 \u0441\u0442\u0435\u043d. " +
                        "\u041f\u0440\u043e\u0432\u0435\u0440\u044c\u0442\u0435, \u0432 \u043a\u0430\u043a\u0438\u0445 \u043f\u0430\u0440\u0430\u043c\u0435\u0442\u0440\u0430\u0445 \u0441\u0435\u043c\u0435\u0439\u0441\u0442\u0432\u0430 \u043b\u0435\u0436\u0430\u0442 \u0440\u0430\u0437\u043c\u0435\u0440\u044b.");
                }

                int windows = rooms.Sum(r => r.Windows?.Count ?? 0);
                double windowArea = rooms.Sum(r => r.WindowArea);
                string outOfScopeNote = _windowsNotAttachedOutOfScope > 0
                    ? $" \u0415\u0449\u0451 {_windowsNotAttachedOutOfScope} \u2014 \u0432 \u043f\u043e\u043c\u0435\u0449\u0435\u043d\u0438\u044f\u0445, \u0441\u043d\u044f\u0442\u044b\u0445 \u0441 \u0440\u0430\u0441\u0447\u0451\u0442\u0430 " +
                      "(\u043b\u043e\u0434\u0436\u0438\u0438, \u0431\u0430\u043b\u043a\u043e\u043d\u044b, \u043b\u0435\u0441\u0442\u043d\u0438\u0446\u044b); \u043d\u0430 \u0438\u0442\u043e\u0433 \u043e\u043d\u0438 \u043d\u0435 \u0432\u043b\u0438\u044f\u044e\u0442."
                    : "";

                if (_windowsNotAttached > 0)
                {
                    Logger.Warn(
                        $"{_windowsNotAttached} \u043e\u043a\u043e\u043d \u0438\u0437 {windows} \u043d\u0435 \u043f\u0440\u0438\u0432\u044f\u0437\u0430\u043d\u044b \u043d\u0438 \u043a \u043e\u0434\u043d\u043e\u0439 " +
                        "\u043d\u0430\u0440\u0443\u0436\u043d\u043e\u0439 \u0441\u0442\u0435\u043d\u0435 \u043f\u043e\u043c\u0435\u0449\u0435\u043d\u0438\u044f. \u0418\u0445 \u043f\u043b\u043e\u0449\u0430\u0434\u044c \u041d\u0415 \u0432\u044b\u0447\u0442\u0435\u043d\u0430 \u0438\u0437 \u043f\u043b\u043e\u0449\u0430\u0434\u0438 \u0441\u0442\u0435\u043d, " +
                        "\u0430 \u043f\u043e\u0442\u0435\u0440\u0438 \u0447\u0435\u0440\u0435\u0437 \u043d\u0438\u0445 \u0441\u0447\u0438\u0442\u0430\u044e\u0442\u0441\u044f \u2014 \u0442\u043e \u0435\u0441\u0442\u044c \u043e\u0441\u0442\u0435\u043a\u043b\u0435\u043d\u0438\u0435 \u0443\u0447\u0442\u0435\u043d\u043e \u0414\u0412\u0410\u0416\u0414\u042b " +
                        "(\u043a\u0430\u043a \u0433\u043b\u0443\u0445\u0430\u044f \u0441\u0442\u0435\u043d\u0430 \u0438 \u043a\u0430\u043a \u043e\u043a\u043d\u043e), \u0438 \u0442\u0435\u043f\u043b\u043e\u043f\u043e\u0442\u0435\u0440\u0438 \u044d\u0442\u0438\u0445 \u043f\u043e\u043c\u0435\u0449\u0435\u043d\u0438\u0439 \u0437\u0430\u0432\u044b\u0448\u0435\u043d\u044b. " +
                        "\u0422\u0438\u043f\u0438\u0447\u043d\u0430\u044f \u043f\u0440\u0438\u0447\u0438\u043d\u0430 \u2014 \u043e\u0441\u0442\u0435\u043a\u043b\u0435\u043d\u0438\u0435 \u0441\u043c\u043e\u0434\u0435\u043b\u0438\u0440\u043e\u0432\u0430\u043d\u043e \u043d\u0430\u0432\u0435\u0441\u043d\u043e\u0439 \u0441\u0442\u0435\u043d\u043e\u0439 " +
                        "(curtain wall), \u0430 \u043d\u0435 \u043e\u043a\u043d\u043e\u043c \u0432 \u0441\u0442\u0435\u043d\u0435." + outOfScopeNote);
                }
                else
                {
                    Logger.Info($"\u041e\u043a\u043d\u0430: {windows} \u0448\u0442., {windowArea:F1} \u043c\u00b2 \u0432\u044b\u0447\u0442\u0435\u043d\u043e \u0438\u0437 \u043f\u043b\u043e\u0449\u0430\u0434\u0438 \u0441\u0442\u0435\u043d." +
                                outOfScopeNote);
                }

                // Шахты: сколько площади ушло из «улицы» в замкнутый объём. Величина
                // проверяемая — до этой правки те же метры считались при полной ΔT.
                if (_shaftFacesDetected > 0)
                {
                    Logger.Info(
                        $"[Шахты] {_shaftFacesDetected} сегментов границы, {_shaftAreaM2:F1} м²: " +
                        "за ними замкнутая пустота внутри здания, а не наружный воздух. " +
                        "Расчётная температура шахты берётся из таблицы температур " +
                        "(по умолчанию 16 °C) и настраивается инженером.");
                }
                if (_voidsRejected > 0)
                {
                    Logger.Info(
                        $"[Шахты] ещё {_voidsRejected} сегментов с пустотой за стеной оставлены " +
                        "наружными: пустота уже 0,35 м либо шире 1,60 м — шов, прослойка фасада " +
                        "или разрыв между секциями. Причина каждого — строкой выше в журнале.");
                }

                LogEnclosureSummary();
            }
            catch (Exception ex)
            {
                Logger.Error("\u041e\u0448\u0438\u0431\u043a\u0430 \u0441\u0431\u043e\u0440\u0430 \u043f\u043e\u043c\u0435\u0449\u0435\u043d\u0438\u0439 \u0438\u0437 \u0442\u0435\u043a\u0443\u0449\u0435\u0439 \u043c\u043e\u0434\u0435\u043b\u0438", ex);
                throw;
            }
            return rooms;
        }

        // ─────────────────────────────────────────────────────────
        //  \u0412\u041d\u0423\u0422\u0420\u0415\u041d\u041d\u0418\u0415 \u041c\u0415\u0422\u041e\u0414\u042b
        // ─────────────────────────────────────────────────────────

        private List<RoomData> CollectRoomsFromDocument()
        {
            var rooms = new List<RoomData>();
            try
            {
                var collector = new FilteredElementCollector(_document)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType();

                var placed = new List<Room>();
                foreach (Room revitRoom in collector)
                {
                    if (revitRoom == null || revitRoom.Area <= 0) continue;
                    placed.Add(revitRoom);
                }

                foreach (Room revitRoom in FilterToSinglePhase(placed))
                {
                    var roomData = RoomData.FromRevitRoom(revitRoom, "\u0410\u0420-\u043c\u043e\u0434\u0435\u043b\u044c");
                    if (roomData == null) continue;

                    DetermineRoomType(revitRoom, roomData);
                    roomData.Apartment      = DetermineApartment(revitRoom);
                    roomData.BoundaryPoints = GetRoomBoundaryPoints(revitRoom, Transform.Identity);
                    roomData.Orientation    = _orientationCalculator.FromRoom(revitRoom);
                    // IsCorner здесь не считается: он выводится из
                    // NumberOfExternalWalls в CalculateRoomAreasFromBoundary —
                    // единственном месте, где наружные стены реально определяются.

                    rooms.Add(roomData);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("\u041e\u0448\u0438\u0431\u043a\u0430 \u043f\u0440\u0438 \u043e\u0431\u0445\u043e\u0434\u0435 \u043f\u043e\u043c\u0435\u0449\u0435\u043d\u0438\u0439", ex);
            }
            return rooms;
        }

        /// <summary>
        /// Оставляет помещения ОДНОЙ фазы.
        ///
        /// Зачем: <c>FilteredElementCollector</c> по <c>OST_Rooms</c> отдаёт помещения
        /// ВСЕХ фаз документа. В модели реконструкции («Существующие» + «Новая
        /// конструкция») один и тот же физический объём описан дважды, разными
        /// элементами Room с разными Id, — и здание посчиталось бы в два раза.
        /// Дедупликации не было: до сих пор все модели проекта были однофазными,
        /// и молчание выглядело как отсутствие проблемы.
        ///
        /// Берётся ПОСЛЕДНЯЯ по порядку фаза документа, в которой есть помещения:
        /// <c>Document.Phases</c> упорядочены хронологически, и для проектной модели
        /// последняя — это проектируемое состояние. Выбор всегда пишется в журнал
        /// с перечислением отброшенного: угадывать за инженера молча нельзя.
        /// </summary>
        private List<Room> FilterToSinglePhase(List<Room> rooms)
        {
            if (rooms.Count == 0) return rooms;

            var byPhase = rooms
                .GroupBy(r => ElementCollectorService.GetRoomPhase(r)?.Id?.IntegerValue ?? -1)
                .ToList();

            if (byPhase.Count <= 1) return rooms;

            // Хронологический порядок фаз документа: индекс в Document.Phases.
            var order = new Dictionary<int, int>();
            try
            {
                var phases = _document.Phases;
                for (int i = 0; i < phases.Size; i++)
                {
                    var phase = phases.get_Item(i);
                    if (phase != null) order[phase.Id.IntegerValue] = i;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"FilterToSinglePhase: порядок фаз не прочитан: {ex.Message}");
            }

            var chosen = byPhase
                .OrderByDescending(g => order.ContainsKey(g.Key) ? order[g.Key] : -1)
                .ThenByDescending(g => g.Count())
                .First();

            string PhaseName(int id) =>
                (_document.GetElement(new ElementId(id)) as Phase)?.Name ?? $"id {id}";

            var dropped = byPhase
                .Where(g => g.Key != chosen.Key)
                .Select(g => $"«{PhaseName(g.Key)}» ({g.Count()} пом.)")
                .ToList();

            Logger.Warn(
                $"В модели помещения в {byPhase.Count} фазах. В расчёт взята " +
                $"«{PhaseName(chosen.Key)}» ({chosen.Count()} пом.), отброшены: " +
                $"{string.Join(", ", dropped)}. Если считать нужно другую фазу — " +
                "здание описано в модели дважды, и суммировать фазы нельзя.");

            return chosen.ToList();
        }

        private void ProcessRooms(List<RoomData> rooms)
        {
            foreach (var room in rooms)
            {
                try
                {
                    Room revitRoom = _document.GetElement(new ElementId(room.Id)) as Room;
                    if (revitRoom == null) continue;

                    // Высота — ДО сбора площадей: от неё зависят и площадь стен,
                    // и объём, и надбавка за высокое помещение.
                    ApplyEffectiveHeight(room, revitRoom);

                    room.Windows = _elementCollector.CollectWindowsForRoom(revitRoom);
                    room.Doors   = _elementCollector.CollectDoorsForRoom(revitRoom);

                    CalculateRoomAreasFromBoundary(room, revitRoom, _document);
                    DetermineRoomProperties(room);
                }
                catch (Exception ex)
                {
                    Logger.Error($"\u041e\u0448\u0438\u0431\u043a\u0430 \u043e\u0431\u0440\u0430\u0431\u043e\u0442\u043a\u0438 \u043f\u043e\u043c\u0435\u0449\u0435\u043d\u0438\u044f {room.Name}", ex);
                }
            }
        }



        /// <summary>
        /// Достраивает конструкцию помещениям, у которых ограждение на улицу есть,
        /// а разбирать в нём нечего.
        ///
        /// <para><b>Что происходит на модели.</b> Границу помещения образует отделочный
        /// слой («Отделка Штук15», 15 мм), а несущую стену за ним проба не нашла:
        /// впереди пусто, либо кандидат отброшен перегородкой. U такому ограждению
        /// уже спасает нижняя граница по СП 50.13330 табл. 3 — стена на улицу не может
        /// быть хуже нормируемой, — но КОНСТРУКЦИЯ при этом оставалась Unknown:
        /// 15 мм не проходят ни в утеплитель (λ 0,93), ни в несущий слой (толщина
        /// меньше 30 мм). Ни одна таблица приложения Г не подбиралась, узлы
        /// пропускались, r оставался единицей. На 76-СУЗДАЛ.23 это 218 м² одной
        /// только «Отделки Штук15», выходящей на улицу.</para>
        ///
        /// <para><b>Чем достраиваем.</b> Преобладающей по площади наружной конструкцией
        /// ЭТОГО ЖЕ объекта — той, что разобралась у остальных помещений. Рассуждение
        /// то же, что у нижней границы R: дом построен по одному альбому АР, и стена
        /// за отделкой — та же самая, которую видно в соседней комнате. На 76-СУЗДАЛ.23
        /// это подтверждено прямо: альбом 76-СУЗДАЛ.23-1-1-АР называет наружными
        /// стенами силикатные блоки СБПо-250 по всему объекту.</para>
        ///
        /// <para><b>Почему не молча.</b> Это допущение, а не данные модели. Профиль
        /// помечается <see cref="WallConstructionOrigin.DominantOfObject"/>, число
        /// таких помещений идёт в журнал и на лист «Параметры»: инженер должен видеть,
        /// у скольких помещений конструкция принята, а не прочитана.</para>
        /// </summary>
        private void AssignDominantConstruction(List<RoomData> rooms)
        {
            if (_roomsAwaitingDominantConstruction.Count == 0) return;

            var dominant = _streetConstructionArea.Values
                .OrderByDescending(t => t.Item2)
                .FirstOrDefault();

            if (dominant == null)
            {
                // Ни одной разобранной наружной стены на объекте. Подставлять здесь
                // нечего: выдумать конструкцию из ничего — ровно та ошибка, от которой
                // отказались с Ψ «из коробки» и с типовым U = 0,51.
                _constructionUnresolved += _roomsAwaitingDominantConstruction.Count;
                Logger.Warn(
                    $"[Конструкция] у {_roomsAwaitingDominantConstruction.Count} помещений " +
                    "ограждение на улицу есть, но конструкция не опознана НИ У ОДНОГО " +
                    "помещения объекта — принимать нечего. Мостики холода у них не считаются, " +
                    "теплопотери занижены. Причина обычно одна: несущая стена за отделкой " +
                    "не найдена пробой (см. [Промахи]).");
                _roomsAwaitingDominantConstruction.Clear();
                return;
            }

            var byId = rooms.ToLookup(r => r.Id);
            double totalArea = _streetConstructionArea.Values.Sum(t => t.Item2);

            foreach (int roomId in _roomsAwaitingDominantConstruction)
            {
                foreach (var room in byId[roomId])
                {
                    if (room.WallConstruction != null) continue;
                    room.WallConstruction =
                        dominant.Item1.WithOrigin(WallConstructionOrigin.DominantOfObject);
                    _constructionFromDominant++;
                }
            }

            Logger.Info(
                $"[Конструкция] у {_constructionFromDominant} помещений в уличном ограждении " +
                $"разбирать нечего (в модели остался отделочный слой) — принята ПРЕОБЛАДАЮЩАЯ " +
                $"конструкция объекта: {dominant.Item1.Construction}, " +
                $"λ_осн={dominant.Item1.BaseConductivity:F3}, " +
                $"R_ут={dominant.Item1.InsulationResistance:F2}, " +
                $"d_осн={dominant.Item1.BaseThicknessMm:F0} мм " +
                $"({dominant.Item2:F0} м² из {totalArea:F0} м² разобранных). " +
                "Это допущение, а не данные модели: узлы у таких помещений считаются " +
                "по таблицам соседней стены того же дома.");

            _roomsAwaitingDominantConstruction.Clear();
        }

        /// <summary>
        /// Помечает помещения, над которыми НЕТ отапливаемого помещения, — значит
        /// над ними кровля.
        ///
        /// Зачем: «верхний этаж» задаётся одним номером на всё здание, а верхний
        /// уровень обычно технический. На 76-СУЗДАЛ.23 верхним стал этаж 16
        /// с единственным помещением 16,5 м², и весь 15-й этаж (371,9 м², 37 помещений)
        /// остался без кровельных потерь — около 3,5 кВт, примерно 11% его Q_огр,
        /// причём именно там подбирают приборы.
        ///
        /// Метод: для каждого помещения берём БЛИЖАЙШИЙ уровень выше и проверяем,
        /// перекрывает ли хоть одно его помещение наш габарит в плане. Перекрытия
        /// нет — помещение под кровлей. Габариты берутся из <c>BoundaryPoints</c>
        /// (мм), сравнение по прямоугольнику: для плана этажа этого достаточно,
        /// а точная геометрия здесь и не нужна.
        /// </summary>
        private static void DetectRoomsUnderRoof(List<RoomData> rooms)
        {
            var levels = rooms
                .Where(r => r.BoundaryPoints != null && r.BoundaryPoints.Count >= 3)
                .GroupBy(r => Math.Round(r.Elevation, 2))
                .OrderBy(g => g.Key)
                .ToList();

            if (levels.Count < 2) return;

            int underRoof = 0;
            for (int i = 0; i < levels.Count; i++)
            {
                var above = i + 1 < levels.Count ? levels[i + 1].ToList() : null;

                foreach (var room in levels[i])
                {
                    double minX, minY, maxX, maxY;
                    GetPlanBounds(room, out minX, out minY, out maxX, out maxY);

                    bool covered = false;
                    if (above != null)
                    {
                        foreach (var upper in above)
                        {
                            double uMinX, uMinY, uMaxX, uMaxY;
                            GetPlanBounds(upper, out uMinX, out uMinY, out uMaxX, out uMaxY);

                            // Перекрытие габаритов в плане с небольшим запасом:
                            // касание углами за перекрытие не считаем.
                            const double toleranceMm = 100.0;
                            if (uMinX < maxX - toleranceMm && uMaxX > minX + toleranceMm &&
                                uMinY < maxY - toleranceMm && uMaxY > minY + toleranceMm)
                            {
                                covered = true;
                                break;
                            }
                        }
                    }

                    if (!covered)
                    {
                        room.IsUnderRoof = true;
                        underRoof++;
                    }
                }
            }

            Logger.Info($"Под кровлей (нет отапливаемого помещения сверху): {underRoof} " +
                        $"из {rooms.Count} помещений");
        }

        private static void GetPlanBounds(RoomData room,
            out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;
            foreach (var p in room.BoundaryPoints)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        /// <summary>
        /// Расчётная высота помещения, м. Единственное место, где сквозные общедомовые
        /// помещения ограничиваются одним этажом.
        ///
        /// Зачем ограничение: у лестничной клетки, лифтового холла и тамбура помещение
        /// на каждом этаже своё, но верхняя граница у них не задана, и
        /// <c>UnboundedHeight</c> возвращает высоту НА ВСЁ ЗДАНИЕ. Без ограничения
        /// стены такого помещения посчитались бы во всю высоту дома на КАЖДОМ этаже.
        ///
        /// Почему это здесь, а не в цикле по границам: раньше ограничение применялось
        /// локальной переменной внутри сбора площадей, а <c>RoomData.Height</c>
        /// оставался неограниченным — и его читали ещё три места:
        ///   • <c>DetermineRoomProperties</c> → <c>Volume = Area × Height</c>, откуда
        ///     воздухообмен по кратности объёма: у лестницы 45 м вместо 3 м это
        ///     пятнадцатикратная норма вытяжки;
        ///   • <c>CalculationEngine</c> → надбавка β = 0.02 за помещение выше 4 м,
        ///     которая доставалась всем сквозным помещениям подряд;
        ///   • <c>ReducedResistanceCalculator</c> → запасная длина вертикальных узлов.
        /// </summary>
        private static void ApplyEffectiveHeight(RoomData roomData, Room revitRoom)
        {
            double rawHeightM = 3.0;
            try
            {
                if (revitRoom.UnboundedHeight > 0)
                    rawHeightM = UnitUtils.ConvertFromInternalUnits(
                        revitRoom.UnboundedHeight, UnitTypeId.Meters);
            }
            catch (Exception ex)
            {
                Logger.Debug($"ApplyEffectiveHeight: UnboundedHeight недоступна: {ex.Message}");
            }

            bool isThroughBuilding =
                roomData.Category == RoomCategory.Stairs    ||
                roomData.Category == RoomCategory.Lobby     ||
                roomData.Category == RoomCategory.Vestibule ||
                roomData.Category == RoomCategory.Corridor;

            double effective = isThroughBuilding && rawHeightM > ThroughRoomHeightThresholdM
                ? DefaultFloorHeightM
                : rawHeightM;

            if (Math.Abs(effective - rawHeightM) > 0.01)
            {
                Logger.Info($"[Высота] {roomData.Number} «{roomData.Name}» " +
                            $"cat={roomData.Category}: UnboundedHeight={rawHeightM:F2}м — " +
                            $"сквозное помещение, ограничено до {effective:F2}м");
            }

            roomData.Height = effective;
            roomData.Volume = roomData.Area * effective;
        }

        /// <summary>Высота, выше которой помещение считается сквозным, м.</summary>
        private const double ThroughRoomHeightThresholdM = 4.5;

        /// <summary>Высота этажа по умолчанию, м.</summary>
        private const double DefaultFloorHeightM = 3.0;

        /// <summary>
        /// Направление «наружу» для сегмента границы: нормаль к сегменту, повёрнутая
        /// в ту сторону, где НЕТ этого помещения.
        ///
        /// Проверка идёт коротким пробным шагом (15 см): на таком расстоянии от
        /// границы внутрь помещения ещё точно попадаем, а в соседнее — ещё нет.
        ///
        /// Если обе стороны дали одинаковый ответ (помещение слишком узкое, границы
        /// кривые), берётся ТА ЖЕ нормаль, но знак выбирается по центроиду.
        /// Раньше в этом случае возвращался сам центроидный вектор — и у вытянутой
        /// или Г-образной комнаты он идёт ВДОЛЬ фасада, то есть ровно та беда,
        /// ради которой центроидный метод и заменили нормалью. Направление обязано
        /// остаться поперёк стены; неизвестен только знак, его и берём от центроида.
        /// Голый центроид возвращается лишь при вырожденном сегменте, у которого
        /// нормали нет вовсе.
        /// </summary>
        private static XYZ ResolveOutwardDirection(Curve curve, XYZ midPt, Room room,
                                                   XYZ roomCenter, Phase phase, Document doc)
        {
            XYZ centroidDirection = new XYZ(1, 0, 0);
            if (roomCenter != null)
            {
                double dx = midPt.X - roomCenter.X;
                double dy = midPt.Y - roomCenter.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 0.001) centroidDirection = new XYZ(dx / len, dy / len, 0);
            }

            try
            {
                XYZ tangent = (curve.GetEndPoint(1) - curve.GetEndPoint(0));
                double tangentLength = Math.Sqrt(tangent.X * tangent.X + tangent.Y * tangent.Y);
                if (tangentLength < 1e-6) return centroidDirection;

                XYZ normal = new XYZ(tangent.Y / tangentLength, -tangent.X / tangentLength, 0);

                // 15 см — заведомо внутри помещения, но заведомо не за стеной.
                const double insideProbeFt = 0.5;
                bool plusIsInside  = IsSameRoomAt(midPt, normal,  insideProbeFt, room, phase, doc);
                bool minusIsInside = IsSameRoomAt(midPt, normal, -insideProbeFt, room, phase, doc);

                if (plusIsInside && !minusIsInside) return new XYZ(-normal.X, -normal.Y, 0);
                if (minusIsInside && !plusIsInside) return normal;

                // Пробы не различили стороны. Направление всё равно берём нормальное —
                // знак выбираем по центроиду: наружу это «прочь от центра помещения».
                double towardsOutside = normal.X * centroidDirection.X +
                                        normal.Y * centroidDirection.Y;
                return towardsOutside >= 0
                    ? normal
                    : new XYZ(-normal.X, -normal.Y, 0);
            }
            catch (Exception ex)
            {
                Logger.Debug($"ResolveOutwardDirection: {ex.Message}");
            }

            return centroidDirection;
        }

        /// <summary>
        /// Помещение по ту сторону ограждения — марш СКВОЗЬ КОНСТРУКЦИЮ.
        ///
        /// <para><b>Что было.</b> Одна проба на фиксированных 2,0 ft ≈ 0,61 м
        /// с комментарием «перелетает стену любой толщины». На 76-СУЗДАЛ.23 это
        /// неверно: точка попадает ВНУТРЬ стены, <c>GetRoomAtPoint</c> отдаёт null,
        /// и сегмент объявляется улицей. Замер (прогон 2026-08-17): у 207 сегментов
        /// с вердиктом УЛИЦА за стеной стоит отапливаемое помещение, и ВСЕ
        /// они — на 0,69 м и дальше, ни одного ближе 0,61. Разделение идеальное,
        /// то есть причина именно в фиксированной дальности. Помещения эти —
        /// «Межквартирный коридор» на каждом этаже: стена квартиры в коридор
        /// считалась при полной ΔT = 49 К вместо примерно нуля.</para>
        ///
        /// <para><b>Почему не «сделать пробу длиннее».</b> Так уже пробовали
        /// 2026-08-06 (проба 3 м) и откатили: она теряла настоящие наружные стены
        /// кухонь, дотягиваясь до помещений через двор и через соседнюю секцию.
        /// Дальность — не доказательство. Здесь марш идёт, ПОКА ТОЧКА ВНУТРИ
        /// КОНСТРУКЦИИ: раз мы всё ещё в теле стены, здание не кончилось.
        /// Как только под точкой пусто, марш прекращается и дальше работает
        /// прежняя логика (улица либо шахта по <see cref="EnclosedVoid"/>) —
        /// поэтому настоящие наружные стены ведут себя ровно как раньше.</para>
        ///
        /// <para>Тот же класс дефекта, что «пусто за стеной ≠ улица» (шахты,
        /// 2026-08-13) и «за гранью колонны ≠ улица» (2026-08-12): вывод об улице
        /// делался из молчания <c>GetRoomAtPoint</c>, а молчит он по многим
        /// причинам.</para>
        /// </summary>
        private static Room RoomBehindWall(Room room, XYZ midPt, XYZ outward,
                                           Phase roomPhase, Document doc)
        {
            double stepFt = UnitUtils.ConvertToInternalUnits(ThroughWallStepM, UnitTypeId.Meters);
            double maxFt  = UnitUtils.ConvertToInternalUnits(ThroughWallMaxM, UnitTypeId.Meters);

            for (double d = FirstProbeOffsetFt; d <= maxFt + 1e-9; d += stepFt)
            {
                var probe = new XYZ(midPt.X + outward.X * d,
                                    midPt.Y + outward.Y * d,
                                    midPt.Z + 1.0);

                Room found = roomPhase != null
                    ? doc.GetRoomAtPoint(probe, roomPhase)
                    : doc.GetRoomAtPoint(probe);

                // Нашли своё же помещение — проба обогнула угол или нишу.
                // Идти дальше в этом направлении бессмысленно.
                if (found != null)
                    return found.Id.IntegerValue == room.Id.IntegerValue ? null : found;

                // Помещения нет. Если точка всё ещё в теле конструкции — здание
                // не кончилось, шагаем дальше. Если пусто — вышли наружу либо
                // в полость, и решать это должна прежняя логика, а не марш.
                if (WallsAtPoint(doc, probe, ElementId.InvalidElementId).Count == 0)
                    return null;
            }

            return null;
        }

        /// <summary>
        /// Первая проба — та же, что стояла единственной: 2,0 ft ≈ 0,61 м.
        /// У стены нормальной толщины ответ получается сразу, и марш не начинается.
        /// </summary>
        private const double FirstProbeOffsetFt = 2.0;

        /// <summary>Шаг марша сквозь конструкцию, м. Мельче любого слоя отделки.</summary>
        private const double ThroughWallStepM = 0.08;

        /// <summary>
        /// Докуда идёт марш сквозь конструкцию, м.
        ///
        /// <para>Ограничение намеренно жёсткое. Толще 1,2 м ограждающих конструкций
        /// в жилом доме не бывает, а всё, что дальше, — это уже попытка доказать
        /// «внутренность» дальностью, ровно та ошибка, из-за которой откатили
        /// пробу 3 м 2026-08-06. Замеренные на модели расстояния — 0,69…0,99 м.</para>
        /// </summary>
        private const double ThroughWallMaxM = 1.20;

        private static bool IsSameRoomAt(XYZ midPt, XYZ direction, double offsetFt,
                                         Room room, Phase phase, Document doc)
        {
            var probe = new XYZ(midPt.X + direction.X * offsetFt,
                                midPt.Y + direction.Y * offsetFt,
                                midPt.Z + 1.0);
            var found = phase != null ? doc.GetRoomAtPoint(probe, phase) : doc.GetRoomAtPoint(probe);
            return found != null && found.Id == room.Id;
        }

        /// <summary>
        /// Помещение за стеной отапливаемое — значит стена внутренняя, чем бы её
        /// ни объявлял тип. Лоджии, лестницы, тамбуры, технические помещения
        /// и подвалы отапливаемыми НЕ считаются: стена к ним остаётся ограждающей.
        /// </summary>
        private static bool IsHeatedNeighbour(Room neighbour)
        {
            var category = DetectNeighbourCategory(neighbour);
            return category.HasValue &&
                   !ThermalConstants.UnheatedOrCommonCategories.Contains(category.Value);
        }

        /// <summary>
        /// Категория помещения за стеной. null — помещения там нет (улица) либо имя
        /// не прочиталось. Нужна не только для вето по отапливаемому соседу, но и
        /// для расчётной температуры за ограждением: лоджия 5 °C, лестница 16 °C.
        /// </summary>
        private static RoomCategory? DetectNeighbourCategory(Room neighbour)
        {
            if (neighbour == null) return null;
            try
            {
                string name = neighbour.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString()
                              ?? neighbour.Name;
                return RoomCategoryHelper.Detect(name);
            }
            catch (Exception ex)
            {
                Logger.Debug($"DetectNeighbourCategory: {ex.Message}");
                return null;
            }
        }

        private void CalculateRoomAreasFromBoundary(RoomData roomData, Room revitRoom, Document doc)
        {
            double extWallNetAreaM2 = 0;
            double extWindowAreaM2  = 0;
            double extDoorAreaM2    = 0;
            int    extSegmentCount  = 0;
            int    totalSegments    = 0;
            double totalWallUA      = 0; // сумма U×A для взвешенного среднего U
            double totalWallNetM2   = 0; // сумма нетто-площадей
            double largestWallAreaM2 = 0; // площадь самой большой наружной стены помещения

            // Запасной кандидат на конструкцию: самое крупное ограждение НЕ на улицу
            // (лоджия, шахта, лестница). Нужен помещениям, у которых уличных стен нет
            // вовсе — потери у них идут через эти самые ограждения, и узлы там такие же
            // реальные. Слои по нему разбираются ОДИН раз после цикла: делать это
            // на каждом сегменте значило бы запрашивать геометрию впустую.
            Wall       fallbackWall    = null;
            List<Wall> fallbackStack   = null;
            double     fallbackAreaM2  = 0;
            bool       hasStreetWall   = false;

            // Сегменты границы, выходящие НА УЛИЦУ, по адресу «цикл, номер в цикле».
            // Угол наружных стен — это стык двух таких соседей, и распознать его
            // можно только зная порядок обхода.
            var streetSegments = new HashSet<Tuple<int, int>>();

            // РАЗНЫЕ наружные стены помещения — именно они, а не сегменты границы,
            // отвечают на вопрос «сколько у помещения наружных стен».
            // Один фасад режется на несколько граничных сегментов (полилиния ломается
            // на примыканиях перегородок, а отделка часто набрана полосами), поэтому
            // счётчик сегментов даёт «8 наружных стен» у межквартирного коридора.
            // От этого числа зависит β = 0.05 за угловое помещение и длины углов в R_пр.
            // Ключ — Id НЕСУЩЕЙ стены (thermalWall): несколько полос отделки перед
            // одной кладкой схлопываются в одну стену, как и должно быть.
            var externalWallIds = new HashSet<int>();

            roomData.Walls.Clear();      // пересбор помещения не должен удваивать стены

            var boundaryOptions = new SpatialElementBoundaryOptions();
            var boundaries      = revitRoom.GetBoundarySegments(boundaryOptions);
            if (boundaries == null)
            {
                Logger.Debug($"[BoundaryAnalysis] {roomData.Name}: GetBoundarySegments вернул null");
                return;
            }

            var windowsByHost = roomData.Windows.ToLookup(w => w.HostWallId?.IntegerValue ?? -1);
            var doorsByHost   = roomData.Doors  .ToLookup(d => d.HostWallId?.IntegerValue ?? -1);

            // Проёмы, уже вычтенные из какого-то сегмента этого помещения. Одну несущую
            // стену закрывает несколько полос отделки, и у стыка окно попадает в допуск
            // сразу двух сегментов — вычесть его дважды хуже, чем не вычесть вовсе.
            var consumedOpenings = new HashSet<int>();

            // ── Центроид помещения: гарантированно внутри Room ──────────────────
            // Вектор (midPt → roomCenter) = "внутрь"
            // Вектор (roomCenter → midPt) = "наружу" — не зависит от CW/CCW обхода
            XYZ roomCenter = null;
            if (revitRoom.Location is LocationPoint lp)
                roomCenter = lp.Point;

            // Фаза помещения для корректного GetRoomAtPoint — одна реализация
            // на проект, см. ElementCollectorService.GetRoomPhase.
            Phase roomPhase = ElementCollectorService.GetRoomPhase(revitRoom);

            // Дальность пробы больше не константа этого метода: см. RoomBehindWall.
            // Комментарий, стоявший здесь, — «≈ 60 см, перелетает стену любой
            // толщины» — оказался неверным для этого дома и стоил 207 сегментов,
            // объявленных улицей при отапливаемом помещении за стеной.

            // Обвод помещения в плане — для зонального метода. Берётся ПЕРВЫЙ цикл
            // границы: у Revit это внешний контур, остальные — вырезы (колонны,
            // шахты внутри помещения). Вырез уменьшает площадь, а её мы берём
            // из модели отдельно, поэтому на раскладку по зонам он не влияет.
            roomData.FloorOutline = OutlineOf(boundaries.FirstOrDefault());

            // Циклы и сегменты обходятся ПО ИНДЕКСАМ: соседство сегментов в цикле —
            // это и есть угол помещения, а без индекса соседа не назвать.
            for (int loopIndex = 0; loopIndex < boundaries.Count; loopIndex++)
            {
                var loop = boundaries[loopIndex];
                for (int segIndex = 0; segIndex < loop.Count; segIndex++)
                {
                    var segment = loop[segIndex];
                    totalSegments++;
                    try
                    {
                        var curve = segment.GetCurve();
                        if (curve == null || curve.Length < 0.01) continue;

                        XYZ midPt = curve.Evaluate(0.5, true);

                        // ── Направление «наружу» — НОРМАЛЬ К СЕГМЕНТУ, а не вектор от центра ──
                        //
                        // Центроидный метод (был здесь до 2026-08-06) устойчив к направлению
                        // обхода полилинии, но у вытянутой или Г-образной комнаты вектор
                        // «центр → середина сегмента» идёт ВДОЛЬ фасада, а не поперёк него,
                        // и проба на 60 см залетает в соседнюю квартиру. На модели
                        // 76-СУЗДАЛ.23 это дало 1016 сегментов, за которыми «нашлось»
                        // помещение: 416 жилых комнат, 168 кухонь-столовых, 100 кухонь.
                        //
                        // Нормаль к сегменту всегда поперёк стены; из двух её направлений
                        // выбираем то, с которого НЕ видно это же помещение.
                        XYZ outward = ResolveOutwardDirection(curve, midPt, revitRoom, roomCenter, roomPhase, doc);

                        // Нет комнаты за стеной → наружная (не зависит от фазы и CW/CCW).
                        // Проба идёт МАРШЕМ СКВОЗЬ КОНСТРУКЦИЮ, а не одной точкой —
                        // причина в RoomBehindWall.
                        Room outRoom = RoomBehindWall(revitRoom, midPt, outward, roomPhase, doc);

                        ElementId wallElemId = segment.ElementId;
                        Wall wall = doc.GetElement(wallElemId) as Wall;

                        // ── Определение наружной стены: имя + Function ──────────────
                        string wallTypeName = wall?.WallType?.Name ?? "";
                        string wallNameLower = wallTypeName.ToLowerInvariant();

                        // Диагностика: если wall == null — это линия разделения помещений, не стена
                        if (wall == null)
                        {
                            // Что именно попалось — важно: границу могут образовывать
                            // не только линии разделения, но и панели навесной стены.
                            // Панель — не Wall, и раньше такой сегмент выпадал молча.
                            var boundaryElement = wallElemId != ElementId.InvalidElementId
                                ? doc.GetElement(wallElemId)
                                : null;

                            double segLenMeters = UnitUtils.ConvertFromInternalUnits(
                                curve.Length, UnitTypeId.Meters);

                            // ── КОЛОННА в наружной границе ─────────────────────────────
                            // Железобетонная колонна, выходящая на улицу, — ограждающая
                            // конструкция и мостик холода, а не «другой тип элемента».
                            // Её сегмент границы выпадал целиком: doc.GetElement(id) as Wall
                            // даёт null. На 76-СУЗДАЛ.23 это около 60 сегментов за прогон
                            // («Колонна Бетон 1000х250», 1500х250, 650х300…).
                            if (IsColumn(boundaryElement))
                            {
                                // Общая проба (60 см) для колонны не годится: она короче
                                // самой колонны, остаётся в её теле и любую грань объявляет
                                // наружной. Отсюда своя, привязанная к габариту элемента.
                                Room columnOutRoom;
                                List<Wall> columnCover;
                                var columnVerdict = ProbeColumnFace(
                                    boundaryElement, midPt, outward, roomPhase,
                                    doc, out columnOutRoom, out columnCover);

                                string columnHead =
                                    $"  Seg ElemId={wallElemId.IntegerValue} → КОЛОННА " +
                                    $"«{boundaryElement.Name}» L={segLenMeters:F2}м";

                                // За гранью отапливаемый объём (в том числе САМО это
                                // помещение — так выглядит колонна, стоящая внутри него).
                                if (columnVerdict == ColumnFaceVerdict.IntoRoom &&
                                    (columnOutRoom.Id == revitRoom.Id || IsHeatedNeighbour(columnOutRoom)))
                                {
                                    _columnFacesInside++;
                                    Logger.Debug(
                                        $"{columnHead} → за гранью отапливаемый объём " +
                                        $"({(columnOutRoom.Id == revitRoom.Id ? "это же помещение" : columnOutRoom.Name)}) " +
                                        "— не ограждение, ПРОПУЩЕНА");
                                    continue;
                                }

                                // Грань упёрлась в конструкцию, за которой наружу не выйти:
                                // так выглядит боковая грань колонны, утопленной в кладке.
                                // Эта площадь уже посчитана как стена — считать её второй раз
                                // хуже, чем не посчитать вовсе.
                                if (columnVerdict == ColumnFaceVerdict.Buried)
                                {
                                    _columnFacesBuried++;
                                    Logger.Debug(
                                        $"{columnHead} → грань не выходит наружу " +
                                        $"(упирается в {DescribeCover(columnCover)}) — ПРОПУЩЕНА");
                                    continue;
                                }

                                RoomCategory? columnAdjacent =
                                    columnVerdict == ColumnFaceVerdict.IntoRoom
                                        ? DetectNeighbourCategory(columnOutRoom)
                                        : null;

                                {
                                    double columnThicknessM;
                                    string columnCoverNote;
                                    double columnU = ResolveColumnU(
                                        boundaryElement, segDirOf(curve), columnCover,
                                        out columnThicknessM, out columnCoverNote);
                                    double columnAreaM2 = segLenMeters * roomData.Height;

                                    extSegmentCount++;
                                    extWallNetAreaM2 += columnAreaM2;
                                    totalWallUA      += columnU * columnAreaM2;
                                    totalWallNetM2   += columnAreaM2;

                                    roomData.Walls.Add(new WallInfo
                                    {
                                        Id          = boundaryElement.Id,
                                        TypeName    = boundaryElement.Name,
                                        Length      = segLenMeters,
                                        Height      = roomData.Height,
                                        Area        = columnAreaM2,
                                        UValue      = columnU,
                                        RValue      = columnU > 0 ? 1.0 / columnU : 0,
                                        IsExternal  = true,
                                        // Толщина и материал колонны — из модели; покрытие
                                        // засчитано, только если у него нашлись данные.
                                        ThermalFromModel = true,
                                        ThermalSource    = "колонна: d/λ" +
                                                           (columnCoverNote.StartsWith("покрытия нет")
                                                               ? " без покрытия"
                                                               : " + покрытие"),
                                        Orientation = _orientationCalculator.FromNormal(outward),
                                        FloorNumber = roomData.FloorNumber,
                                        AdjacentCategory = columnAdjacent,
                                        AdjacentRoomId   = columnAdjacent.HasValue && columnOutRoom != null
                                                           ? columnOutRoom.Id.IntegerValue : 0
                                    });

                                    // В счёт «наружных стен» колонна НЕ идёт: это фрагмент
                                    // того же фасада, а не отдельная сторона помещения,
                                    // и надбавка за угловое от неё возникать не должна.
                                    _columnFacesCounted++;
                                    Logger.Debug(
                                        $"{columnHead} " +
                                        $"толщина={columnThicknessM:F3}м U={columnU:F3} " +
                                        $"S={columnAreaM2:F2}м² {columnCoverNote} " +
                                        $"за={(columnAdjacent.HasValue ? columnAdjacent.Value.ToString() : "улица")} " +
                                        "→ учтена как ограждение");
                                    continue;
                                }
                            }

                            // Длина и то, что за сегментом, — чтобы было видно, теряется
                            // ли на этом реальная площадь ограждения.
                            Logger.Debug(
                                $"  Seg ElemId={wallElemId.IntegerValue} " +
                                $"→ НЕ СТЕНА ({boundaryElement?.Category?.Name ?? "нет элемента"}" +
                                $"{(boundaryElement != null ? $", «{boundaryElement.Name}»" : "")}) " +
                                $"L={segLenMeters:F2}м " +
                                $"outRoom={outRoom?.Name ?? "null"}, ПРОПУЩЕНО");
                            continue;
                        }

                        // Критерий 1: имя типа явно говорит "наружная"
                        bool nameIsExterior = wallNameLower.Contains("наруж") ||
                                              wallNameLower.Contains("фасад") ||
                                              wallNameLower.Contains("exterior");

                        // Критерий 2: Function=Exterior, НО исключаем ложные срабатывания.
                        //
                        // «Витраж» из этого списка УБРАН 2026-08-10. Он выбрасывал
                        // остеклённый фасад целиком: витраж не проходил ни как стена
                        // (исключён здесь), ни как окно (панели навесной стены — это
                        // не категория «Окна»), и офисы первого этажа получали ноль
                        // теплопотерь через всё остекление. На 76-СУЗДАЛ.23 это давало
                        // −7,5% по первому этажу против расчёта проектировщика.
                        bool funcIsExterior = false;
                        WallFunction? wallFuncValue = null;
                        try
                        {
                            wallFuncValue = wall.WallType?.Function;
                            if (wallFuncValue == WallFunction.Exterior)
                            {
                                bool excluded = wallNameLower.Contains("перегородка") ||
                                                wallNameLower.Contains("вентшахта") ||
                                                wallNameLower.Contains("парапет");
                                funcIsExterior = !excluded;
                            }
                        }
                        catch (Exception ex) { Logger.Debug($"Wall.Function недоступен для {wall.Id.IntegerValue}: {ex.Message}"); }

                        // Критерий 5: навесная стена (витраж). У её типа Function часто
                        // не выставлена вовсе (`func=False` в журнале), слоёв нет,
                        // и по имени она тоже не «наружная» — то есть ни один из прочих
                        // критериев не срабатывает. При этом витраж по определению
                        // ограждает: непрозрачной навесной стены внутри здания не бывает.
                        bool isCurtainWall = IsCurtainWall(wall);

                        // Критерий 3: в этот сегмент стены вставлено окно → 100% наружная
                        bool hasHostedWindow = windowsByHost[wallElemId.IntegerValue].Any();

                        // Критерий 4: отделочный слой (штукатурка, гипс и т.п.) на границе помещения.
                        // Отделка сама по себе не несущая и не имеет "наруж" в имени,
                        // но может стоять перед наружной стеной. Признак: outRoom==null.
                        // Лестничные клетки не попадут — они не называются "Отделка Штук15".
                        //
                        // ИЗВЕСТНОЕ ОГРАНИЧЕНИЕ (не чинить здесь без ray-cast против реальной
                        // геометрии стен — два варианта на пробах уже не сработали, см. ниже).
                        // outRoom==null сам по себе означает лишь "Revit не нашёл Room по ту
                        // сторону", а это верно и для настоящей улицы, и для шахты лифта / зазора
                        // у лестницы — GetRoomAtPoint их не отличает. На модели 76-СУЗДАЛ.23 это
                        // завышает WallArea у МОП-коридоров (все их ограждения — один тип "Отделка
                        // ... МОП" независимо от того, что за ними). Проверено два варианта отсечь
                        // ложные срабатывания: (1) требовать, чтобы ResolveThermalWall нашла за
                        // отделкой настоящую несущую стену — не годится, она на этой же модели то
                        // находится, то нет и для точно наружных стен обычных квартир тоже;
                        // (2) контрольный пробой на 3 м вместо 60 см — снял только часть завышения
                        // у коридоров и при этом сам начал терять настоящие наружные стены кухонь.
                        // Оба варианта отменены после сверки фикстурой. МОП-коридоры без квартиры
                        // теперь просто снимаются с расчёта по умолчанию (MainWindow.LoadData) —
                        // это защищает итог по квартирам; отдельный расчёт МОП по этой же геометрии
                        // до починки будет давать завышенные числа.
                        bool isFinishLayer = wallNameLower.Contains("отделка") ||
                                             wallNameLower.Contains("штук")    ||
                                             wallNameLower.Contains("шпакл")   ||
                                             wallNameLower.Contains("гипс");
                        bool finishIsExterior = isFinishLayer && (outRoom == null);

                        // ── «Пусто за стеной» ≠ «улица»: шахта ──────────────────────────
                        // Ровно тот дефект, что описан абзацем выше как известное
                        // ограничение. GetRoomAtPoint молчит и над тротуаром, и внутри
                        // вентшахты, а шахта проходит СКВОЗЬ отапливаемый объём здания.
                        // Замер на 76-СУЗДАЛ.23: 25 санузлов, 127 м² «наружных» стен,
                        // 5,9% Q огр здания, по 104 Вт/м² против типовых 39.
                        //
                        // Проба идёт только там, где ответ ещё не известен: помещения
                        // за стеной нет, окна в ней нет, и сама модель не объявила её
                        // наружной ни именем, ни функцией. Стену, которую инженер назвал
                        // «Наруж…» или «Фасад…», геометрия не переспрашивает.
                        bool   facesShaft = false;
                        string shaftNote  = null;
                        if (outRoom == null && !hasHostedWindow && !isCurtainWall &&
                            !nameIsExterior && !funcIsExterior)
                        {
                            facesShaft = ResolveBeyondWall(wall, revitRoom, midPt, outward,
                                                           roomPhase, doc, out shaftNote) == BeyondWall.Shaft;
                        }

                        // ── Отапливаемый сосед перекрывает ВСЕ признаки наружности ──
                        // Тип стены объявляет функцию для всей стены целиком, а стена
                        // идёт через всё здание: подвальный монолит «Exterior» служит
                        // и наружной стеной, и границей между кладовыми. Раньше признак
                        // типа выигрывал, и кладовая 3 м² получала 8,8 м² «наружных»
                        // стен неутеплённого бетона (U = 2,19) — сотни лишних ватт
                        // на каждом таком помещении.
                        //
                        // Лоджия, лестница, тамбур, техпомещение и подвал отапливаемыми
                        // не считаются: стена к ним остаётся ограждающей. Именно на этом
                        // сорвались две прошлые попытки — они убирали и такие стены.
                        // ── Что за стеной: улица, отапливаемое или НЕотапливаемое ──
                        RoomCategory? neighbourCategory = null;
                        bool heatedNeighbour   = false;
                        bool unheatedNeighbour = false;
                        if (outRoom != null && outRoom.Id != revitRoom.Id)
                        {
                            neighbourCategory = DetectNeighbourCategory(outRoom);
                            heatedNeighbour = neighbourCategory.HasValue &&
                                !ThermalConstants.UnheatedOrCommonCategories.Contains(neighbourCategory.Value);
                            unheatedNeighbour = !heatedNeighbour;
                        }

                        // НЕотапливаемый сосед — САМОСТОЯТЕЛЬНЫЙ признак ограждающей
                        // конструкции. Раньше он работал только как право вето
                        // («отапливаемый сосед делает стену внутренней»), а обратный
                        // случай не рассматривался вовсе: стена «кухня — лоджия»
                        // не проходила ни по имени, ни по функции, а finishIsExterior
                        // требует outRoom == null — за ней же нашлась «Лоджия 1».
                        // Итог: ~292 ограждения на прогон 76-СУЗДАЛ.23 считались
                        // с нулевыми потерями, а оконно-балконные блоки в них
                        // не находились — 296 окон из 517 остались непривязанными.
                        // Шахта — САМОСТОЯТЕЛЬНЫЙ признак ограждения, как и неотапливаемый
                        // сосед: за стеной неотапливаемый объём, просто помещением его
                        // не назвали. Без этого слагаемого стена в шахту, не прошедшая
                        // ни по имени, ни по отделке, выпадала бы из расчёта совсем.
                        bool isExterior = !heatedNeighbour &&
                            (nameIsExterior || funcIsExterior || hasHostedWindow ||
                             finishIsExterior || unheatedNeighbour || isCurtainWall || facesShaft);

                        // Категория ЗА ограждением: null — наружный воздух.
                        // Шахта перекрывает прочие исходы: пустота за стеной уже разобрана
                        // маршем, и это не улица.
                        RoomCategory? adjacentCategory = facesShaft
                            ? RoomCategory.Shaft
                            : (unheatedNeighbour ? neighbourCategory : null);

                        // Id соседнего объёма: по нему движок берёт температуру,
                        // ПОСЧИТАННУЮ балансом для этой самой лоджии, а не общее
                        // значение категории (СП 50.13330 п. 5.2). У шахты помещения
                        // нет, поэтому 0 — там остаётся таблица.
                        int adjacentRoomId = (!facesShaft && unheatedNeighbour && outRoom != null)
                            ? outRoom.Id.IntegerValue
                            : 0;

                        if (facesShaft)
                        {
                            _shaftFacesDetected++;
                            Logger.Debug(
                                $"  Seg WallId={wallElemId.IntegerValue} «{wallTypeName}» → ШАХТА: {shaftNote}");
                        }

                        Logger.Debug(
                            $"  Seg WallId={wallElemId.IntegerValue} " +
                            $"Type=\"{wallTypeName}\" " +
                            $"name={nameIsExterior} func={funcIsExterior} win={hasHostedWindow} " +
                            $"heatedNb={heatedNeighbour} shaft={facesShaft} " +
                            $"за={(adjacentCategory.HasValue ? adjacentCategory.Value.ToString() : "улица")} " +
                            $"L={UnitUtils.ConvertFromInternalUnits(curve.Length, UnitTypeId.Meters):F2}м " +
                            $"outRoom={outRoom?.Name ?? "null"} " +
                            $"→ {(isExterior ? "НАРУЖНАЯ" : "внутренняя")}");

                        if (!isExterior) continue;
                        extSegmentCount++;

                        // ── Витраж считается ОСТЕКЛЕНИЕМ, а не стеной ──────────────────
                        // Навесная стена целиком светопрозрачна, поэтому весь сегмент
                        // уходит в окна помещения: так его видит инженер, так он попадает
                        // в колонку «Окна» отчёта и получает U остекления, а не кладки.
                        // Отдельного WallInfo для него не создаётся — иначе площадь
                        // учлась бы дважды.
                        if (isCurtainWall)
                        {
                            double curtainHeightM = roomData.Height;
                            double curtainLenM = UnitUtils.ConvertFromInternalUnits(curve.Length, UnitTypeId.Meters);
                            double curtainAreaM2 = curtainLenM * curtainHeightM;
                            double curtainU = ResolveCurtainWallU(wall);

                            roomData.Windows.Add(new WindowInfo
                            {
                                Id          = wall.Id,
                                TypeName    = wallTypeName,
                                Width       = curtainLenM,
                                Height      = curtainHeightM,
                                Area        = curtainAreaM2,
                                UValue      = curtainU,
                                HostWallId  = wall.Id,
                                Orientation = _orientationCalculator.FromNormal(outward),
                                AdjacentCategory = adjacentCategory,
                                AdjacentRoomId   = adjacentRoomId,
                                IsCurtainGlazing = true
                            });
                            extWindowAreaM2 += curtainAreaM2;

                            if (!adjacentCategory.HasValue)
                                externalWallIds.Add(wall.Id.IntegerValue);

                            Logger.Debug(
                                $"    Витраж «{wallTypeName}»: L={curtainLenM:F2}м H={curtainHeightM:F2}м " +
                                $"S={curtainAreaM2:F2}м² U={curtainU:F3} → учтён как остекление");
                            continue;
                        }

                        // Высота уже ограничена в roomData.Height (см. ApplyEffectiveHeight):
                        // раньше ограничение жило ТОЛЬКО здесь, локальной переменной,
                        // и объём с надбавками читали неограниченную величину.
                        double roomHeightM = roomData.Height;

                        double segLengthM = UnitUtils.ConvertFromInternalUnits(curve.Length, UnitTypeId.Meters);
                        double grossM2    = segLengthM * roomHeightM;

                        // ── Несущая стена за отделочным слоем ───────────────────────────
                        // Границу помещения часто образует отдельная тонкая стена отделки
                        // («Отделка Штук15»), а настоящая наружная — силикатный блок,
                        // утеплитель и фасад — стоит за ней отдельным элементом.
                        // Брать теплотехнику с отделки нельзя: у неё нет ни слоёв,
                        // ни сопротивления, и расчёт молча уходил на типовое U.
                        Wall thermalWall = ResolveThermalWall(wall, midPt, outward, doc) ?? wall;

                        // В счёт «наружных стен» (а значит и в β за угловое помещение)
                        // идут только ограждения к НАРУЖНОМУ ВОЗДУХУ. Стена на лоджию —
                        // ограждающая, но угловым помещение от неё не становится:
                        // надбавка по СП про обдув и инсоляцию, а не про любую ΔT.
                        if (!adjacentCategory.HasValue)
                            externalWallIds.Add(thermalWall.Id.IntegerValue);

                        // ── U-значение стены: единая реализация в WallThermalCalculator ─────
                        // Ограждение — это ВСЯ сборка: несущая стена плюс фасадные
                        // конструкции, стоящие перед ней отдельными элементами.
                        // У стены в шахту фасадной системы нет по определению, а марш
                        // наружу идёт там по пустоте и может подобрать ДАЛЬНЮЮ стенку
                        // шахты — чужое ограждение, которое завысит сопротивление.
                        var facadeStack = facesShaft
                            ? new List<Wall>()
                            : ResolveFacadeStack(wall, thermalWall, midPt, outward, roomPhase, doc, revitRoom,
                                  adjacentCategory.HasValue
                                      ? "сосед: " + RoomCategoryHelper.GetRussianLabel(adjacentCategory.Value)
                                      : "УЛИЦА");
                        // Контур здания для зонального метода: сегмент, выходящий
                        // на НАРУЖНЫЙ ВОЗДУХ. Стена к лоджии, шахте или соседнему
                        // помещению контуром не является — от неё зоны не отсчитываются.
                        if (!adjacentCategory.HasValue && !facesShaft)
                            AddContourSegment(roomData.LevelId, curve);

                        var wallThermal = _wallCalculator.CalculateAssembly(thermalWall, facadeStack);
                        double wallUValue = wallThermal.UValue;

                        // Ограждение НА УЛИЦУ не может быть хуже требований
                        // СП 50.13330 таблица 3: дом с такой стеной не прошёл бы
                        // экспертизу. Если модель даёт хуже — в модели нет данных,
                        // а не стена такая. Проверено пробой: перед монолитом
                        // 200 мм на 70 см в модели нет ничего (прогон 2026-08-17).
                        //
                        // Только к наружному воздуху и только вверх: к шахте,
                        // лестнице и лоджии норматив требований не предъявляет,
                        // и там конструкция ДЕЙСТВИТЕЛЬНО бывает неутеплённой.
                        if (DegreeDays > 0 && !adjacentCategory.HasValue && !facesShaft && wallUValue > 0)
                        {
                            bool raised;
                            double r = NormativeResistance.ApplyFloor(
                                1.0 / wallUValue, NormativeResistance.Enclosure.Wall, DegreeDays, out raised);
                            if (raised)
                            {
                                wallUValue = 1.0 / r;
                                wallThermal.RValue = r;
                                wallThermal.UValue = wallUValue;
                                wallThermal.RaisedToNormative = true;
                                _normativeFloorCount++;
                            }
                        }

                        // Учёт для сводки: нашёлся ли перед стеной фасад. Сегмент
                        // без фасада — это либо стена к лоджии, шахте или лестнице
                        // (утеплителя там и правда нет), либо непойманная фасадная
                        // система. Различить их можно только по соседу, поэтому
                        // счётчики раздельные.
                        if (facesShaft)          _facadeStackSkippedShaft++;
                        else if (facadeStack.Count > 0) _facadeStackFound++;
                        else                     _facadeStackEmpty++;

                        // ── Проёмы этого сегмента ───────────────────────────────────────
                        // Окно вставлено в НЕСУЩУЮ стену, а границу помещения образует
                        // отделочный слой — это разные элементы с разными Id. Поиск шёл
                        // только по Id граничной стены, и на 76-СУЗДАЛ.23 совпало
                        // 26 сегментов из 10 340: площадь окон не вычиталась из стен
                        // ВООБЩЕ («брутто 14 869 м², вычтено окон 0 м²»), а потери через
                        // окна движок при этом считал по room.Windows. То есть площадь
                        // остекления учитывалась ДВАЖДЫ — как глухая стена и как окно.
                        // Расхождение с расчётом проектировщика по этой же секции
                        // (+27,6 кВт, +6,4%) сходится с этой двойной площадью.
                        //
                        // Ищем проёмы и по граничной стене, и по несущей за ней. Но одного
                        // Id мало: фасад набран из НЕСКОЛЬКИХ элементов одного типа, а
                        // ResolveThermalWall отбирает кандидатов через BoundingBoxIntersectsFilter,
                        // то есть по ГАБАРИТНОМУ ПАРАЛЛЕЛЕПИПЕДУ. У длинной стены он огромен,
                        // и проба уверенно цепляет соседний элемент того же типа. Для
                        // теплотехники это безразлично (тип один — U один), а для привязки
                        // окна по Id фатально: на 76-СУЗДАЛ.23 все 231 непривязанных окна
                        // сидели в «Наруж стена Силикатныйблок250» — ровно в том типе,
                        // который проба и находила, только в другом экземпляре.
                        // Поэтому Id — быстрый точный путь, а решает ГЕОМЕТРИЯ.
                        //
                        // Проверку «хост того же ТИПА» пробовали 2026-08-10 и отменили:
                        // из 574 привязок по ней прошли ДВЕ. У «Кухни 2» окно сидит
                        // в «Силикатныйблок250», а сегмент на лоджию разрешился
                        // в «Силикатныйблок390» — типы разные, и фильтр отсекал ровно
                        // те окна, ради которых затевался. Тип стены-хоста ни о чём
                        // не говорит: ResolveThermalWall и так возвращает произвольный
                        // элемент из тех, чей габарит накрыл пробу.
                        var segmentHostIds = new HashSet<int> { wallElemId.IntegerValue };
                        segmentHostIds.Add(thermalWall.Id.IntegerValue);

                        // ── Фильтрация окон/дверей по положению вдоль сегмента ──────────────────
                        // Проблема: одна длинная наружная стена может граничить с кухней И жилой
                        // комнатой. windowsByHost[wallId] вернёт ВСЕ окна стены — и кухонное,
                        // и окна жилой. Проверяем, что окно лежит В ПРЕДЕЛАХ длины этого сегмента.
                        // Плюс одну несущую стену закрывает НЕСКОЛЬКО полос отделки, и у стыка
                        // окно может попасть в допуск сразу двух сегментов — поэтому каждый
                        // проём засчитывается помещению один раз (consumedOpenings).
                        XYZ segStart = curve.GetEndPoint(0);
                        XYZ segEnd   = curve.GetEndPoint(1);
                        // Единичный вектор вдоль сегмента
                        XYZ segDir = (segEnd - segStart);
                        double segLenFt = segDir.GetLength(); // в ft (внутренние единицы)
                        if (segLenFt > 0.001) segDir = segDir.Normalize();

                        const double winTolerance = 0.5; // ft ≈ 15 см допуск по краям
                        // Поперечный допуск: центр окна лежит на оси стены, а граница
                        // помещения — по отделочной поверхности. Между ними отделка,
                        // зазор и половина толщины стены.
                        //
                        // ЗАМЕРЕНО на 76-СУЗДАЛ.23 (824 привязки): по хосту отклонение
                        // 0,14–0,15 м, по геометрии 0,08–0,48 м при медиане 0,14 м.
                        // Порога 0,75 м не достиг никто, то есть запас больше половины.
                        // Сужать до замеренного максимума не стоит: на модели с более
                        // толстыми стенами смещение больше, а верхняя граница всё равно
                        // задана шириной самого узкого помещения — окно в противоположной
                        // стене поймать нельзя.
                        double perpToleranceFt = UnitUtils.ConvertToInternalUnits(0.75, UnitTypeId.Meters);

                        double openingsM2 = 0;
                        foreach (var win in roomData.Windows)
                        {
                            // Остекление витража — не проём в этой стене: оно и есть
                            // отдельный сегмент границы, вычитать его неоткуда.
                            if (win.IsCurtainGlazing) continue;

                            int winId = win.Id?.IntegerValue ?? -1;
                            if (winId >= 0 && consumedOpenings.Contains(winId)) continue;

                            // Точное совпадение по хосту — быстрый путь; во всех остальных
                            // случаях принадлежность решает ГЕОМЕТРИЯ. Окно уже отнесено
                            // к этому помещению пробой с обеих сторон стены-хоста, так что
                            // кандидаты заведомо «свои»; остаётся выбрать, к какому сегменту.
                            int hostId = win.HostWallId?.IntegerValue ?? -1;
                            bool exactHost = hostId >= 0 && segmentHostIds.Contains(hostId);

                            XYZ winPt = null;
                            try
                            {
                                var winFi = doc.GetElement(win.Id) as Autodesk.Revit.DB.FamilyInstance;
                                // Способ 1: LocationPoint
                                winPt = (winFi?.Location as Autodesk.Revit.DB.LocationPoint)?.Point;
                                // Способ 2: центр BoundingBox (надёжнее для hosted-элементов)
                                if (winPt == null && winFi != null)
                                {
                                    var bb = winFi.get_BoundingBox(null);
                                    if (bb != null)
                                        winPt = new XYZ(
                                            (bb.Min.X + bb.Max.X) / 2.0,
                                            (bb.Min.Y + bb.Max.Y) / 2.0,
                                            (bb.Min.Z + bb.Max.Z) / 2.0);
                                }
                            }
                            catch (Exception ex) { Logger.Debug($"Не удалось получить позицию окна: {ex.Message}"); }

                            if (winPt == null)
                            {
                                // Позиция совсем неизвестна — пропускаем (не включаем по умолчанию чтобы не завысить)
                                if (exactHost)
                                    Logger.Debug($"    Окно {win.Area:F2}м² — позиция неизвестна, ПРОПУЩЕНО");
                                continue;
                            }

                            XYZ toWin = winPt - segStart;
                            double proj = toWin.X * segDir.X + toWin.Y * segDir.Y;
                            // Расстояние от оси сегмента: z-компонента векторного произведения.
                            double perp = Math.Abs(toWin.X * segDir.Y - toWin.Y * segDir.X);

                            bool inSegment = proj >= -winTolerance && proj <= segLenFt + winTolerance;
                            bool nearWall  = exactHost || perp <= perpToleranceFt;

                            if (inSegment && nearWall)
                            {
                                openingsM2      += win.Area;
                                extWindowAreaM2 += win.Area;
                                // Окно наследует то, что за стеной: остекление на лоджию
                                // греется на ΔT до лоджии, а не до наружного воздуха.
                                win.AdjacentCategory = adjacentCategory;
                                win.AdjacentRoomId   = adjacentRoomId;
                                if (winId >= 0) consumedOpenings.Add(winId);
                                Logger.Debug(
                                    $"    Окно {win.Area:F2}м² proj={UnitUtils.ConvertFromInternalUnits(proj, UnitTypeId.Meters):F2}м " +
                                    $"откл={UnitUtils.ConvertFromInternalUnits(perp, UnitTypeId.Meters):F2}м " +
                                    $"({(exactHost ? "по хосту" : "по геометрии")}) ✓");
                            }
                            else if (exactHost)
                            {
                                Logger.Debug(
                                    $"    Окно ПРОПУЩЕНО proj={UnitUtils.ConvertFromInternalUnits(proj, UnitTypeId.Meters):F2}м " +
                                    $"откл={UnitUtils.ConvertFromInternalUnits(perp, UnitTypeId.Meters):F2}м " +
                                    $"(вне 0..{segLengthM:F2}м либо далеко от оси)");
                            }
                        }

                        foreach (var door in roomData.Doors)
                        {
                            if (!door.IsExternal) continue;
                            int doorId = door.Id?.IntegerValue ?? -1;
                            if (doorId >= 0 && consumedOpenings.Contains(doorId)) continue;

                            int doorHostId = door.HostWallId?.IntegerValue ?? -1;
                            bool exactDoorHost = doorHostId >= 0 && segmentHostIds.Contains(doorHostId);

                            XYZ doorPt = null;
                            try
                            {
                                var doorFi = doc.GetElement(door.Id) as Autodesk.Revit.DB.FamilyInstance;
                                doorPt = (doorFi?.Location as Autodesk.Revit.DB.LocationPoint)?.Point;
                            }
                            catch (Exception ex) { Logger.Debug($"Не удалось получить позицию двери: {ex.Message}"); }
                            if (doorPt == null)
                            {
                                // Позиция неизвестна — засчитываем только при точном хосте:
                                // по одному типу стены, без геометрии, дверь можно приписать
                                // не тому сегменту, а площадь двери крупная.
                                if (!exactDoorHost) continue;
                                openingsM2 += door.Area;
                                extDoorAreaM2 += door.Area;
                                door.AdjacentCategory = adjacentCategory;
                                door.AdjacentRoomId   = adjacentRoomId;
                                if (doorId >= 0) consumedOpenings.Add(doorId);
                                continue;
                            }
                            XYZ toD  = doorPt - segStart;
                            double proj = toD.X * segDir.X + toD.Y * segDir.Y;
                            double perpD = Math.Abs(toD.X * segDir.Y - toD.Y * segDir.X);
                            if (proj >= -winTolerance && proj <= segLenFt + winTolerance &&
                                (exactDoorHost || perpD <= perpToleranceFt))
                            {
                                openingsM2    += door.Area;
                                extDoorAreaM2 += door.Area;
                                door.AdjacentCategory = adjacentCategory;
                                door.AdjacentRoomId   = adjacentRoomId;
                                if (doorId >= 0) consumedOpenings.Add(doorId);
                            }
                        }

                        // СП 60.13330 Приложение А / Audytor CO: площадь стены = НЕТТО (вычитаем проёмы).
                        // Каждый элемент (стена, окно, дверь) считается отдельно со своим k и своей площадью.
                        double netM2 = Math.Max(0, grossM2 - openingsM2);
                        extWallNetAreaM2 += netM2;

                        // Площадь, ушедшая в шахты, — мера самой правки: до неё эти
                        // квадратные метры считались по ΔT до наружного воздуха.
                        if (facesShaft) _shaftAreaM2 += netM2;

                        // Взвешенное по площади накопление для расчёта среднего U стен
                        totalWallUA  += wallUValue * netM2;  // сумма U×A
                        totalWallNetM2 += netM2;

                        // ── Сохраняем саму стену, а не только её площадь ──────────────────
                        // Площадь по «тип × за чем стена × откуда U» — сводка после
                        // сбора отвечает на главный вопрос доверия одним взглядом,
                        // без просеивания сотен тысяч строк журнала.
                        string tallyKey = string.Join(" | ", new[]
                        {
                            thermalWall.WallType?.Name ?? wallTypeName,
                            adjacentCategory.HasValue
                                ? RoomCategoryHelper.GetRussianLabel(adjacentCategory.Value)
                                : (facesShaft ? "шахта" : "улица"),
                            wallThermal.RaisedToNormative
                                ? "поднято до нормируемого R (СП 50 табл. 3)"
                                : WallThermalCalculator.DescribeSource(wallThermal.Source),
                            $"U={wallUValue:F2}"
                        });
                        double tallied;
                        _areaByTypeAndSource[tallyKey] =
                            (_areaByTypeAndSource.TryGetValue(tallyKey, out tallied) ? tallied : 0) + netM2;

                        if (wallThermal.RaisedToNormative) _normativeFloorAreaM2 += netM2;

                        // Всё нужное уже посчитано выше: длина сегмента, высота, U, нетто.
                        // Без этой записи room.Walls оставался пустым, и расчёт терял:
                        // ориентацию каждой стены (все помещения выходили северными),
                        // длины узлов для R_пр и состав конструкции для таблиц СП 230.
                        roomData.Walls.Add(new WallInfo
                        {
                            Id          = thermalWall.Id,
                            TypeName    = thermalWall.WallType?.Name ?? wallTypeName,
                            Length      = segLengthM,
                            Height      = roomHeightM,
                            Area        = netM2,
                            UValue      = wallUValue,
                            RValue      = wallUValue > 0 ? 1.0 / wallUValue : 0,
                            IsExternal  = true,
                            ThermalFromModel = wallThermal.IsEntirelyFromModel,
                            ThermalNormative = wallThermal.UsedNormativeLambda,
                            ThermalSource    = WallThermalCalculator.DescribeSource(wallThermal.Source),
                            AdjacentCategory = adjacentCategory,
                            AdjacentRoomId   = adjacentRoomId,
                            // Сторона света — по вектору «наружу», посчитанному пробой,
                            // а НЕ по Wall.Orientation граничной стены. Wall.Orientation
                            // зависит от того, как стену нарисовали и не отражали ли её,
                            // а граничная стена на реальной модели — отделочный слой
                            // («Отделка Штук15»), у которого «наружная сторона» вообще
                            // произвольна. Вектор outward определён по построению:
                            // это та сторона сегмента, с которой не видно это помещение.
                            // От ориентации зависит β = 0…0.10 и ориентация помещения
                            // целиком (по доминирующей стене).
                            Orientation = _orientationCalculator.FromNormal(outward),
                            FloorNumber = roomData.FloorNumber,
                            Location    = wall.Location as LocationCurve
                        });

                        if (thermalWall.Id != wall.Id)
                        {
                            Logger.Debug($"    Теплотехника взята с несущей стены " +
                                         $"«{thermalWall.WallType?.Name}» вместо отделки «{wallTypeName}»");
                        }

                        // Сторона света — единственная величина, которую нельзя проверить
                        // по числам отчёта: она либо совпадает с планом, либо нет.
                        // Пишем ОБА способа и сам вектор, чтобы спор решался журналом,
                        // а не памятью. `out` — вектор «наружу» из пробы (им и считаем),
                        // `wallOri` — Wall.Orientation граничной стены (был до 2026-08-10).
                        XYZ wallNormal = null;
                        try { wallNormal = wall.Orientation; }
                        catch (Exception ex) { Logger.Debug($"Wall.Orientation: {ex.Message}"); }
                        Logger.Debug(
                            $"    Ориентация: out=({outward.X:F2};{outward.Y:F2})→" +
                            $"{_orientationCalculator.FromNormal(outward)}  " +
                            $"wallOri=({wallNormal?.X ?? 0:F2};{wallNormal?.Y ?? 0:F2})→" +
                            $"{_orientationCalculator.FromWall(wall)}");

                        // Конструкция стены для выбора таблиц СП 230 — по самой большой
                        // стене НА УЛИЦУ: она задаёт узлы. Стена к лоджии, шахте или
                        // лестнице конструкцию наружного ограждения не описывает, а
                        // именно она раньше могла выиграть отбор по площади.
                        //
                        // Слои берутся со ВСЕЙ сборки, а не с несущей стены: утеплитель
                        // стоит перед ней отдельным элементом, и без него наружное
                        // утепление классифицировалось бы как голая кладка — с чужими
                        // таблицами Ψ и лишним узлом плиты перекрытия на каждом этаже.
                        if (!adjacentCategory.HasValue && !facesShaft)
                        {
                            hasStreetWall = true;
                            streetSegments.Add(Tuple.Create(loopIndex, segIndex));

                            if (roomData.WallConstruction == null || netM2 > largestWallAreaM2)
                            {
                                var layers = _wallCalculator.GetAssemblyLayers(thermalWall, facadeStack);
                                if (layers.Count > 0)
                                {
                                    var profile = WallConstructionProfile.FromLayers(layers);

                                    // Утеплителя в модели нет, но U мы уже подняли до
                                    // нормируемого — значит нет его В МОДЕЛИ, а не в доме.
                                    // Конструкция обязана сказать то же самое, иначе узлы
                                    // считаются по таблицам голой кладки.
                                    if (wallThermal.RaisedToNormative)
                                    {
                                        var withInsulation = profile.WithNormativeInsulation(wallUValue);
                                        if (!ReferenceEquals(withInsulation, profile))
                                        {
                                            _normativeInsulationCount++;
                                            profile = withInsulation;
                                        }
                                    }

                                    // Непригодный профиль НЕ занимает место годного.
                                    // Раньше он записывался как есть, и помещение
                                    // с ограждением «Отделка Штук15» (15 мм — ни утеплитель,
                                    // ни несущий слой) получало конструкцию Unknown, которую
                                    // уже нечем было заменить: ни одна таблица приложения Г
                                    // по ней не подбиралась, а сводка называла это так же,
                                    // как помещение вообще без наружных стен.
                                    if (profile.IsUsable)
                                    {
                                        roomData.WallConstruction = profile;
                                        largestWallAreaM2 = netM2;

                                        Logger.Debug(
                                            $"    Конструкция для СП 230: {profile.Construction} " +
                                            $"(слоёв {layers.Count}, R_ут={profile.InsulationResistance:F2}, " +
                                            $"λ_осн={profile.BaseConductivity:F3})");
                                    }
                                    else
                                    {
                                        Logger.Debug(
                                            $"    Конструкция для СП 230 не опознана: {profile.Construction} " +
                                            $"по {layers.Count} слоям " +
                                            $"({string.Join(" + ", layers.Select(l => $"{l.Material} {l.ThicknessM * 1000:F0}мм λ={l.Conductivity:F3}"))}) — " +
                                            "ни утеплителя, ни несущего слоя; будет принята преобладающая по объекту");
                                    }
                                }
                            }
                        }
                        else if (netM2 > fallbackAreaM2)
                        {
                            // Ограждение к лоджии, шахте или лестнице. Конструкцию оно
                            // задаёт только тогда, когда уличных стен у помещения нет
                            // ВООБЩЕ, — иначе узлы описывала бы не та стена. Решение
                            // принимается после цикла, здесь только запоминается кандидат.
                            fallbackWall   = thermalWall;
                            fallbackStack  = facadeStack;
                            fallbackAreaM2 = netM2;
                        }

                        Logger.Debug(
                            $"    L={segLengthM:F2}м H={roomHeightM:F2}м брутто={grossM2:F2}м² окна={openingsM2:F2}м² нетто={netM2:F2}м² U={wallUValue:F3}");





                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"  [Ошибка сегмента]: {ex.Message}");
                    }
                }
            }

            // ── Конструкция, когда уличной стены у помещения нет ────────────────
            // Внутриквартирный санузел, гардероб или коридор часто выходит только
            // в вентшахту или на лоджию. Потери через такое ограждение считаются
            // (своя ΔT), а вот узлы у него до 2026-08-20 не считались вовсе:
            // конструкция оставалась Unknown, потому что её задавала ТОЛЬКО стена
            // на улицу. На 76-СУЗДАЛ.23 таких помещений 136 из 585.
            //
            // Уличная стена по-прежнему главнее: она решает, какие узлы существуют
            // и по какой таблице их считать. Запасной путь включается лишь тогда,
            // когда выбирать не из чего.
            if (roomData.WallConstruction == null && !hasStreetWall && fallbackWall != null)
            {
                var layers = _wallCalculator.GetAssemblyLayers(fallbackWall, fallbackStack);
                if (layers.Count > 0)
                {
                    var profile = WallConstructionProfile.FromLayers(layers);
                    if (profile.IsUsable)
                    {
                        // Нормируемое R здесь НЕ достраивается: СП 50 табл. 3
                        // предъявляет требования к ограждению НАРУЖНОГО ВОЗДУХА,
                        // а к шахте и лоджии — нет. Там конструкция действительно
                        // бывает неутеплённой, и это же видно по её U.
                        roomData.WallConstruction =
                            profile.WithOrigin(WallConstructionOrigin.NonStreetEnclosure);
                        _constructionFromNonStreet++;

                        Logger.Debug(
                            $"    Конструкция для СП 230 (уличных стен нет): {profile.Construction} " +
                            $"по ограждению «{fallbackWall.WallType?.Name}» {fallbackAreaM2:F1} м², " +
                            $"R_ут={profile.InsulationResistance:F2}, λ_осн={profile.BaseConductivity:F3}");
                    }
                }
            }

            // Копилка преобладающей конструкции объекта: складываем площадь той
            // стены, которая конструкцию помещения и задала. Из неё берут своё
            // помещения, у которых уличное ограждение есть, а разбирать в нём нечего.
            if (roomData.WallConstruction != null &&
                roomData.WallConstruction.Origin != WallConstructionOrigin.NonStreetEnclosure &&
                largestWallAreaM2 > 0)
            {
                string key = roomData.WallConstruction.Signature;
                Tuple<WallConstructionProfile, double> tally;
                _streetConstructionArea[key] = _streetConstructionArea.TryGetValue(key, out tally)
                    ? Tuple.Create(tally.Item1, tally.Item2 + largestWallAreaM2)
                    : Tuple.Create(roomData.WallConstruction, largestWallAreaM2);
            }
            else if (roomData.WallConstruction == null && hasStreetWall)
            {
                _roomsAwaitingDominantConstruction.Add(roomData.Id);
            }

            // Ориентация помещения — по САМОЙ БОЛЬШОЙ наружной стене, а не по
            // геометрии помещения целиком: β назначается за самую неблагоприятную
            // сторону, и именно крупная стена её определяет. Прежний расчёт
            // возвращал «Север» всем помещениям подряд, из-за чего южные комнаты
            // получали надбавку 0.1 наравне с северными.
            // Сторону света задаёт стена, выходящая НА УЛИЦУ: β по ориентации — про
            // обдув и инсоляцию, и стена на лоджию тут не показательна. Если уличных
            // стен нет вовсе, берём любую — лучше приблизительно, чем «Север» по умолчанию.
            var dominantWall = roomData.Walls
                .Where(w => !string.IsNullOrEmpty(w.Orientation) && !w.AdjacentCategory.HasValue)
                .OrderByDescending(w => w.Area)
                .FirstOrDefault()
                ?? roomData.Walls
                    .Where(w => !string.IsNullOrEmpty(w.Orientation))
                    .OrderByDescending(w => w.Area)
                    .FirstOrDefault();
            if (dominantWall != null)
                roomData.Orientation = dominantWall.Orientation;

            // ── Сторож: окно есть у помещения, но ни к одной стене не привязалось ──
            // Именно этот случай молчал с самого начала. Движок считает потери через
            // окна по room.Windows, а вычитает их площадь из стен ЭТОТ метод. Если
            // привязка не сработала, окно учитывается дважды, и в отчёте это выглядит
            // просто как «окон 0,0 м²» — числом, на которое никто не смотрит.
            var unattached = roomData.Windows
                .Where(w => !w.IsCurtainGlazing)   // витраж — сам сегмент, привязывать не к чему
                .Where(w => (w.Id?.IntegerValue ?? -1) < 0 ||
                            !consumedOpenings.Contains(w.Id.IntegerValue))
                .ToList();
            if (unattached.Count > 0)
            {
                // Лоджии, балконы и лестницы сами снимаются с расчёта, и их
                // непривязанные окна на итог не влияют. Считать их вместе
                // с остальными — значит поднимать тревогу там, где её нет:
                // на 76-СУЗДАЛ.23 из 105 непривязанных 90 приходились именно
                // на такие помещения, и за ними терялись два реальных случая.
                if (ThermalConstants.UnheatedOrCommonCategories.Contains(roomData.Category))
                    _windowsNotAttachedOutOfScope += unattached.Count;
                else
                    _windowsNotAttached += unattached.Count;

                // Куда именно вставлено непривязанное окно — единственное, чего
                // не хватало, чтобы понять причину: совпадения по Id хоста нет,
                // а значит окно сидит в элементе, которого нет среди стен помещения
                // ни как границы, ни как несущей за отделкой.
                var hosts = unattached.Select(w =>
                {
                    if (w.HostWallId == null) return "хост не задан";
                    var host = doc.GetElement(w.HostWallId);
                    return $"{w.HostWallId.IntegerValue} «{(host as Wall)?.WallType?.Name ?? host?.Name ?? host?.GetType().Name ?? "?"}»";
                });

                Logger.Debug(
                    $"[Окна] {roomData.Number} «{roomData.Name}»: {unattached.Count} из " +
                    $"{roomData.Windows.Count} окон не привязаны ни к одной наружной стене " +
                    $"({unattached.Sum(w => w.Area):F2} м²) — их площадь не вычтена из стен. " +
                    $"Хосты: {string.Join("; ", hosts.Distinct())}");
            }

            roomData.WallArea              = Math.Max(0, extWallNetAreaM2);
            roomData.WindowArea            = extWindowAreaM2;
            roomData.DoorArea              = extDoorAreaM2;
            // Наружных стен — число РАЗНЫХ стен, а не граничных сегментов. Сегментов
            // у одного фасада бывает несколько (см. externalWallIds выше), и на счёте
            // сегментов «угловым» становилось помещение с единственной наружной стеной.
            roomData.NumberOfExternalWalls = externalWallIds.Count;
            // Угловое = две и более наружные стены. Считается ЗДЕСЬ и только здесь:
            // раньше IsCorner давала отдельная эвристика (FUNCTION_PARAM по каждому
            // сегменту границы, без разрешения отделочного слоя), и два физически
            // одинаковых помещения — 303 и 1103, оба «Межквартирный коридор» —
            // получали IsCorner=false и IsCorner=true.
            // β = 0.05 за угол зависит от этого флага, так что расхождение было расчётным.
            // Надбавка за угол в CalculationEngine ОДНА: вторая, «Много наружных стен»,
            // проверяла ровно это же условие и удваивала β.
            roomData.IsCorner              = externalWallIds.Count >= 2;

            // Углы для СП 230 — ПОСЛЕ NumberOfExternalWalls: в журнал идёт сравнение
            // с прежним правилом, а оно считалось именно от этого числа. Само β
            // за угловое помещение (IsCorner) к этому разбору отношения не имеет:
            // оно про обдув и инсоляцию, а не про геометрию узла.
            DetectExternalCorners(roomData, revitRoom, boundaries, streetSegments, roomPhase, doc);
            roomData.HasExternalDoor       = extDoorAreaM2 > 0;
            // 1/U_avg = ΣA / Σ(U·A) — даёт корректный суммарный поток через стены.
            if (totalWallUA > 0)
                roomData.AverageInverseUValue = totalWallNetM2 / totalWallUA;

            Logger.Debug(
                $"[BoundaryAnalysis] {roomData.Name}: " +
                $"сегментов={totalSegments} наружных сегментов={extSegmentCount} " +
                $"наружных стен={externalWallIds.Count} " +
                $"стены={extWallNetAreaM2:F2}м² окна={extWindowAreaM2:F2}м²");
        }


        /// <summary>
        /// Наружные углы помещения — выпуклые и вогнутые — по геометрии границы.
        ///
        /// <para><b>Что было.</b> Число углов бралось как «наружных стен минус одна»,
        /// и все они считались выпуклыми. Это не геометрия, а счёт: у комнаты с окнами
        /// на север и на юг стыка между наружными стенами нет вовсе, а угол ей
        /// начислялся; у комнаты в нише фасада углы есть, но вогнутые, и по СП 230
        /// раздел Г.4 они теплопотери ВЫЧИТАЮТ, а начислялись со знаком плюс.
        /// «В запас» это только на прямоугольном доме — на изрезанном фасаде
        /// это завышение, причём тем большее, чем сложнее план.</para>
        ///
        /// <para><b>Правило.</b> Угол — стык двух СОСЕДНИХ по обходу сегментов границы,
        /// оба выходят на улицу и не лежат на одной прямой. Выпуклым или вогнутым его
        /// делает внутренний угол помещения в этой точке: меньше 180° — здание
        /// выступает наружу (выпуклый), больше 180° — помещение обходит нишу (вогнутый).</para>
        ///
        /// <para><b>Как отличить, не зная направления обхода.</b> Revit не гарантирует
        /// ни CW, ни CCW, поэтому знак векторного произведения сам по себе ничего
        /// не значит. Берём биссектрису: сумму единичных векторов, идущих из точки
        /// стыка ВДОЛЬ обоих сегментов. При внутреннем угле меньше 180° она смотрит
        /// ВНУТРЬ помещения, при большем — наружу. Куда именно — спрашиваем у самой
        /// модели тем же <c>GetRoomAtPoint</c>, которым определяется наружность стены.
        /// Направление обхода в ответ не входит.</para>
        /// </summary>
        private static void DetectExternalCorners(
            RoomData roomData, Room revitRoom,
            IList<IList<BoundarySegment>> boundaries,
            HashSet<Tuple<int, int>> streetSegments,
            Phase roomPhase, Document doc)
        {
            if (streetSegments.Count < 2) return;

            // Проба биссектрисы: заметно дальше допусков Revit и заметно ближе
            // половины типовой комнаты. Тот же порядок, что у пробы «то же помещение».
            const double bisectorProbeM = 0.20;
            double probeFt = UnitUtils.ConvertToInternalUnits(bisectorProbeM, UnitTypeId.Meters);

            int convex = 0, concave = 0;

            try
            {
                for (int loopIndex = 0; loopIndex < boundaries.Count; loopIndex++)
                {
                    var loop = boundaries[loopIndex];
                    if (loop.Count < 2) continue;

                    for (int segIndex = 0; segIndex < loop.Count; segIndex++)
                    {
                        int nextIndex = (segIndex + 1) % loop.Count;
                        if (!streetSegments.Contains(Tuple.Create(loopIndex, segIndex)) ||
                            !streetSegments.Contains(Tuple.Create(loopIndex, nextIndex)))
                            continue;

                        var a = loop[segIndex].GetCurve();
                        var b = loop[nextIndex].GetCurve();
                        if (a == null || b == null) continue;

                        // Точка стыка — конец первого сегмента. У замкнутого цикла Revit
                        // это же начало второго; если сегменты не стыкуются (разрыв
                        // границы), угла между ними нет.
                        XYZ corner = a.GetEndPoint(1);
                        if (corner.DistanceTo(b.GetEndPoint(0)) > probeFt) continue;

                        // Векторы ИЗ точки стыка вдоль каждого сегмента.
                        XYZ uA = Flat(a.GetEndPoint(0) - corner);
                        XYZ uB = Flat(b.GetEndPoint(1) - corner);
                        if (uA == null || uB == null) continue;

                        XYZ bisector = uA + uB;

                        // Насколько излом отличается от прямой стены. Длина суммы двух
                        // единичных векторов равна 2·cos(θ/2), где θ — угол между ними:
                        // прямой угол даёт 1,41, отклонение 30° от прямой стены — 0,52,
                        // разрезанный на полосы фасад — около нуля.
                        //
                        // Порог нужен потому, что таблицы Г.27 и Г.28 даны для ПРЯМОГО
                        // угла, другого СП не приводит. Считать углом излом фасада
                        // в 10° значит начислить ему Ψ прямого угла — а на длинном
                        // ломаном фасаде таких изломов десятки. Отклонение меньше 30°
                        // считаем прямой стеной; больше — прямым углом, как в СП.
                        if (bisector.GetLength() < 0.52) continue;

                        bisector = bisector.Normalize();
                        XYZ probe = corner + bisector.Multiply(probeFt);
                        // Пробу поднимаем на ту же высоту, на которой лежит помещение:
                        // GetRoomAtPoint работает по объёму, а не по плану.
                        probe = new XYZ(probe.X, probe.Y, corner.Z + probeFt);

                        Room at = roomPhase != null
                            ? doc.GetRoomAtPoint(probe, roomPhase)
                            : doc.GetRoomAtPoint(probe);

                        if (at != null && at.Id == revitRoom.Id)
                        {
                            // Биссектриса смотрит внутрь помещения — внутренний угол
                            // меньше 180°, здание в этой точке выступает наружу.
                            convex++;
                        }
                        else
                        {
                            // Биссектриса вышла из помещения (пусто или соседнее).
                            // Внутренний угол больше 180° — помещение обходит нишу
                            // фасада: угол вогнутый, и по СП 230 он ВЫЧИТАЕТСЯ.
                            concave++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Углы] {roomData.Name}: разбор границы не удался — {ex.Message}");
                return;
            }

            roomData.ConvexCorners  = convex;
            roomData.ConcaveCorners = concave;

            if (convex + concave > 0)
            {
                Logger.Debug(
                    $"    Углы наружных стен: выпуклых {convex}, вогнутых {concave} " +
                    $"(прежнее правило дало бы {Math.Max(0, roomData.NumberOfExternalWalls - 1)} выпуклых)");
            }
        }

        /// <summary>Вектор в плане, приведённый к единичной длине. null — вырожденный.</summary>
        private static XYZ Flat(XYZ v)
        {
            var flat = new XYZ(v.X, v.Y, 0);
            return flat.GetLength() < 1e-6 ? null : flat.Normalize();
        }

        /// <summary>Обвод цикла границы в плане, м.</summary>
        private static List<Point2D> OutlineOf(IList<BoundarySegment> loop)
        {
            var outline = new List<Point2D>();
            if (loop == null) return outline;

            foreach (var segment in loop)
            {
                var curve = segment?.GetCurve();
                if (curve == null) continue;

                var p = curve.GetEndPoint(0);
                outline.Add(new Point2D(
                    UnitUtils.ConvertFromInternalUnits(p.X, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(p.Y, UnitTypeId.Meters)));
            }
            return outline;
        }

        /// <summary>Добавляет отрезок в контур здания на уровне.</summary>
        private void AddContourSegment(int levelId, Curve curve)
        {
            if (curve == null) return;

            var a = curve.GetEndPoint(0);
            var b = curve.GetEndPoint(1);

            List<Segment2D> list;
            if (!_contourByLevel.TryGetValue(levelId, out list))
            {
                list = new List<Segment2D>();
                _contourByLevel[levelId] = list;
            }

            list.Add(new Segment2D(
                UnitUtils.ConvertFromInternalUnits(a.X, UnitTypeId.Meters),
                UnitUtils.ConvertFromInternalUnits(a.Y, UnitTypeId.Meters),
                UnitUtils.ConvertFromInternalUnits(b.X, UnitTypeId.Meters),
                UnitUtils.ConvertFromInternalUnits(b.Y, UnitTypeId.Meters)));
        }

        /// <summary>
        /// Раскладывает пол и заглублённые стены по зонам СП 50.13330.2024 Г.7.
        ///
        /// <para><b>Пол на грунте — не то же, что первый этаж.</b> При частичном
        /// подвале первый этаж стоит и на грунте, и над подвалом одновременно,
        /// а <see cref="RoomData.IsFirstFloor"/> — свойство НОМЕРА этажа. Признак
        /// определяется геометрией: есть ли под помещением другое помещение.</para>
        ///
        /// <para><b>Заглублённые стены</b> считаются полосами по 2 м от отметки
        /// земли вниз. Отметка земли — низ самого нижнего НАДЗЕМНОГО уровня; если
        /// надземных уровней в модели нет вовсе (собран один подвал), зоны стен
        /// не назначаются: отсчитывать не от чего, а выдумывать отметку нельзя.</para>
        /// </summary>
        private void AssignGroundZones(List<RoomData> rooms)
        {
            if (rooms == null || rooms.Count == 0) return;

            var allContour = _contourByLevel.SelectMany(p => p.Value).ToList();
            if (allContour.Count == 0)
            {
                Logger.Warn("[Грунт] контур здания пуст — зональный метод не применён. " +
                            "Ни один сегмент границы не признан выходящим на наружный воздух.");
                return;
            }

            var aboveGround = rooms.Where(r => !r.IsUnderground).ToList();
            double? groundElevationM = aboveGround.Count > 0
                ? aboveGround.Min(r => r.Elevation)
                : (double?)null;

            int floors = 0, walls = 0;
            double floorAreaM2 = 0, wallAreaM2 = 0;

            foreach (var room in rooms)
            {
                // ── Пол по грунту ────────────────────────────────────────────
                room.FloorOnGround = !HasRoomBelow(room, rooms);

                if (room.FloorOnGround && room.Area > 0)
                {
                    List<Segment2D> contour;
                    if (!_contourByLevel.TryGetValue(room.LevelId, out contour) || contour.Count == 0)
                        contour = allContour;

                    // Эффективная полоса пола НИЖЕ уровня земли: половина средней
                    // высоты стен в грунте (Г.7). У надземного пола её нет.
                    double burialM = groundElevationM.HasValue
                        ? Math.Max(0, groundElevationM.Value - room.Elevation)
                        : 0;

                    var zones = GroundZoneMapper.FloorZones(
                        room.FloorOutline, contour, room.Area, burialM / 2);

                    if (zones.Count > 0)
                    {
                        room.GroundFloorZones = zones;
                        floors++;
                        floorAreaM2 += room.Area;
                    }
                }

                // ── Стены в грунте ───────────────────────────────────────────
                if (!room.IsUnderground || !groundElevationM.HasValue) continue;

                double top = Math.Max(0, groundElevationM.Value - (room.Elevation + room.Height));
                double bottom = Math.Max(0, groundElevationM.Value - room.Elevation);
                if (bottom <= top) continue;

                // Доля стены ниже уровня земли. У подвала верх стены часто
                // выходит из земли — цоколь считается обычным наружным ограждением,
                // и отдать зональному методу всю стену значило бы занизить потери.
                double buriedFraction = room.Height > 0
                    ? Math.Min(1.0, (bottom - top) / room.Height)
                    : 1.0;

                var buried = new List<GroundZoneArea>();
                double uaSum = 0, areaSum = 0;

                foreach (var wall in room.Walls.Where(w => w.IsExternal && !w.AdjacentCategory.HasValue))
                {
                    double length = wall.Length > 0
                        ? wall.Length
                        : (room.Height > 0 ? wall.Area / room.Height : 0);
                    if (length <= 0) continue;

                    wall.BuriedFraction = buriedFraction;
                    buried.AddRange(GroundContact.SplitWallByDepth(length, top, bottom));

                    // R конструкции для слагаемого δ_ут/λ_ут формулы (Г.18) —
                    // средневзвешенное по площади R ТЕЛА стены, без Rsi+Rse:
                    // сопротивления теплоотдаче в зональной методике не участвуют.
                    double bodyR = EnclosureThermal.BodyRFromU(wall.UValue);
                    if (bodyR > 0 && wall.Area > 0)
                    {
                        uaSum += bodyR * wall.Area;
                        areaSum += wall.Area;
                    }
                }

                if (buried.Count > 0)
                {
                    room.GroundWallZones = GroundContact.Merge(buried);
                    room.GroundWallResistance = areaSum > 0 ? uaSum / areaSum : 0;
                    walls++;
                    wallAreaM2 += room.GroundWallZones.Sum(z => z.AreaM2);
                }
            }

            Logger.Info(
                $"[Грунт] зональный метод СП 50.13330.2024 Г.7: пол по грунту у {floors} помещений " +
                $"({floorAreaM2:F0} м²), заглублённые стены у {walls} ({wallAreaM2:F0} м²). " +
                $"Контур здания: {allContour.Count} отрезков на {_contourByLevel.Count} уровнях.");

            if (floors == 0)
            {
                Logger.Warn("[Грунт] пол по грунту не найден НИ У ОДНОГО помещения — " +
                            "проверьте, собраны ли помещения нижнего уровня.");
            }
        }

        /// <summary>
        /// Есть ли под помещением другое помещение — то есть отделён ли его пол
        /// от грунта. Сравниваются габариты в плане: точная проверка пересечения
        /// многоугольников здесь не нужна, перекрытие плана этажами в жилом доме
        /// либо есть, либо его нет.
        /// </summary>
        private static bool HasRoomBelow(RoomData room, List<RoomData> rooms)
        {
            if (room.FloorOutline == null || room.FloorOutline.Count < 3) return false;

            double minX = room.FloorOutline.Min(p => p.X), maxX = room.FloorOutline.Max(p => p.X);
            double minY = room.FloorOutline.Min(p => p.Y), maxY = room.FloorOutline.Max(p => p.Y);

            const double toleranceM = 0.5;

            foreach (var other in rooms)
            {
                if (ReferenceEquals(other, room)) continue;
                if (other.Elevation >= room.Elevation - toleranceM) continue;
                if (other.FloorOutline == null || other.FloorOutline.Count < 3) continue;

                double oMinX = other.FloorOutline.Min(p => p.X), oMaxX = other.FloorOutline.Max(p => p.X);
                double oMinY = other.FloorOutline.Min(p => p.Y), oMaxY = other.FloorOutline.Max(p => p.Y);

                bool overlaps = oMinX < maxX && oMaxX > minX && oMinY < maxY && oMaxY > minY;
                if (overlaps) return true;
            }

            return false;
        }

        /// <summary>
        /// Сводка по ограждениям после сбора: где нашлась фасадная система и
        /// сколько площади посчитано по какому источнику.
        ///
        /// <para><b>Зачем отдельной сводкой.</b> Пока λ материалов в модели
        /// не заполнены, U ограждения решает один вопрос — нашли ли мы перед
        /// несущей стеной утеплитель. Кладка 250 мм без него даёт R ≈ 0,46,
        /// с ним ≈ 3,37: разница в семь раз, и она размазана по всему дому.
        /// Ответ на этот вопрос до сих пор приходилось собирать подсчётом строк
        /// в журнале на сотни тысяч записей — и он был неполным, потому что
        /// сегмент без фасада не писал в журнал вообще ничего.</para>
        ///
        /// <para><b>Как читать.</b> «Фасад не найден» у стены к лоджии, шахте
        /// или лестнице — норма: утеплителя там и нет. То же у стены НА УЛИЦУ —
        /// уже улика: либо фасадная система в модели не нарисована, либо марш
        /// её не поймал. Отправная точка для сравнения — прогон 2026-08-13:
        /// 584 сегмента с найденным фасадом из 2 060.</para>
        /// </summary>
        private void LogEnclosureSummary()
        {
            int probed = _facadeStackFound + _facadeStackEmpty + _facadeStackSkippedShaft;
            if (probed == 0) return;

            Logger.Info(
                $"[Фасад] сегментов с пробой {probed}: фасадная система найдена у {_facadeStackFound}, " +
                $"не найдена у {_facadeStackEmpty}, пропущено как стена в шахту {_facadeStackSkippedShaft}.");

            if (_probeMissByCause.Count > 0)
            {
                Logger.Info("[Промахи] почему фасад не найден — вердикт сегмента × причина:");
                foreach (var pair in _probeMissByCause.OrderByDescending(p => p.Value))
                    Logger.Info($"    {pair.Value,5}  {pair.Key}");
            }

            // Площадь «на улицу» с плохим U — главный остаток. Ограждение жилого
            // дома в Ярославле при U > 1,0 означает, что утеплитель не найден:
            // норматив требует R около 3, то есть U около 0,32.
            double streetBad = 0, streetGood = 0;
            foreach (var pair in _areaByTypeAndSource)
            {
                if (!pair.Key.Contains("| улица |")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(pair.Key, @"U=([\d]+[,\.][\d]+)");
                if (!m.Success) continue;
                double u = double.Parse(m.Groups[1].Value.Replace(',', '.'),
                                        System.Globalization.CultureInfo.InvariantCulture);
                if (u > 1.0) streetBad += pair.Value; else streetGood += pair.Value;
            }
            if (streetBad + streetGood > 0)
            {
                Logger.Info(
                    $"[Ограждения] на улицу: {streetGood:F0} м² с утеплителем (U ≤ 1,0), " +
                    $"{streetBad:F0} м² без него (U > 1,0). Второе число обязано стремиться к нулю: " +
                    "наружная стена жилого дома без утеплителя нормативу не удовлетворяет.");
            }

            if (_normativeInsulationCount > 0)
            {
                Logger.Info(
                    $"[Конструкция] у {_normativeInsulationCount} сегментов утеплителя в модели нет, " +
                    "но U поднят до нормируемого — конструкция принята НАРУЖНЫМ УТЕПЛЕНИЕМ, " +
                    "R утеплителя получен вычитанием основания из тела нормируемой стены. " +
                    "Иначе узлы считались бы по таблицам голой кладки: лишний узел плиты " +
                    "перекрытия (СП 230 Г.3) и оконный откос по чужой таблице.");
            }

            Logger.Info("[Ограждения] площадь по типу, соседу и источнику U:");
            foreach (var pair in _areaByTypeAndSource.OrderByDescending(p => p.Value))
                Logger.Info($"    {pair.Value,8:F1} м²  {pair.Key}");
        }

        /// <summary>Единичный вектор вдоль сегмента границы (XY).</summary>
        private static XYZ segDirOf(Curve curve)
        {
            XYZ d = curve.GetEndPoint(1) - curve.GetEndPoint(0);
            double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            return len > 1e-6 ? new XYZ(d.X / len, d.Y / len, 0) : new XYZ(1, 0, 0);
        }

        /// <summary>
        /// Фасадные конструкции, стоящие перед несущей стеной отдельными элементами,
        /// изнутри наружу.
        ///
        /// <para><b>Зачем.</b> <see cref="ResolveThermalWall"/> находит ОДНУ стену —
        /// ту, чей габарит первым накрыл пробу. На 76-СУЗДАЛ.23 это давало то
        /// «Наруж стена Силикатныйблок250» (без λ → типовое U 0,51), то «Фасад
        /// Утеплитель НГ 140 под штукатурку», то вовсе «Фасад Зазор225» с R = 9,17.
        /// Одно ограждение — а результат зависит от того, что первым попалось.</para>
        ///
        /// <para><b>Где марш останавливается.</b> На вентилируемой прослойке:
        /// по СП 50.13330 слои за ней в сопротивление не входят, прослойка
        /// сообщается с наружным воздухом. Отличить вентилируемую прослойку
        /// от замкнутой по модели нельзя — в обеих просто слой, — поэтому
        /// не учитывается ни она сама, ни то, что за ней. Это оценка В ЗАПАС
        /// и она пишется в журнал: замкнутая прослойка по СП 50 даёт около
        /// 0,15 м²·К/Вт, и их мы теряем.</para>
        /// </summary>
        private List<Wall> ResolveFacadeStack(Wall boundaryWall, Wall thermalWall,
                                              XYZ midPt, XYZ outward, Phase roomPhase, Document doc,
                                              Room roomOfSegment = null, string verdict = null)
        {
            var stack = new List<Wall>();

            try
            {
                // Марш начинается за ВНЕШНЕЙ гранью несущей стены. Слагаемых два
                // только тогда, когда границу помещения образует отдельный
                // отделочный слой, а несущая стена стоит за ним ВТОРЫМ элементом.
                //
                // ⚠ Когда отделки нет, `ResolveThermalWall` возвращает null и
                // выше стоит `?? wall` — то есть несущая стена И ЕСТЬ граничная,
                // один и тот же элемент. Складывать тогда две ширины значит
                // считать толщину одной стены дважды и начинать пробу ЗА фасадом.
                //
                // Цена ошибки измерена на 76-СУЗДАЛ.23 (прогон 2026-08-17):
                // силикатный блок 250 — старт с 0,52 м вместо 0,27, а утеплитель
                // 140 мм лежит на 0,25…0,39; монолит 200 — старт с 0,42 вместо
                // 0,22 при утеплителе на 0,20…0,34. Ни один из пяти шагов пробы
                // в утеплитель уже не попадал. Улика видна прямо в сводке
                // [Ограждения]: у ОДНОГО типа «Наруж стена Силикатныйблок250»
                // на улицу 1 370,6 м² собрались с U = 0,30, а 479,7 м² остались
                // голой кладкой с U = 2,19 — разница ровно в том, есть ли перед
                // блоком «Отделка Штук15».
                // Само правило — в EnclosureThermal: оно нормативно значимо
                // (решает, войдёт ли утеплитель в сопротивление) и обязано
                // проверяться автотестом без Revit.
                bool boundaryIsStructural = boundaryWall != null && thermalWall != null &&
                                            boundaryWall.Id == thermalWall.Id;
                double startM = EnclosureThermal.FacadeProbeStartM(
                    WallWidthM(boundaryWall), WallWidthM(thermalWall), boundaryIsStructural);

                // Протокол пробы. Нужен потому, что пустая сборка молчала, и вопрос
                // «фасада в модели нет или мы его не нашли» решался догадками: правка
                // старта пробы 2026-08-17 сдвинула счётчик с 584 на 586 из 2 029 —
                // то есть гипотеза была неверна, а стоила прогона. Записывается
                // ТОЛЬКО когда сборка пуста: у 586 удачных случаев протокол не нужен.
                var trace = new List<string>();

                // ── 1. Докуда вообще можно смотреть ─────────────────────────────
                // Если впереди помещение, всё что за ним — уже чужое ограждение.
                // Этот вопрос решается дёшево (GetRoomAtPoint, без запросов
                // к телам) и задаёт предел поиска.
                double limitM = startM + FacadeProbeStepsM[FacadeProbeStepsM.Length - 1];

                foreach (double step in FacadeProbeStepsM)
                {
                    double d = startM + step;
                    double dFt = UnitUtils.ConvertToInternalUnits(d, UnitTypeId.Meters);
                    XYZ probe = new XYZ(midPt.X + outward.X * dFt,
                                        midPt.Y + outward.Y * dFt,
                                        midPt.Z + 1.0);

                    Room behind = roomPhase != null
                        ? doc.GetRoomAtPoint(probe, roomPhase)
                        : doc.GetRoomAtPoint(probe);
                    if (behind != null)
                    {
                        limitM = d;
                        // ЧЬЁ это помещение — решающий вопрос, а не какое.
                        //
                        // Если ЧУЖОЕ, то сегмент объявлен наружным напрасно: за ним
                        // отапливаемый объём, и считать его при полной ΔT — ошибка
                        // того же рода, что была с шахтами. Если ЖЕ ЭТО ЖЕ помещение,
                        // проба обогнула угол или нишу, и вывод «фасада здесь нет»
                        // верен.
                        //
                        // Различить это по журналу до сих пор было нельзя: писалось
                        // только имя, а имена в модели повторяются. На прогоне
                        // 2026-08-17 таких случаев 403 из 1 023 промахов — самая
                        // крупная группа, и её природа осталась неизвестной.
                        bool sameRoom = roomOfSegment != null &&
                                        behind.Id.IntegerValue == roomOfSegment.Id.IntegerValue;
                        trace.Add($"{d:F2}м: помещение «{behind.Name}» " +
                                  $"({(sameRoom ? "ТО ЖЕ" : "ЧУЖОЕ")}) — стоп");
                        break;
                    }
                }

                // ── 2. Что стоит на пути — СПЛОШНЫМ запросом, а не выборкой точек ──
                //
                // Пять точек с промежутками 0,09 / 0,13 / 0,20 / 0,25 м не видят
                // слой, целиком попавший МЕЖДУ ними: утеплитель 100 мм, стоящий
                // в промежутке 0,20 м, пропускался полностью и сборка выходила
                // пустой. На прогоне 2026-08-17 «пусто на всех шагах» — самая
                // крупная оставшаяся причина промаха, 283 сегмента из 844.
                //
                // Коридор шириной 40 мм от начала до предела ищет всё, что его
                // пересекает телом, за ОДИН запрос — то есть и дешевле пяти
                // прежних, и без пропусков по построению.
                var ahead = WallsAlongOutward(doc, midPt, outward, startM, limitM);
                if (ahead.Count == 0)
                    trace.Add($"{startM:F2}…{limitM:F2}м: пусто{DescribeNonWallsAhead(doc, midPt, outward, startM, limitM)}");

                foreach (var found in ahead)
                {
                    string who = found.WallType?.Name ?? found.Name;

                    if (found.Id == boundaryWall.Id || found.Id == thermalWall.Id)
                    { trace.Add($"«{who}» — это сама стена"); continue; }

                    if (stack.Any(w => w.Id == found.Id)) continue;

                    if (!IsNotPartition(found))
                    { trace.Add($"«{who}» — ОТБРОШЕНА как перегородка"); continue; }

                    if (IsShaft(found))
                    { trace.Add($"«{who}» — ОТБРОШЕНА как шахта"); continue; }

                    // Прослойка обрывает сборку: по СП 50.13330 ни она, ни слои
                    // за ней в сопротивление не входят. Именно поэтому кандидаты
                    // обязаны идти В ПОРЯДКЕ УДАЛЕНИЯ — иначе «за ней» теряет смысл.
                    if (IsVentilatedLayer(found))
                    {
                        trace.Add($"«{who}» — прослойка, стоп");
                        Logger.Debug(
                            $"    Фасад: «{who}» — прослойка, " +
                            "по СП 50.13330 она и слои за ней не учитываются");
                        break;
                    }

                    stack.Add(found);
                }

                if (stack.Count == 0)
                {
                    // Вердикт сегмента печатается рядом с протоколом намеренно.
                    // «За стеной чужое помещение» само по себе ничего не означает:
                    // у стены к лоджии или к лестнице сосед и ДОЛЖЕН находиться,
                    // это законный случай. Тревожно другое сочетание — сегмент
                    // объявлен УЛИЦЕЙ, а помещение за ним есть. Без вердикта
                    // в той же строке эти два случая неразличимы, и на прогоне
                    // 2026-08-17 390 находок «чужого» пришлось оставить
                    // неистолкованными.
                    // Диагноз в готовом виде. Разбирать 800–1000 протоколов
                    // руками после каждого прогона — это и есть та петля,
                    // из-за которой правки шли по одной гипотезе за запуск
                    // Revit. Счётчик сводится сам и печатается в конце сбора.
                    string cause =
                        trace.Any(t => t.Contains("ЧУЖОЕ"))                  ? "помещение ЧУЖОЕ"    :
                        trace.Any(t => t.Contains("ТО ЖЕ"))                  ? "помещение ТО ЖЕ"    :
                        trace.Any(t => t.Contains("как перегородка"))        ? "отброшено перегородкой" :
                        trace.Any(t => t.Contains("прослойка"))              ? "стоп на прослойке"  :
                        trace.Any(t => t.Contains("это сама стена"))         ? "только сама стена"  :
                        trace.Any(t => t.Contains("пусто"))                  ? "пусто впереди"      :
                                                                              "прочее";
                    string key = $"[{verdict ?? "?"}] {cause}";
                    int seen;
                    _probeMissByCause[key] = (_probeMissByCause.TryGetValue(key, out seen) ? seen : 0) + 1;

                    Logger.Debug(
                        $"[Проба] [{verdict ?? "?"}] «{boundaryWall?.WallType?.Name}» → " +
                        $"«{thermalWall?.WallType?.Name}», " +
                        $"старт {startM:F2}м: {(trace.Count > 0 ? string.Join("; ", trace) : "шагов не было")}");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"ResolveFacadeStack: {ex.Message}");
            }

            return stack;
        }

        /// <summary>Отступы пробы за несущей стеной, м. Толщина фасадных систем — от 50 до 400 мм.</summary>
        private static readonly double[] FacadeProbeStepsM = { 0.03, 0.12, 0.25, 0.45, 0.70 };

        /// <summary>Толщина стены, м; 0 — параметр недоступен.</summary>
        private static double WallWidthM(Wall wall)
        {
            try
            {
                return wall == null ? 0 : UnitUtils.ConvertFromInternalUnits(wall.Width, UnitTypeId.Meters);
            }
            catch (Exception ex)
            {
                Logger.Debug($"WallWidthM: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Воздушная прослойка вентфасада. Признак — имя типа: в модели прослойка
        /// это обычный слой, и никакого свойства «вентилируемая» у неё нет.
        ///
        /// <para>Цена ошибки в одну сторону измерена: «Фасад Зазор225 White Hills25»
        /// даёт R = 9,17 — воздух посчитан как утеплитель по λ ≈ 0,024. Цена ошибки
        /// в ДРУГУЮ сторону измерена там же: подстрока «вент» выбрасывала
        /// «Фасад Утеплитель 90+50 под вентфасад» — настоящий утеплитель, названный
        /// по системе, в которую он входит. 150 срабатываний из 208 за прогон.
        /// Поэтому утеплитель проверяется ПЕРВЫМ и прослойкой не бывает никогда.</para>
        ///
        /// <para>Что прослойку выдаёт в этой модели: «Фасад Зазор85» и «Фасад
        /// навесной вент 85 White Hills 25» имеют ОДИНАКОВОЕ R = 3,57 — это один
        /// и тот же зазор 85 мм под разными именами.</para>
        /// </summary>
        private static bool IsVentilatedLayer(Wall wall)
        {
            return LayerNaming.IsVentilatedFacadeLayer(wall?.WallType?.Name ?? wall?.Name);
        }

        /// <summary>
        /// Стена вентшахты, лифтовой шахты и т.п. В сборку фасада не идёт: проба
        /// может пройти сквозь неё, но это самостоятельная конструкция внутри
        /// здания, а не слой перед несущей стеной.
        /// </summary>
        private static bool IsShaft(Wall wall)
        {
            return LayerNaming.IsShaft(wall?.WallType?.Name ?? wall?.Name);
        }

        /// <summary>Запас за граничной стеной для пробы «что стоит сразу за ней», м.</summary>
        private const double BehindBoundaryMarginM = 0.05;

        /// <summary>
        /// Откуда начинается разведка помещений впереди, м. Ближе стоит общая проба
        /// (2,0 ft ≈ 0,61 м), и её ответ уже известен: помещения там нет, иначе
        /// сегмент разбирался бы по соседу, а не по этому пути.
        /// </summary>
        private const double VoidScanStartM = 0.70;

        /// <summary>
        /// Шаг разведки, м. Крупный намеренно: ищется само наличие помещения впереди,
        /// а помещение — объект метрового размера. Подробный профиль строится
        /// шагом <see cref="EnclosedVoid.StepM"/> и только там, где разведка сработала.
        /// </summary>
        private const double VoidScanStepM = 0.20;

        /// <summary>
        /// Что за наружной гранью сегмента: наружный воздух или шахта — замкнутая
        /// пустота внутри здания. Правило разбора живёт в <see cref="EnclosedVoid"/>
        /// без Revit и под тестами; здесь только марш по модели.
        ///
        /// <para><b>Порядок проверок выбран по цене.</b> Настоящих наружных стен
        /// на порядок больше, чем стен в шахту, поэтому первым идёт самый дешёвый
        /// вопрос: есть ли ВООБЩЕ помещение впереди по ходу пробы. У наружной стены
        /// его нет, и марш заканчивается без единого запроса к геометрии тел.
        /// Дорогой профиль (стены на каждом шаге) строится только тогда, когда
        /// помещение впереди нашлось, то есть мы, скорее всего, внутри здания.</para>
        ///
        /// <para><b>Имя конструкции — отдельный, независимый путь.</b> Модель часто
        /// называет шахту сама («Вентшахта Кирп120 рядовой»), и тогда доказывать
        /// нечего. Этот путь нужен ещё и там, где за шахтой помещения нет вовсе
        /// (шахта к шахте, помещение соседней секции в другой фазе): геометрический
        /// признак там не сработает, а имя работает.</para>
        /// </summary>
        private BeyondWall ResolveBeyondWall(Wall boundaryWall, Room room, XYZ midPt, XYZ outward,
                                             Phase roomPhase, Document doc, out string note)
        {
            note = "";

            try
            {
                // Граница помещения — сама стенка шахты. Спрашивать геометрию не о чем.
                if (IsShaft(boundaryWall))
                {
                    note = $"граница — конструкция шахты «{boundaryWall.WallType?.Name ?? boundaryWall.Name}»";
                    return BeyondWall.Shaft;
                }

                // 1. Разведка: есть ли ВООБЩЕ помещение впереди. Шаг здесь крупный —
                //    помещение метровое, промахнуться по нему нечем, — и начинается
                //    она за общей пробой: ближе 0,6 м ответ уже известен (outRoom).
                //    Цена вопроса не абстрактная: кандидатов на прогоне около 9 400,
                //    и каждый лишний шаг марша — это лишний запрос к модели у всех.
                bool roomAhead = false;
                for (double d = VoidScanStartM; d <= EnclosedVoid.LimitM + 1e-9; d += VoidScanStepM)
                {
                    Room found = RoomAtProbe(midPt, outward, d, roomPhase, doc);
                    if (found == null) continue;

                    // Проба вернулась в то же помещение — направление «наружу» не то,
                    // и разбирать по такому маршу нечего. Оставляем прежний ответ.
                    if (found.Id == room.Id)
                    {
                        note = $"проба вернулась в это же помещение на {d:F2} м";
                        return BeyondWall.OutdoorAir;
                    }

                    roomAhead = true;
                    break;
                }

                if (!roomAhead)
                {
                    // 2. Впереди на 2,6 м ничего — почти всегда улица. Последнее слово
                    //    остаётся за именем конструкции сразу за границей.
                    var behind = WallsAtPoint(
                        doc,
                        ProbePoint(midPt, outward, WallWidthM(boundaryWall) + BehindBoundaryMarginM),
                        boundaryWall.Id);

                    var shaftWall = behind.FirstOrDefault(IsShaft);
                    if (shaftWall != null)
                    {
                        note = $"за границей «{shaftWall.WallType?.Name ?? shaftWall.Name}» — шахта по имени конструкции";
                        return BeyondWall.Shaft;
                    }

                    note = "помещений впереди нет — наружный воздух";
                    return BeyondWall.OutdoorAir;
                }

                // 3. Помещение впереди есть — значит мы, скорее всего, внутри здания.
                //    Только теперь строится подробный профиль: мелким шагом и с
                //    запросом тел конструкций. Марш обрывается на первом помещении:
                //    всё, что дальше, к этому ограждению уже не относится.
                var samples = new List<VoidProbeSample>();
                for (double d = EnclosedVoid.FirstStepM; d <= EnclosedVoid.LimitM + 1e-9; d += EnclosedVoid.StepM)
                {
                    Room here = RoomAtProbe(midPt, outward, d, roomPhase, doc);
                    if (here != null)
                    {
                        if (here.Id == room.Id)
                        {
                            note = $"проба вернулась в это же помещение на {d:F2} м";
                            return BeyondWall.OutdoorAir;
                        }

                        samples.Add(new VoidProbeSample(d, false, true));
                        break;
                    }

                    bool hasWall = WallsAtPoint(doc, ProbePoint(midPt, outward, d),
                                                ElementId.InvalidElementId).Count > 0;
                    samples.Add(new VoidProbeSample(d, hasWall, false));
                }

                var verdict = EnclosedVoid.Classify(samples);
                note = verdict.Note;

                if (verdict.Kind == BeyondWall.OutdoorAir && verdict.WidthM > 0)
                {
                    _voidsRejected++;
                    Logger.Debug($"    Пустота за стеной не признана шахтой: {verdict.Note}");
                }

                return verdict.Kind;
            }
            catch (Exception ex)
            {
                // В запас оставляем ПРЕЖНЕЕ поведение: непонятое ограждение остаётся
                // наружным. Ошибка пробы не должна тихо убирать теплопотери.
                Logger.Debug($"ResolveBeyondWall: {ex.Message}");
                note = "проба не удалась — принят наружный воздух";
                return BeyondWall.OutdoorAir;
            }
        }

        /// <summary>Точка на расстоянии <paramref name="distanceM"/> наружу от середины сегмента.</summary>
        private static XYZ ProbePoint(XYZ midPt, XYZ outward, double distanceM)
        {
            double dFt = UnitUtils.ConvertToInternalUnits(distanceM, UnitTypeId.Meters);
            return new XYZ(midPt.X + outward.X * dFt,
                           midPt.Y + outward.Y * dFt,
                           midPt.Z + 1.0);
        }

        private static Room RoomAtProbe(XYZ midPt, XYZ outward, double distanceM, Phase phase, Document doc)
        {
            XYZ probe = ProbePoint(midPt, outward, distanceM);
            return phase != null ? doc.GetRoomAtPoint(probe, phase) : doc.GetRoomAtPoint(probe);
        }

        /// <summary>Чем закончилась проба наружу от грани колонны.</summary>
        private enum ColumnFaceVerdict
        {
            /// <summary>Грань выходит наружу — возможно, под фасадным пирогом.</summary>
            Exterior,

            /// <summary>За гранью помещение (в том числе то же самое).</summary>
            IntoRoom,

            /// <summary>Грань упирается в конструкцию, за которой наружу не выйти.</summary>
            Buried
        }

        /// <summary>Запас за габаритом колонны, чтобы выйти из её тела, м.</summary>
        private const double ColumnProbeMarginM = 0.05;

        /// <summary>Шаг марша наружу от грани колонны, м.</summary>
        private const double ColumnProbeStepM = 0.10;

        /// <summary>
        /// Насколько далеко за колонной ещё ищется улица, м. Это толщина фасадной
        /// системы «в запас»: пирог толще 60 см — уже не облицовка, а конструкция,
        /// в которую грань утоплена.
        /// </summary>
        private const double ColumnCoverMaxM = 0.60;

        /// <summary>
        /// Куда смотрит грань колонны: наружу, в помещение или в соседнюю конструкцию.
        ///
        /// <para><b>Почему нельзя пользоваться общей пробой.</b> Общая проба уходит
        /// на 60 см от границы и рассчитана на стену: «перелетает стену любой
        /// толщины». Колонна поперёк — 1,0…1,5 м, и проба остаётся ВНУТРИ её тела:
        /// <c>GetRoomAtPoint</c> возвращает null, что читается как «за гранью улица».
        /// Из-за этого колонна, стоящая посреди помещения, получала наружными все
        /// четыре грани. Улика с 76-СУЗДАЛ.23 (прогон 2026-08-12): «Колонна Бетон
        /// 1000х250» с гранями [0,25; 1,00; 0,25; 1,00] — замкнутый обход периметра,
        /// то есть колонна внутри офиса, — 8,60 м² «наружных» ограждений из воздуха.</para>
        ///
        /// <para>Поэтому марш начинается ЗА габаритом самого элемента и идёт шагами,
        /// пока не найдётся помещение (грань внутренняя), не кончатся конструкции
        /// (грань наружная, а пройденное — её покрытие) либо не исчерпается запас
        /// на фасадный пирог (грань утоплена в соседнюю конструкцию).</para>
        /// </summary>
        private ColumnFaceVerdict ProbeColumnFace(
            Element column, XYZ midPt, XYZ outward, Phase roomPhase, Document doc,
            out Room foundRoom, out List<Wall> cover)
        {
            foundRoom = null;
            cover = new List<Wall>();

            double extentM = ColumnExtentAlong(column, outward);
            double startM  = extentM + ColumnProbeMarginM;
            double limitM  = extentM + ColumnCoverMaxM;

            try
            {
                for (double d = startM; d <= limitM + 1e-9; d += ColumnProbeStepM)
                {
                    double dFt = UnitUtils.ConvertToInternalUnits(d, UnitTypeId.Meters);
                    XYZ probe = new XYZ(midPt.X + outward.X * dFt,
                                        midPt.Y + outward.Y * dFt,
                                        midPt.Z + 1.0);

                    Room room = roomPhase != null
                        ? doc.GetRoomAtPoint(probe, roomPhase)
                        : doc.GetRoomAtPoint(probe);

                    if (room != null)
                    {
                        foundRoom = room;
                        return ColumnFaceVerdict.IntoRoom;
                    }

                    var solids = WallsAtPoint(doc, probe, column.Id);

                    // Ни помещения, ни конструкции — это улица. Всё, что прошли
                    // до неё, и есть покрытие колонны.
                    if (solids.Count == 0) return ColumnFaceVerdict.Exterior;

                    foreach (var solid in solids)
                        if (!cover.Any(w => w.Id == solid.Id)) cover.Add(solid);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"ProbeColumnFace: проба не удалась ({column?.Id}): {ex.Message}");
                return ColumnFaceVerdict.Exterior; // в запас: лучше посчитать, чем потерять
            }

            return ColumnFaceVerdict.Buried;
        }

        /// <summary>Габарит элемента вдоль направления, м. 0 — габарит недоступен.</summary>
        private static double ColumnExtentAlong(Element element, XYZ direction)
        {
            try
            {
                var bb = element?.get_BoundingBox(null);
                if (bb == null || direction == null) return 0;

                double dx = bb.Max.X - bb.Min.X;
                double dy = bb.Max.Y - bb.Min.Y;
                return UnitUtils.ConvertFromInternalUnits(
                    Math.Abs(direction.X) * dx + Math.Abs(direction.Y) * dy,
                    UnitTypeId.Meters);
            }
            catch (Exception ex)
            {
                Logger.Debug($"ColumnExtentAlong: габарит недоступен: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// ВСЕ стены, чьё ТЕЛО накрывает точку.
        ///
        /// <para><b>Почему не хватает габаритного параллелепипеда.</b> Габарит стены —
        /// коробка вокруг всей её длины, поэтому точку рядом с фасадом накрывают
        /// и перпендикулярные стены, и отделка соседнего помещения. Пока метод
        /// возвращал ОДИН элемент (<c>FirstOrDefault</c>), выигрывал произвольный
        /// из них, а настоящий слой за ним не рассматривался вовсе: на 76-СУЗДАЛ.23
        /// (прогон 2026-08-12 14:34) сборка стены не собралась НИ РАЗУ, хотя
        /// «Фасад ГИх2 Пеноплэкс100 Мембрана» с R = 3,03 стоит в модели.</para>
        ///
        /// <para>Поэтому габарит здесь — только дешёвый предварительный отбор,
        /// а решает точная проверка тела элемента: <c>ElementIntersectsSolidFilter</c>
        /// по кубику вокруг пробной точки. Она применяется к нескольким кандидатам,
        /// а не ко всей модели, поэтому цена приемлема.</para>
        /// </summary>
        private static List<Wall> WallsAtPoint(Document doc, XYZ point, ElementId exclude)
        {
            var result = new List<Wall>();

            try
            {
                double epsFt = UnitUtils.ConvertToInternalUnits(ProbeCubeHalfSizeM, UnitTypeId.Meters);
                var outline = new Outline(
                    new XYZ(point.X - epsFt, point.Y - epsFt, point.Z - epsFt),
                    new XYZ(point.X + epsFt, point.Y + epsFt, point.Z + epsFt));

                var candidates = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .Where(e => e.Id != exclude)
                    .Select(e => e.Id)
                    .ToList();

                if (candidates.Count == 0) return result;

                Solid probe = MakeProbeCube(point, epsFt);
                if (probe == null)
                {
                    // Кубик не построился — остаёмся на габаритах, но это заметно в журнале.
                    Logger.Debug("WallsAtPoint: проба по телу недоступна, отбор только по габаритам");
                    return candidates.Select(id => doc.GetElement(id)).OfType<Wall>().ToList();
                }

                return new FilteredElementCollector(doc, candidates)
                    .WherePasses(new ElementIntersectsSolidFilter(probe))
                    .OfType<Wall>()
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.Debug($"WallsAtPoint: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// ВСЕ стены, чьё тело пересекает КОРИДОР от <paramref name="fromM"/>
        /// до <paramref name="toM"/> по направлению <paramref name="outward"/>,
        /// в порядке удаления от начала.
        ///
        /// <para><b>Почему коридор, а не точки.</b> Проба пятью точками с шагами
        /// 0,03 / 0,12 / 0,25 / 0,45 / 0,70 м оставляет между ними промежутки
        /// 0,09…0,25 м. Слой, целиком попавший в промежуток, не виден вообще —
        /// а типовой фасадный утеплитель как раз 50…140 мм. На 76-СУЗДАЛ.23
        /// (прогон 2026-08-17) «пусто на всех шагах» было самой крупной
        /// оставшейся причиной промаха: 283 сегмента из 844.</para>
        ///
        /// <para>Сплошной запрос вдобавок ДЕШЕВЛЕ: один отбор по габаритам
        /// и одна проверка тел вместо пяти.</para>
        ///
        /// <para><b>Порядок обязателен.</b> Сборка обрывается на вентилируемой
        /// прослойке, и «слои за ней» имеет смысл только если кандидаты идут
        /// по удалению. Расстояние берётся проекцией габарита элемента на
        /// направление пробы: у слоя фасада габарит поперёк направления плотный,
        /// потому что слой параллелен стене.</para>
        /// </summary>
        private static List<Wall> WallsAlongOutward(Document doc, XYZ midPt, XYZ outward,
                                                    double fromM, double toM)
        {
            var result = new List<Wall>();
            try
            {
                if (toM <= fromM) return result;

                double epsFt  = UnitUtils.ConvertToInternalUnits(ProbeCubeHalfSizeM, UnitTypeId.Meters);
                double fromFt = UnitUtils.ConvertToInternalUnits(fromM, UnitTypeId.Meters);
                double toFt   = UnitUtils.ConvertToInternalUnits(toM,   UnitTypeId.Meters);

                XYZ a = new XYZ(midPt.X + outward.X * fromFt, midPt.Y + outward.Y * fromFt, midPt.Z + 1.0);
                XYZ b = new XYZ(midPt.X + outward.X * toFt,   midPt.Y + outward.Y * toFt,   midPt.Z + 1.0);

                var outline = new Outline(
                    new XYZ(Math.Min(a.X, b.X) - epsFt, Math.Min(a.Y, b.Y) - epsFt, a.Z - epsFt),
                    new XYZ(Math.Max(a.X, b.X) + epsFt, Math.Max(a.Y, b.Y) + epsFt, a.Z + epsFt));

                var candidates = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .Select(e => e.Id)
                    .ToList();

                if (candidates.Count == 0) return result;

                Solid corridor = MakeCorridor(a, b, outward, epsFt);
                var walls = corridor != null
                    ? new FilteredElementCollector(doc, candidates)
                        .WherePasses(new ElementIntersectsSolidFilter(corridor))
                        .OfType<Wall>().ToList()
                    : candidates.Select(id => doc.GetElement(id)).OfType<Wall>().ToList();

                if (corridor == null)
                    Logger.Debug("WallsAlongOutward: коридор не построился, отбор только по габаритам");

                return walls
                    .OrderBy(w => DistanceAlong(w, midPt, outward))
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.Debug($"WallsAlongOutward: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Что стоит впереди, КРОМЕ стен — последняя альтернатива объяснению «пусто».
        ///
        /// <para>Поиск фасадной системы смотрит только категорию «Стены». Если
        /// фасад смоделирован иначе — частями (<c>Parts</c>), обобщённой моделью,
        /// перекрытием-обёрткой или лежит в СВЯЗАННОМ файле, — проба увидит
        /// пустоту, и вывод «утеплителя в модели нет» будет неверным.</para>
        ///
        /// <para>Проверка нужна ровно один раз и только там, где стен не нашлось:
        /// на 76-СУЗДАЛ.23 это 282 сегмента из 820, крупнейшая оставшаяся группа.
        /// Различить «фасада нет» и «фасад не той категории» иначе нельзя,
        /// а вывод из этого противоположный: в первом случае R задаёт инженер
        /// по разделу АР, во втором чинится проба.</para>
        /// </summary>
        private static string DescribeNonWallsAhead(Document doc, XYZ midPt, XYZ outward,
                                                    double fromM, double toM)
        {
            try
            {
                double epsFt  = UnitUtils.ConvertToInternalUnits(ProbeCubeHalfSizeM, UnitTypeId.Meters);
                double fromFt = UnitUtils.ConvertToInternalUnits(fromM, UnitTypeId.Meters);
                double toFt   = UnitUtils.ConvertToInternalUnits(toM,   UnitTypeId.Meters);

                XYZ a = new XYZ(midPt.X + outward.X * fromFt, midPt.Y + outward.Y * fromFt, midPt.Z + 1.0);
                XYZ b = new XYZ(midPt.X + outward.X * toFt,   midPt.Y + outward.Y * toFt,   midPt.Z + 1.0);

                var outline = new Outline(
                    new XYZ(Math.Min(a.X, b.X) - epsFt, Math.Min(a.Y, b.Y) - epsFt, a.Z - epsFt),
                    new XYZ(Math.Max(a.X, b.X) + epsFt, Math.Max(a.Y, b.Y) + epsFt, a.Z + epsFt));

                var names = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .Where(e => e.Category != null &&
                                e.Category.Id.IntegerValue != (int)BuiltInCategory.OST_Walls &&
                                e.Category.Id.IntegerValue != (int)BuiltInCategory.OST_Rooms)
                    .Select(e => e.Category.Name)
                    .Distinct()
                    .Take(6)
                    .ToList();

                return names.Count == 0 ? " (и ничего иной категории)"
                                        : " (НЕ стены: " + string.Join(", ", names) + ")";
            }
            catch (Exception ex)
            {
                Logger.Debug($"DescribeNonWallsAhead: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// Ближняя грань габарита элемента вдоль направления пробы, фут.
        /// Нужна только для ПОРЯДКА слоёв, поэтому точности габарита достаточно.
        /// </summary>
        private static double DistanceAlong(Wall wall, XYZ midPt, XYZ outward)
        {
            try
            {
                var bb = wall?.get_BoundingBox(null);
                if (bb == null) return double.MaxValue;

                double best = double.MaxValue;
                foreach (double x in new[] { bb.Min.X, bb.Max.X })
                foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                {
                    double d = (x - midPt.X) * outward.X + (y - midPt.Y) * outward.Y;
                    if (d < best) best = d;
                }
                return best;
            }
            catch (Exception ex)
            {
                Logger.Debug($"DistanceAlong: {ex.Message}");
                return double.MaxValue;
            }
        }

        /// <summary>Тонкий горизонтальный коридор между двумя точками — тело для проверки попадания.</summary>
        private static Solid MakeCorridor(XYZ a, XYZ b, XYZ outward, double halfFt)
        {
            try
            {
                XYZ side = new XYZ(-outward.Y, outward.X, 0);
                double len = Math.Sqrt(side.X * side.X + side.Y * side.Y);
                if (len < 1e-9) return null;
                side = new XYZ(side.X / len * halfFt, side.Y / len * halfFt, 0);

                double z = a.Z - halfFt;
                var p0 = new XYZ(a.X - side.X, a.Y - side.Y, z);
                var p1 = new XYZ(b.X - side.X, b.Y - side.Y, z);
                var p2 = new XYZ(b.X + side.X, b.Y + side.Y, z);
                var p3 = new XYZ(a.X + side.X, a.Y + side.Y, z);

                var loop = CurveLoop.Create(new List<Curve>
                {
                    Line.CreateBound(p0, p1),
                    Line.CreateBound(p1, p2),
                    Line.CreateBound(p2, p3),
                    Line.CreateBound(p3, p0)
                });

                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, XYZ.BasisZ, 2 * halfFt);
            }
            catch (Exception ex)
            {
                Logger.Debug($"MakeCorridor: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Докуда искать несущую стену за отделочным слоем, м. Фасадные системы
        /// доходят до 0,4 м, отделка бывает на подсистеме с зазором — 0,80 м
        /// с запасом покрывает и то и другое, а дальше начинается чужое.
        /// </summary>
        private const double ThermalWallSearchM = 0.80;

        /// <summary>Полуразмер пробного кубика, м. Мельче толщины любого слоя.</summary>
        private const double ProbeCubeHalfSizeM = 0.02;

        /// <summary>Кубик вокруг точки — тело для точной проверки попадания.</summary>
        private static Solid MakeProbeCube(XYZ center, double halfSizeFt)
        {
            try
            {
                double h = halfSizeFt;
                var a = new XYZ(center.X - h, center.Y - h, center.Z - h);
                var b = new XYZ(center.X + h, center.Y - h, center.Z - h);
                var c = new XYZ(center.X + h, center.Y + h, center.Z - h);
                var d = new XYZ(center.X - h, center.Y + h, center.Z - h);

                var loop = CurveLoop.Create(new List<Curve>
                {
                    Line.CreateBound(a, b),
                    Line.CreateBound(b, c),
                    Line.CreateBound(c, d),
                    Line.CreateBound(d, a)
                });

                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, XYZ.BasisZ, 2 * h);
            }
            catch (Exception ex)
            {
                Logger.Debug($"MakeProbeCube: {ex.Message}");
                return null;
            }
        }

        /// <summary>Перечень найденных перед колонной конструкций — для журнала.</summary>
        private static string DescribeCover(List<Wall> cover)
        {
            if (cover == null || cover.Count == 0) return "конструкцию без имени";
            return string.Join(", ", cover.Select(w => $"«{w.WallType?.Name ?? w.Name}»"));
        }

        /// <summary>Колонна — несущая либо архитектурная.</summary>
        private static bool IsColumn(Element element)
        {
            int? category = element?.Category?.Id?.IntegerValue;
            return category == (int)BuiltInCategory.OST_StructuralColumns ||
                   category == (int)BuiltInCategory.OST_Columns;
        }

        /// <summary>
        /// U колонны, Вт/(м²·К), и её толщина В НАПРАВЛЕНИИ ТЕПЛОВОГО ПОТОКА.
        ///
        /// У колонны нет <c>CompoundStructure</c> — это монолит одного материала,
        /// поэтому её собственное R = d/λ, где d берётся из габарита элемента поперёк
        /// сегмента границы: колонна 1000×250 в фасаде повёрнута длинной стороной
        /// вдоль стены, и через неё «работает» именно 250 мм.
        ///
        /// λ читается из материала колонны тем же <c>WallThermalCalculator</c>,
        /// что и слои стен. Не прочиталась — берётся железобетон по СП 50.13330
        /// Приложение Т: подставлять сюда типовое U стены (0,51) нельзя, колонна
        /// заведомо холоднее, и ошибка пошла бы в занижение.
        ///
        /// <para><b>Покрытие.</b> К собственному R прибавляется R конструкций,
        /// найденных перед колонной пробой <see cref="ProbeColumnFace"/>. Колонна
        /// бывает голой — тогда это сильнейший мостик холода, — а бывает закрытой
        /// снаружи тем же фасадным пирогом, что и стена; какой случай на объекте,
        /// решает модель, а не настройка. Сама сборка — в <see cref="EnclosureThermal"/>,
        /// она проверяется тестами без Revit.</para>
        /// </summary>
        private double ResolveColumnU(Element column, XYZ segDir, List<Wall> cover,
                                      out double thicknessM, out string coverNote)
        {
            thicknessM = 0;
            double lambda = 0;

            // Нормаль к сегменту в плане: тепловой поток идёт поперёк грани.
            XYZ acrossSegment = segDir != null
                ? new XYZ(segDir.Y, -segDir.X, 0)
                : null;
            thicknessM = ColumnExtentAlong(column, acrossSegment);

            // Габарит колонны бывает и общим на всю высоту, и повёрнутым; за пределами
            // разумного диапазона доверять ему нельзя.
            if (thicknessM < 0.1 || thicknessM > 1.5)
                thicknessM = ThermalConstants.ColumnThicknessDefaultM;

            try
            {
                var symbol = (column as FamilyInstance)?.Symbol;
                var materialParam = column.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)
                                 ?? symbol?.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
                var materialId = materialParam?.AsElementId();
                if (materialId != null && materialId != ElementId.InvalidElementId)
                    lambda = _wallCalculator.ReadConductivity(_document.GetElement(materialId) as Material);
            }
            catch (Exception ex) { Logger.Debug($"ResolveColumnU: материал не прочитан: {ex.Message}"); }

            if (lambda <= 0.01)
                lambda = ThermalConstants.ReinforcedConcreteConductivity;

            // ── В зачёт идёт ТОЛЬКО ПЕРВАЯ конструкция перед колонной ──────────
            //
            // Проба возвращает всё, что прошла по дороге на улицу, но складывать
            // это целиком нельзя. Прогон 76-СУЗДАЛ.23 13:10: перед колонной стоят
            // «Фасад ГИх2 Пеноплэкс100 Мембрана», «Фасад Зазор225 White Hills25»
            // и «Фасад навесной вент 85 White Hills 25» — то есть утеплитель,
            // ВЕНТИЛИРУЕМЫЙ ЗАЗОР и облицовка. В сумме это давало +R = 11,86
            // и U = 0,064: колонна выходила в восемь раз теплее стены рядом.
            //
            // По СП 50.13330 слои за вентилируемой прослойкой в сопротивление
            // НЕ входят — прослойка сообщается с наружным воздухом. Отличить
            // вентилируемый зазор от невентилируемого по модели нельзя (у обоих
            // это просто слой), поэтому берётся первая конструкция — она стоит
            // на колонне вплотную и заведомо не за прослойкой. Для невентилируемой
            // трёхслойной стены это оценка В ЗАПАС: облицовка не учтена.
            var coverR = new List<double>();
            var coverUsed = new List<Wall>();
            var coverIgnored = new List<Wall>();

            if (cover != null)
            {
                foreach (var wall in cover)
                {
                    if (coverUsed.Count > 0) { coverIgnored.Add(wall); continue; }

                    double bodyR = EnclosureThermal.BodyRFromU(_wallCalculator.GetUValue(wall));
                    if (bodyR <= 0) continue;

                    coverR.Add(bodyR);
                    coverUsed.Add(wall);
                }
            }

            coverNote = coverR.Count == 0
                ? "покрытия нет (голый бетон наружу)"
                : $"покрытие {DescribeCover(coverUsed)} +R={coverR.Sum():F2}" +
                  (coverIgnored.Count > 0
                      ? $" (за ним не учтено: {DescribeCover(coverIgnored)})"
                      : "");

            return EnclosureThermal.ColumnUValue(thicknessM, lambda, coverR);
        }

        /// <summary>
        /// Навесная стена (витраж). Проверяется СНАЧАЛА по <c>WallType.Kind</c> —
        /// это свойство самого Revit и не зависит от того, как тип назвали, — и лишь
        /// затем по имени: в российских проектах витраж иногда моделируют обычной
        /// стеной с соответствующим названием.
        /// </summary>
        private static bool IsCurtainWall(Wall wall)
        {
            if (wall == null) return false;
            try
            {
                if (wall.WallType?.Kind == WallKind.Curtain) return true;
            }
            catch (Exception ex) { Logger.Debug($"IsCurtainWall: Kind недоступен: {ex.Message}"); }

            string name = (wall.WallType?.Name ?? wall.Name ?? "").ToLowerInvariant();
            return name.Contains("витраж") || name.Contains("навесная стена");
        }

        /// <summary>
        /// U витража, Вт/(м²·К). Реальные данные типа (слои, аналитическое
        /// сопротивление, пользовательский параметр) имеют приоритет; если их нет —
        /// нормативная оценка остекления, а НЕ типовое U стены.
        ///
        /// Разница принципиальная: у навесной стены нет <c>CompoundStructure</c>,
        /// поэтому <see cref="WallThermalCalculator"/> доходит до эмпирики и отдаёт
        /// <c>WallUDefault</c> = 0,51 — то есть считает стекло кирпичом в полметра.
        /// </summary>
        private double ResolveCurtainWallU(Wall wall)
        {
            var thermal = _wallCalculator.Calculate(wall);
            if (thermal.Source != WallThermalSource.NormativeByMaterial &&
                thermal.Source != WallThermalSource.Default &&
                thermal.UValue > 0)
            {
                return thermal.UValue;
            }

            Logger.Debug(
                $"[Витраж] {wall.WallType?.Name}: собственных теплотехнических данных нет " +
                $"({thermal.Source}) — принято нормативное U={ThermalConstants.CurtainWallUDefault} " +
                "для стоечно-ригельного остекления");
            return ThermalConstants.CurtainWallUDefault;
        }

        private void DetermineRoomProperties(RoomData room)
        {
            // CalculateRoomAreasFromBoundary уже посчитала AverageInverseUValue по реальным
            // сегментам границ. Если по какой-то причине там не сработало (нет границ),
            // считаем здесь по коллекции room.Walls — той же формулой 1/U_avg = ΣA/Σ(U·A),
            // чтобы оба пути давали согласованный результат.
            if (room.AverageInverseUValue <= 0)
            {
                double totalUA   = 0;
                double totalArea = 0;
                foreach (var wall in room.Walls.Where(w => w.IsExternal))
                {
                    totalUA   += wall.UValue * wall.Area;
                    totalArea += wall.Area;
                }
                if (totalUA > 0)
                    room.AverageInverseUValue = totalArea / totalUA;
            }

            if (room.AverageInverseUValue > 0)
                room.HeatLossCoefficient = 1.0 / room.AverageInverseUValue;

            // Объём пересчитывается по ТОЙ ЖЕ высоте, что и площадь стен —
            // её задаёт ApplyEffectiveHeight до сбора площадей.
            room.Volume = room.Area * room.Height;
        }

        // ─────────────────────────────────────────────────────────
        //  ОПРЕДЕЛЕНИЕ ТИПА, ОРИЕНТАЦИИ, УГЛОВОСТИ
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Определяет категорию и заполняет <c>Type</c>+<c>Category</c> в RoomData.
        /// Категория — единый источник правды для нормативных коэффициентов.
        /// </summary>
        private void DetermineRoomType(Room room, RoomData roomData)
        {
            string rawName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            var category = RoomCategoryHelper.Detect(rawName);

            // Нераспознанное помещение остаётся Other и НЕ подменяется жилой комнатой.
            // Прежняя подмена была молчаливой: «Колясочная» получала полное жилое
            // обращение — жилую норму притока, надбавку ГОСТ 30494 по температуре и
            // освобождение от штрафного β за угол, — и нигде об этом не сообщалось.
            // Угадывать по площади по-прежнему нельзя (так получались тамбур→«Спальня»),
            // поэтому такие помещения просто помечаются и пересчитываются в MainWindow.
            if (category == RoomCategory.Other)
            {
                Logger.Debug($"[Категория] {room.Number} «{rawName}»: не распознано, " +
                             "считается как неопределённое нежилое");
            }

            roomData.Category = category;
            roomData.Type = RoomCategoryHelper.GetRussianLabel(category);
        }

        /// <summary>Толщина, ниже которой стена считается отделочным слоем, м.</summary>
        private const double FinishLayerMaxThicknessM = 0.05;

        /// <summary>
        /// Внутренняя перегородка — не наружная стена, и брать с неё теплотехнику нельзя.
        /// Проба наружу от отделочного слоя иногда задевает перегородку соседнего
        /// помещения: на реальной модели так набралось 990 стен из 2501.
        /// </summary>
        private static bool IsNotPartition(Wall wall)
        {
            string name = (wall.WallType?.Name ?? "").ToLowerInvariant();

            // Утеплитель и фасадный слой перегородкой не бывают — и проверяются
            // ПЕРВЫМИ, как везде в LayerNaming. Без этого отбор съедал их молча
            // по функции стены `Interior`: фасадные слои в моделях рисуют именно
            // так, они не несущие и не «наружная стена» в понимании Revit.
            // На 76-СУЗДАЛ.23 (прогон 2026-08-17) так терялись 109 слоёв за
            // прогон — «Фасад ГИх2 Пеноплэкс100 Мембрана», «Отделка
            // Утеплитель100 Штук10», «Фасад Ут140 Хризлист20» и другие.
            if (LayerNaming.IsFacadeOrInsulationLayer(name)) return true;

            if (name.Contains("перегородка") || name.Contains("внутрен")) return false;

            try
            {
                var function = wall.WallType?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
                if (function != null && function.AsInteger() == (int)WallFunction.Interior)
                    return false;
            }
            catch (Exception ex)
            {
                Logger.Debug($"IsNotPartition: функция стены недоступна: {ex.Message}");
            }

            return true;
        }

        /// <summary>Признак того, что стена по имени или функции наружная — для выбора лучшей.</summary>
        private static int LooksExternal(Wall wall)
        {
            string name = (wall.WallType?.Name ?? "").ToLowerInvariant();
            if (name.Contains("наруж") || name.Contains("фасад")) return 2;
            if (name.Contains("монолит")) return 1;

            try
            {
                var function = wall.WallType?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
                if (function != null && function.AsInteger() == (int)WallFunction.Exterior) return 2;
            }
            catch (Exception ex)
            {
                Logger.Debug($"LooksExternal: функция стены недоступна: {ex.Message}");
            }

            return 0;
        }

        /// <summary>
        /// Возвращает стену, с которой надо брать теплотехнику. Если границу помещения
        /// образует отделочный слой (тонкая стена «Отделка Штук15», «Шпаклёвка» и т.п.),
        /// ищет за ним настоящую наружную стену — она стоит отдельным элементом
        /// и именно в ней лежат утеплитель, кладка и фасад.
        ///
        /// Возвращает null, если искать не нужно или ничего подходящего не нашлось;
        /// тогда вызывающий остаётся на исходной стене.
        /// </summary>
        private Wall ResolveThermalWall(Wall boundaryWall, XYZ midPt, XYZ outward, Document doc)
        {
            try
            {
                double widthM = UnitUtils.ConvertFromInternalUnits(boundaryWall.Width, UnitTypeId.Meters);
                string name = (boundaryWall.WallType?.Name ?? "").ToLowerInvariant();

                bool looksLikeFinish =
                    widthM > 0 && widthM <= FinishLayerMaxThicknessM ||
                    name.Contains("отделка") || name.Contains("шпакл");

                if (!looksLikeFinish) return null;

                // Сплошной запрос по коридору вместо четырёх точек.
                //
                // Здесь стоял тот же дефект, что в пробе фасада: отступы
                // 0,08 / 0,20 / 0,40 / 0,70 м оставляют между собой промежутки
                // 0,12 / 0,20 / 0,30 м, и несущая стена, целиком попавшая
                // в промежуток, не находилась. Тогда ограждением оставался сам
                // отделочный слой — 15 мм штукатурки, U ≈ 5,4. На 76-СУЗДАЛ.23
                // (прогон 2026-08-17) таких сегментов было 882, из них около
                // 312 м² выходили «на улицу».
                var found = WallsAlongOutward(doc, midPt, outward,
                                              ProbeCubeHalfSizeM, ThermalWallSearchM)
                    .Where(w => w.Id != boundaryWall.Id &&
                                UnitUtils.ConvertFromInternalUnits(w.Width, UnitTypeId.Meters)
                                    > FinishLayerMaxThicknessM)
                    .Where(IsNotPartition)
                    .ToList();

                if (found.Count > 0)
                {
                    // Из нескольких предпочитаем ту, что по имени или функции
                    // наружная: коридор может задеть и внутреннюю конструкцию
                    // соседнего помещения. При равенстве — самую толстую:
                    // несущая толще фасадного слоя.
                    return found
                        .OrderByDescending(LooksExternal)
                        .ThenByDescending(w => w.Width)
                        .First();
                }

                // Прежний путь точками оставлен запасным: он срабатывает там, где
                // коридор не построился (см. журнал WallsAlongOutward).
                foreach (double offsetM in new[] { 0.08, 0.20, 0.40, 0.70 })
                {
                    double offsetFt = UnitUtils.ConvertToInternalUnits(offsetM, UnitTypeId.Meters);
                    XYZ probe = new XYZ(midPt.X + outward.X * offsetFt,
                                        midPt.Y + outward.Y * offsetFt,
                                        midPt.Z);

                    double epsFt = UnitUtils.ConvertToInternalUnits(0.02, UnitTypeId.Meters);
                    var outline = new Outline(
                        new XYZ(probe.X - epsFt, probe.Y - epsFt, probe.Z - epsFt),
                        new XYZ(probe.X + epsFt, probe.Y + epsFt, probe.Z + epsFt));

                    var candidates = new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_Walls)
                        .WhereElementIsNotElementType()
                        .WherePasses(new BoundingBoxIntersectsFilter(outline))
                        .OfType<Wall>()
                        .Where(w => w.Id != boundaryWall.Id &&
                                    UnitUtils.ConvertFromInternalUnits(w.Width, UnitTypeId.Meters)
                                        > FinishLayerMaxThicknessM)
                        .Where(IsNotPartition)
                        .ToList();

                    if (candidates.Count == 0) continue;

                    // Предпочитаем стену, которая по имени или функции наружная:
                    // проба может задеть и внутреннюю конструкцию соседнего помещения.
                    var candidate = candidates
                        .OrderByDescending(LooksExternal)
                        .ThenByDescending(w => w.Width)
                        .First();

                    return candidate;
                }

                // Границу образует отделка, но несущей стены за ней не нашлось
                // ни на одном из четырёх отступов. Выше стоит `?? wall`, поэтому
                // ограждением останется сам отделочный слой: 15 мм штукатурки
                // дают U ≈ 5,4 — это не физика, а пробел сбора.
                //
                // Молчать нельзя: на 76-СУЗДАЛ.23 (прогон 2026-08-17) так
                // посчитано около 312 м² «на улицу» («Отделка Штук15» 141,9 м²,
                // «Отделка Штук15 кухня» 52,6, «Отделка Окраска» 33,8 и другие).
                Logger.Debug(
                    $"[Несущая] за «{boundaryWall.WallType?.Name}» ({widthM * 1000:F0} мм) " +
                    "несущая стена НЕ найдена на отступах 0,08 / 0,20 / 0,40 / 0,70 м — " +
                    "ограждением останется отделочный слой");
            }
            catch (Exception ex)
            {
                Logger.Debug($"ResolveThermalWall: поиск несущей стены не удался: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Номер квартиры помещения. Цепочка поиска, первое непустое значение выигрывает:
        /// 1) параметр, названный пользователем в настройках (<c>ApartmentParameterName</c>);
        /// 2) типовые имена параметра — «Номер квартиры», «Квартира», «Apartment»;
        /// 3) «Комментарии» (<c>ROOM_COMMENTS</c>) — куда номер квартиры кладут чаще всего,
        ///    если отдельного параметра в проекте не завели.
        /// Пусто — помещение считается внеквартирным (общедомовым).
        /// </summary>
        private string DetermineApartment(Room room)
        {
            // 1. Точное имя из настроек, если пользователь его задал.
            foreach (var name in _apartmentParameterNames)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                try
                {
                    string value = ReadParameterAsText(room.LookupParameter(name));
                    if (IsPlausibleApartmentNumber(value, room, name)) return value.Trim();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[Квартира] Параметр «{name}» не прочитан: {ex.Message}");
                }
            }

            // 2. Поиск по образцу среди ВСЕХ параметров помещения. Точные имена в
            //    проектах не совпадают: в «76-СУЗДАЛ.23» это «N_Кв.Номер» (101) и
            //    «Т_Номер продаваемого помещения» (1-12-9), в другом проекте будет
            //    третий вариант. Образец ищем так, чтобы не поймать однокоренные
            //    площади вроде «N_Кв.Площадь».
            try
            {
                foreach (Parameter parameter in room.Parameters)
                {
                    string name = parameter?.Definition?.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    string lower = name.ToLowerInvariant();
                    bool looksLikeApartmentNumber =
                        lower.Contains("кв.номер")            ||
                        lower.Contains("кв. номер")           ||
                        lower.Contains("номер квартиры")      ||
                        lower.Contains("номер продаваемого")  ||
                        lower.Contains("apartment number")    ||
                        lower == "квартира" || lower == "apartment";

                    if (!looksLikeApartmentNumber) continue;

                    string value = ReadParameterAsText(parameter);
                    if (IsPlausibleApartmentNumber(value, room, name))
                    {
                        Logger.Debug($"[Квартира] {room.Number}: «{name}» = {value}");
                        return value.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Квартира] Перебор параметров не удался: {ex.Message}");
            }

            // 3. Последняя попытка — «Комментарии»: туда номер кладут, если
            //    отдельного параметра в проекте не завели.
            try
            {
                string comments = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                if (IsPlausibleApartmentNumber(comments, room, "Комментарии"))
                    return comments.Trim();
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Квартира] Комментарии не прочитаны: {ex.Message}");
            }

            return string.Empty;
        }

        /// <summary>
        /// Проверка правдоподобности номера квартиры. Раньше бралось первое непустое
        /// значение параметра без разбора, и входной вестибюль 1-го этажа 76-СУЗДАЛ.23
        /// (Revit-помещение «Коридор», ~330 м²) получал <c>Apartment = "1--1-0"</c> —
        /// битую «секция-этаж-номер» с пустым полем этажа. Такое помещение переставало
        /// быть внеквартирным, не снималось автоисключением МОП-коридоров и уносило
        /// свои завышенные стены в квартирный итог.
        ///
        /// Правила (намеренно мягкие — форматы номеров в проектах разные):
        ///   • есть хотя бы одна цифра и это не «0»;
        ///   • в составном номере через дефис нет пустых частей («1--1-0», «-5», «7-»);
        ///   • длина разумная — не абзац описания из «Комментариев».
        /// </summary>
        private static bool IsPlausibleApartmentNumber(string value, Room room, string source)
        {
            string reason;
            if (IsPlausibleApartmentNumber(value, out reason)) return true;

            if (!string.IsNullOrWhiteSpace(value))
            {
                Logger.Debug($"[Квартира] {room.Number} «{room.Name}»: значение «{value.Trim()}» " +
                             $"из «{source}» отброшено — {reason}; помещение считается внеквартирным");
            }
            return false;
        }

        /// <summary>
        /// Чистая проверка формата — без Revit, чтобы её гоняли автотесты.
        /// </summary>
        internal static bool IsPlausibleApartmentNumber(string value, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                reason = "пустое значение";
                return false;
            }

            string trimmed = value.Trim();

            if (trimmed.Length > 24)
                reason = "слишком длинное значение";
            else if (!trimmed.Any(char.IsDigit))
                reason = "нет ни одной цифры";
            else if (trimmed.Split('-').Any(string.IsNullOrWhiteSpace))
                reason = "пустая часть в составном номере";
            else if (trimmed.Trim('0', ' ', '-').Length == 0)
                reason = "номер состоит из нулей";

            return reason == null;
        }

        /// <summary>
        /// Значение параметра как текст независимо от типа хранения. Номер квартиры
        /// бывает и числом («N_Кв.Номер» = 101), и строкой («1-12-9»), а <c>AsString()</c>
        /// для числового параметра возвращает null — из-за этого номер терялся.
        /// </summary>
        private static string ReadParameterAsText(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue) return null;

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString();
                case StorageType.Integer:
                    return parameter.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.Double:
                    // Номер, записанный числом с плавающей точкой, — редкость,
                    // но встречается. Дробную часть отбрасываем: «101,0» → «101».
                    double value = parameter.AsDouble();
                    return Math.Abs(value - Math.Round(value)) < 1e-6
                        ? Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : parameter.AsValueString();
                default:
                    return null;
            }
        }

        // ─────────────────────────────────────────────────────────
        //  ГРАНИЧНЫЕ ТОЧКИ С ТРАНСФОРМАЦИЕЙ
        // ─────────────────────────────────────────────────────────

        private List<System.Windows.Point> GetRoomBoundaryPoints(Room room, Transform transform)
        {
            var points = new List<System.Windows.Point>();

            try
            {
                var options = new SpatialElementBoundaryOptions();
                var boundaries = room.GetBoundarySegments(options);
                if (boundaries == null || boundaries.Count == 0) return points;

                foreach (var boundary in boundaries)
                {
                    foreach (var segment in boundary)
                    {
                        var curve = segment.GetCurve();
                        if (curve == null) continue;

                        var start = curve.GetEndPoint(0);

                        // Применяем трансформацию координат (для linked-моделей)
                        if (transform != null && !transform.IsIdentity)
                            start = transform.OfPoint(start);

                        // Внутренние единицы Revit (футы) → миллиметры. Через UnitUtils,
                        // а не множителем 304.8: ручные множители в этом проекте —
                        // антипаттерн (см. CLAUDE.md, «Единицы»), и именно на таком
                        // множителе, подставленном не туда, уже терялся R стены втрое.
                        points.Add(new System.Windows.Point(
                            UnitUtils.ConvertFromInternalUnits(start.X, UnitTypeId.Millimeters),
                            UnitUtils.ConvertFromInternalUnits(start.Y, UnitTypeId.Millimeters)));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Ошибка получения границ помещения", ex);
            }

            return points;
        }

        // ─────────────────────────────────────────────────────────
        //  ПУБЛИЧНЫЙ ВСПОМОГАТЕЛЬНЫЙ МЕТОД
        // ─────────────────────────────────────────────────────────

        /// <summary>
        /// Определяет, является ли этаж первым или последним для каждого помещения.
        /// </summary>
        public void DetermineRoomFloors(
            List<RoomData> rooms,
            int groundFloorNumber,
            int topFloorNumber)
        {
            foreach (var room in rooms)
                room.DetermineFloorProperties(groundFloorNumber, topFloorNumber);
        }
    }
}
