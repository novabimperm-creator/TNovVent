using QOVETER.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QOVETER.Services
{
    /// <summary>Способ получения приведённого сопротивления теплопередаче.</summary>
    public enum ReducedResistanceMode
    {
        /// <summary>Не считать: U остаётся условным (одномерный слоёный пирог).</summary>
        Off = 0,

        /// <summary>По каталогу линейных коэффициентов Ψ: U_пр = U_усл + Σ(Ψ·l)/A.</summary>
        Catalog,

        /// <summary>По коэффициенту теплотехнической однородности r: U_пр = U_усл / r.</summary>
        HomogeneityFactor
    }

    /// <summary>
    /// Приведённое сопротивление теплопередаче наружных стен помещения.
    ///
    /// Зачем: <see cref="WallThermalCalculator"/> считает U по <c>CompoundStructure</c>
    /// как одномерный слоёный пирог — это R УСЛОВНОЕ. Норматив требует R ПРИВЕДЁННОЕ,
    /// с учётом теплотехнических неоднородностей: углов, откосов, примыканий плит.
    /// Без этого потери в углах и у проёмов систематически недобираются.
    ///
    /// Два пути, оба честные:
    ///   • <see cref="ReducedResistanceMode.Catalog"/> — U_пр = U_усл + Σ(Ψ·l)/A,
    ///     где Ψ берутся из <see cref="ThermalBridgeCatalog"/>, а длины узлов l
    ///     восстанавливаются из геометрии помещения;
    ///   • <see cref="ReducedResistanceMode.HomogeneityFactor"/> — U_пр = U_усл / r,
    ///     где r (0.7…0.92) инженер задаёт руками. Грубее, но требует только одного
    ///     числа и не зависит от полноты каталога.
    /// </summary>
    public class ReducedResistanceCalculator
    {
        private readonly IReadOnlyDictionary<ThermalBridgeType, ThermalBridgeEntry> _catalog;

        public ReducedResistanceCalculator(
            IReadOnlyDictionary<ThermalBridgeType, ThermalBridgeEntry> catalog = null)
        {
            _catalog = catalog ?? ThermalBridgeCatalog.Default;
        }

        /// <summary>
        /// Считает приведённое U наружных стен помещения.
        /// </summary>
        /// <param name="room">Помещение с заполненной геометрией ограждений.</param>
        /// <param name="conditionalU">U условное, Вт/(м²·К) — то, что даёт слоёный пирог.</param>
        /// <param name="mode">Способ расчёта.</param>
        /// <param name="homogeneityFactor">r для режима <see cref="ReducedResistanceMode.HomogeneityFactor"/>.</param>
        /// <param name="floorHeight">Высота этажа, м — длина вертикальных узлов (углы, откосы по бокам).</param>
        public ReducedResistanceResult Calculate(
            RoomData room,
            double conditionalU,
            ReducedResistanceMode mode,
            double homogeneityFactor,
            double floorHeight,
            WallConstructionProfile profile = null,
            BridgeSelectors selectors = null)
        {
            var result = new ReducedResistanceResult
            {
                ConditionalU = conditionalU,
                ReducedU     = conditionalU,
                Homogeneity  = 1.0,
                Mode         = mode
            };

            if (mode == ReducedResistanceMode.Off || conditionalU <= 0)
                return result;

            if (mode == ReducedResistanceMode.HomogeneityFactor)
            {
                if (homogeneityFactor <= 0 || homogeneityFactor > 1)
                {
                    Logger.Debug($"[R_пр] {room.Name}: r = {homogeneityFactor} вне диапазона (0…1] — " +
                                 "приведение не выполнено");
                    return result;
                }

                result.ReducedU    = conditionalU / homogeneityFactor;
                result.Homogeneity = homogeneityFactor;
                result.IsProvisional = false;   // r задал инженер, это его ответственность
                return result;
            }

            // ─── Режим каталога ────────────────────────────────────────────
            double wallArea = room.WallArea > 0
                ? room.WallArea
                : room.Walls?.Where(w => w.IsExternal).Sum(w => w.Area) ?? 0;

            if (wallArea <= 0)
            {
                Logger.Debug($"[R_пр] {room.Name}: нулевая площадь наружных стен — приведение не выполнено");
                return result;
            }

            // Наружные ограждения у помещения ЕСТЬ. Отличать это от «их нет вовсе»
            // обязательно: помещение без ограждений не имеет узлов по построению,
            // и называть его в сводке так же, как помещение с неопознанной
            // конструкцией, значит записывать в недолёт то, чего недобирать нечем.
            result.HasEnclosures = true;

            var construction = profile?.Construction ?? WallConstructionType.Unknown;
            result.Construction = construction;
            result.ConstructionOrigin = profile?.Origin ?? WallConstructionOrigin.FromLayers;

            foreach (var node in DetectNodes(room, floorHeight, construction))
            {
                if (node.Length <= 0) continue;

                // Сначала пробуем СП 230: значение из таблицы приложения Г, подобранной
                // по конструкции стены. Не нашлось — падаем на ручной каталог.
                // Толщина основания приходит из слоёв стены, остальные признаки —
                // из настроек расчёта: в модели их нет.
                var nodeSelectors = selectors == null
                    ? new BridgeSelectors
                    {
                        BaseThicknessMm = profile?.BaseThicknessMm,
                        WallInsulationR = profile?.InsulationResistance
                    }
                    : new BridgeSelectors
                    {
                        FrameMm = selectors.FrameMm,
                        NotchMm = selectors.NotchMm,
                        OverlapMm = selectors.OverlapMm,
                        SlabInsulationR = selectors.SlabInsulationR,
                        SlabThicknessMm = selectors.SlabThicknessMm,
                        SlabPerforationRatio = selectors.SlabPerforationRatio,
                        BaseThicknessMm = selectors.BaseThicknessMm ?? profile?.BaseThicknessMm,
                        ParapetInsulationMm = selectors.ParapetInsulationMm,
                        // R утеплителя на стене — из конструкции, а не из настроек.
                        WallInsulationR = selectors.WallInsulationR ?? profile?.InsulationResistance,

                        // Исполнение узла ПЕРЕПИСЫВАЛОСЬ ЗДЕСЬ В NULL до 2026-08-19:
                        // копировались все поля, кроме этого. Настройка существовала,
                        // была описана и покрыта тестом на SP230Catalog.Find — но
                        // до каталога из расчёта не доезжала, и любое заданное
                        // инженером исполнение молча заменялось «взять худшее
                        // из равных». Цена на СФТК: Ψ оконного узла 0,433 (Г.35)
                        // вместо 0,100 (Г.33) — вчетверо, при том что откос окна
                        // даёт больше потерь, чем угол.
                        Execution = selectors.Execution
                    };

                var fromSp = profile != null
                    ? SP230Catalog.Find(node.Type, profile.Construction, profile,
                                        node.Variant, nodeSelectors)
                    : null;

                if (fromSp != null)
                {
                    result.Nodes.Add(new ThermalBridgeContribution
                    {
                        Type      = node.Type,
                        Variant   = node.Variant,
                        Name      = fromSp.Title,
                        Psi       = fromSp.Psi,
                        Length    = Math.Round(node.Length, 2),
                        Loss      = Math.Round(fromSp.Psi * node.Length, 3),
                        Reference = fromSp.Reference,
                        FromNorm  = true,
                        Execution = fromSp.Execution,
                        IsExecutionAssumed = fromSp.IsExecutionAssumed
                    });
                    if (fromSp.IsClamped) result.HasClampedParameters = true;
                    continue;
                }

                // Вогнутый угол ручным каталогом не подменяется: там лежит одно
                // значение на узел, и оно ПОЛОЖИТЕЛЬНОЕ. Подставить его вогнутому
                // углу — это не «в запас», а перевёрнутый знак: по СП 230 раздел Г.4
                // вогнутый угол теплопотери ВЫЧИТАЕТ.
                if (node.Variant == BridgeVariant.Concave)
                {
                    result.SkippedNodes.Add(node.Type);
                    continue;
                }

                ThermalBridgeEntry entry;
                if (!_catalog.TryGetValue(node.Type, out entry) || entry == null)
                {
                    result.SkippedNodes.Add(node.Type);
                    continue;
                }

                // Таблицы СП 230 для этого узла при этой конструкции нет, а значение
                // «из коробки» — оценка порядка величины, а не выписка из норматива.
                // В итог оно не идёт: с 2026-08-19 режим включён ПО УМОЛЧАНИЮ, и
                // подставленное Ψ размазалось бы по всему дому молча. Узел
                // пропускается и попадает в сводку прогона — это недолёт, но
                // недолёт названный. Инженер, выписавший Ψ из СП в
                // thermal_bridges.json с IsVerified = true, получает его в расчёт.
                if (!entry.IsVerified)
                {
                    result.SkippedNodes.Add(node.Type);
                    Logger.Debug(
                        $"[R_пр] {room.Name}: узел «{entry.Name}» пропущен — таблицы СП 230 " +
                        $"для конструкции {construction} нет, а каталожное Ψ = {entry.Psi} не сверено");
                    continue;
                }

                result.Nodes.Add(new ThermalBridgeContribution
                {
                    Type      = node.Type,
                    Variant   = node.Variant,
                    Name      = entry.Name,
                    Psi       = entry.Psi,
                    Length    = Math.Round(node.Length, 2),
                    Loss      = Math.Round(entry.Psi * node.Length, 3),
                    Reference = entry.Source,
                    FromNorm  = true
                });
            }

            double extraPerSquareMeter = result.Nodes.Sum(n => n.Loss) / wallArea;

            // ── Тарельчатые анкеры: ТОЧЕЧНЫЙ элемент (СП 230 таблица Г.4) ───────
            //
            // Потери считаются на штуку: Σχ·n = χ · (плотность · A), поэтому в U_пр
            // добавка равна просто χ · плотность — площадь сокращается. Крепёж
            // рядом с углом в Ψ угла НЕ входит, СП 230 раздел Г.4 говорит это прямо.
            //
            // Плотность анкеров из модели не вытаскивается, умолчания у неё нет,
            // и пока она не задана — анкеры не считаются, а сводка это называет.
            double anchorsPerM2 = selectors?.AnchorsPerM2 ?? 0;
            if (anchorsPerM2 > 0)
            {
                double l1 = selectors.AnchorL1Mm ?? 0;   // не задано — худший случай
                result.AnchorChi = SP230Catalog.AnchorChi(l1);
                result.AnchorsPerM2 = anchorsPerM2;
                result.AnchorU = result.AnchorChi * anchorsPerM2;
                extraPerSquareMeter += result.AnchorU;
            }

            result.ReducedU  = conditionalU + extraPerSquareMeter;
            result.Homogeneity = result.ReducedU > 0 ? conditionalU / result.ReducedU : 1.0;

            Logger.Debug($"[R_пр] {room.Name}: U_усл={conditionalU:F3} → U_пр={result.ReducedU:F3} " +
                         $"(r={result.Homogeneity:F3}, конструкция {construction}, " +
                         $"узлов {result.Nodes.Count}, пропущено {result.SkippedNodes.Count}, " +
                         $"S_ст={wallArea:F1} м²)");

            return result;
        }

        /// <summary>
        /// Восстанавливает длины линейных узлов из геометрии помещения, м.
        ///
        /// Что удаётся распознать и как:
        ///   • наружный угол — по <c>NumberOfExternalWalls</c>: у помещения с N наружными
        ///     стенами N−1 внутренних стыков между ними; длина каждого = высота этажа;
        ///   • оконный и дверной откос — периметр проёма; если габариты окна из модели
        ///     не пришли, периметр оценивается по площади как для квадрата (заниженная,
        ///     но безопасная оценка — реальное окно шире квадрата, периметр больше);
        ///   • примыкание плиты перекрытия — длина наружных стен помещения (одно
        ///     примыкание на этаж);
        ///   • парапет и цокольный узел — та же длина, но только на последнем и первом
        ///     этаже соответственно.
        ///
        /// Балконная плита автоматически НЕ распознаётся: для этого нужно знать, что
        /// за дверью балкон, а это связь между помещениями, которой у нас пока нет.
        /// Узел в каталоге есть — его можно учесть вручную.
        /// </summary>
        /// <summary>
        /// Какие узлы вообще существуют у этой конструкции — для тестов.
        /// Помещение берётся типовое: одна наружная стена 5 м, высота 3 м.
        /// </summary>
        public List<ThermalBridgeType> DetectNodesForTest(WallConstructionType construction)
        {
            var room = new RoomData
            {
                Name = "Тестовая комната",
                Height = 3,
                WallArea = 15,
                NumberOfExternalWalls = 1,
                Walls = new List<WallInfo>
                {
                    new WallInfo { Area = 15, Length = 5, Height = 3, IsExternal = true }
                }
            };
            return DetectNodes(room, 3.0, construction).Select(n => n.Type).Distinct().ToList();
        }

        /// <summary>
        /// Линейный узел, найденный геометрией: что за узел, в каком исполнении
        /// и какой длины.
        ///
        /// <para>Вариант стал частью узла 2026-08-20. До этого <c>DetectNodes</c>
        /// отдавала словарь «тип → длина», и вариант в каталог уходил всегда один —
        /// <see cref="BridgeVariant.Convex"/>. Это делало невозможным сам вопрос
        /// о вогнутом угле: два угла одного помещения нельзя было положить
        /// в словарь под одним ключом с разными знаками Ψ.</para>
        /// </summary>
        internal struct DetectedNode
        {
            public ThermalBridgeType Type;
            public BridgeVariant Variant;
            public double Length;
        }

        internal List<DetectedNode> DetectNodes(
            RoomData room, double floorHeight,
            WallConstructionType construction = WallConstructionType.Unknown)
        {
            var nodes = new List<DetectedNode>();

            // СП 230, раздел Г.4: для тонкостенных панелей и стен с внутренним
            // утеплением угол как геометрический элемент при расчётах не учитывается.
            bool countCorners =
                construction != WallConstructionType.ThinPanel &&
                construction != WallConstructionType.InternalInsulation;
            double height = floorHeight > 0 ? floorHeight : (room.Height > 0 ? room.Height : 3.0);

            // 1. Наружные углы — выпуклые и вогнутые ОТДЕЛЬНО.
            //
            // СП 230 раздел Г.4: угол — чисто геометрический элемент. Выпуклый
            // (здание выступает наружу) добавляет теплопотери, вогнутый (ниша
            // в фасаде) их ВЫЧИТАЕТ, и таблицы Г.27/Г.28 дают для него
            // отрицательные Ψ. До 2026-08-20 все углы считались выпуклыми
            // «в запас» — на изрезанном фасаде это завышение, а не запас:
            // у здания с прямыми углами выпуклых ровно на 4 больше вогнутых,
            // и лишние вогнутые начисляются со знаком плюс каждому помещению ниши.
            if (countCorners)
            {
                if (room.ConvexCorners.HasValue || room.ConcaveCorners.HasValue)
                {
                    // Углы распознаны геометрией границы помещения — считаем их.
                    int convex  = Math.Max(0, room.ConvexCorners  ?? 0);
                    int concave = Math.Max(0, room.ConcaveCorners ?? 0);
                    if (convex > 0)
                        nodes.Add(new DetectedNode
                        {
                            Type = ThermalBridgeType.ExternalCorner,
                            Variant = BridgeVariant.Convex,
                            Length = convex * height
                        });
                    if (concave > 0)
                        nodes.Add(new DetectedNode
                        {
                            Type = ThermalBridgeType.ExternalCorner,
                            Variant = BridgeVariant.Concave,
                            Length = concave * height
                        });
                }
                else
                {
                    // Геометрии нет (фикстура, синтетика в тестах) — прежнее правило:
                    // у помещения с N наружными стенами N−1 стыков, все выпуклые.
                    int corners = Math.Max(0, room.NumberOfExternalWalls - 1);
                    if (corners == 0 && room.IsCorner) corners = 1;
                    if (corners > 0)
                        nodes.Add(new DetectedNode
                        {
                            Type = ThermalBridgeType.ExternalCorner,
                            Variant = BridgeVariant.Convex,
                            Length = corners * height
                        });
                }
            }

            // 2. Откосы проёмов
            double windowPerimeter = (room.Windows ?? new List<WindowInfo>())
                .Sum(w => OpeningPerimeter(w.Width, w.Height, w.Area));
            if (windowPerimeter > 0)
                nodes.Add(new DetectedNode
                {
                    Type = ThermalBridgeType.WindowReveal, Length = windowPerimeter
                });

            double doorPerimeter = (room.Doors ?? new List<DoorInfo>())
                .Where(d => d.IsExternal)
                .Sum(d => OpeningPerimeter(d.Width, d.Height, d.Area));
            if (doorPerimeter > 0)
                nodes.Add(new DetectedNode
                {
                    Type = ThermalBridgeType.DoorReveal, Length = doorPerimeter
                });

            // 3. Горизонтальные узлы по длине наружных стен
            double wallLength = ExternalWallLength(room, height);
            if (wallLength > 0)
            {
                // СП 230, раздел Г.3: при НАРУЖНОМ утеплении выходы плиты перекрытия
                // закрыты утеплителем и мостиками холода не являются — учитывать надо
                // только стыки с балконными плитами, где утеплитель действительно
                // разрывается. Считать здесь плиту означало бы завысить потери.
                bool slabCrossesInsulation =
                    construction != WallConstructionType.ExternalInsulationThinFacing;

                if (slabCrossesInsulation)
                    nodes.Add(new DetectedNode
                    {
                        Type = ThermalBridgeType.FloorSlab, Length = wallLength
                    });

                if (room.IsLastFloor)
                    nodes.Add(new DetectedNode
                    {
                        Type = ThermalBridgeType.Parapet, Length = wallLength
                    });
                if (room.IsFirstFloor)
                    nodes.Add(new DetectedNode
                    {
                        Type = ThermalBridgeType.BaseJunction, Length = wallLength
                    });
            }

            // 4. Балконная плита — по длине ограждения, отделяющего помещение
            //    от балкона или лоджии. Именно там плита пересекает утеплитель.
            //
            // <para><b>Почему стало возможно.</b> В плане этот узел стоял «за рамками»
            // с формулировкой «нет признака за дверью балкон». Признак появился
            // 2026-08-13 вместе с тепловым балансом неотапливаемых объёмов:
            // <see cref="IAdjacentSurface.AdjacentCategory"/> у стены и двери прямо
            // называет, что за ограждением. Отдельно искать «балконную дверь»
            // не нужно — плита пересекает стену на всю ширину балкона, а это и есть
            // длина ограждения.</para>
            //
            // <para><b>Двойного счёта с узлом плиты перекрытия нет.</b> При наружном
            // утеплении FloorSlab не считается вовсе (Г.3), а при кладке считается
            // по длине УЛИЧНЫХ ограждений — стена на балкон туда не входит.</para>
            double balconyLength = BalconyWallLength(room);
            if (balconyLength > 0)
                nodes.Add(new DetectedNode
                {
                    Type = ThermalBridgeType.BalconySlab, Length = balconyLength
                });

            return nodes;
        }

        /// <summary>
        /// Длина ограждений помещения, за которыми балкон или лоджия, м — по ней
        /// считается узел балконной плиты (СП 230 таблицы Г.17–Г.23).
        ///
        /// <para>Балкон и лоджия в модели одна категория
        /// <see cref="RoomCategory.Balcony"/> («Балкон/Лоджия»), и для этого узла
        /// разделять их не нужно: в обоих случаях плита проходит сквозь плоскость
        /// утепления наружной стены. Разница между консольным балконом и утопленной
        /// лоджией сидит в перфорации плиты, а это отдельный признак настройки.</para>
        /// </summary>
        private static double BalconyWallLength(RoomData room)
        {
            var walls = (room.Walls ?? new List<WallInfo>())
                .Where(w => w.IsExternal && w.AdjacentCategory == RoomCategory.Balcony)
                .ToList();

            double byLength = walls.Sum(w => w.Length);
            if (byLength > 0) return byLength;

            // Длин в модели нет — оцениваем по площади и высоте помещения.
            double area = walls.Sum(w => w.Area);
            double height = room.Height > 0 ? room.Height : 3.0;
            return area > 0 ? area / height : 0;
        }

        /// <summary>
        /// Периметр проёма. Габариты из модели точнее; при их отсутствии считаем проём
        /// квадратным — это ЗАНИЖАЕТ периметр (у квадрата он минимален при данной площади),
        /// то есть ошибка идёт в сторону меньших теплопотерь, и это надо помнить.
        /// </summary>
        private static double OpeningPerimeter(double width, double height, double area)
        {
            if (width > 0 && height > 0) return 2 * (width + height);
            if (area > 0) return 4 * Math.Sqrt(area);
            return 0;
        }

        /// <summary>
        /// Длина ограждений помещения, выходящих НА УЛИЦУ, по низу, м — по ней
        /// считаются горизонтальные узлы: выход плиты перекрытия, цокольный узел,
        /// парапет.
        ///
        /// <para><b>Почему только улица.</b> До 2026-08-20 длина бралась по ВСЕМ
        /// ограждениям, а <c>IsExternal</c> у нас стоит и на стене в вентшахту,
        /// и на стене к лоджии, лифтовому холлу и лестнице — они ограждающие, просто
        /// за ними не наружный воздух. На 76-СУЗДАЛ.23 это около 2 100 м² из 5 800,
        /// то есть больше трети длины узлов бралось там, где узла нет:</para>
        ///
        /// <list type="bullet">
        /// <item>цокольный узел (Г.39, Г.40) — это примыкание стены к ЦОКОЛЬНОМУ
        /// ограждению, то есть к наружному контуру здания у земли; у стены в шахту
        /// цоколя нет;</item>
        /// <item>парапет (Г.41–Г.52) — край КРОВЛИ по фасаду; стена в шахту
        /// с парапетом не стыкуется;</item>
        /// <item>выход плиты перекрытия (Г.5–Г.10) — торец плиты, выходящий
        /// НА ФАСАД и охлаждаемый наружным воздухом; за стеной шахты торец плиты
        /// остаётся внутри здания.</item>
        /// </list>
        ///
        /// <para>Бьёт это прицельно по первому и последнему этажам: цокольный узел
        /// и парапет есть только у них, Ψ там самые большие в каталоге
        /// (0,45…0,71 и 0,55…0,84), и лишняя длина превращалась в киловатты именно
        /// на тех двух этажах, которые расходились с проектировщиком сильнее типовых.</para>
        ///
        /// <para>Приоритет — реальные длины стен из модели; без них площадь уличных
        /// ограждений, делённая на высоту этажа.</para>
        /// </summary>
        private static double ExternalWallLength(RoomData room, double height)
        {
            var walls = room.Walls ?? new List<WallInfo>();
            var street = walls.Where(w => w.IsExternal && !w.AdjacentCategory.HasValue).ToList();

            double byLength = street.Sum(w => w.Length);
            if (byLength > 0) return byLength;

            // Длин в модели нет (фикстура старого формата) — считаем по площади.
            // Здесь тоже только уличная часть; если разделения нет вовсе, поведение
            // прежнее, и golden-фикстуры не двигаются.
            double streetArea = street.Sum(w => w.Area);
            if (streetArea <= 0 && !walls.Any(w => w.IsExternal && w.AdjacentCategory.HasValue))
                streetArea = room.WallArea;

            return height > 0 ? streetArea / height : 0;
        }
    }

    public class ReducedResistanceResult
    {
        /// <summary>U условное (одномерный слоёный пирог), Вт/(м²·К).</summary>
        public double ConditionalU { get; set; }

        /// <summary>U приведённое, Вт/(м²·К).</summary>
        public double ReducedU { get; set; }

        /// <summary>Коэффициент теплотехнической однородности r = U_усл / U_пр.</summary>
        public double Homogeneity { get; set; }

        public ReducedResistanceMode Mode { get; set; }

        /// <summary>
        /// Хотя бы одно использованное Ψ не сверено с текстом СП 230 — результат
        /// нельзя подавать в отчёте как нормативный.
        /// </summary>
        public bool IsProvisional { get; set; }

        /// <summary>
        /// Параметр конструкции вышел за пределы сетки таблицы СП и был зажат по краю.
        /// Значение остаётся нормативным, но инженер должен об этом знать.
        /// </summary>
        public bool HasClampedParameters { get; set; }

        public List<ThermalBridgeContribution> Nodes { get; } = new List<ThermalBridgeContribution>();

        /// <summary>
        /// Узлы, распознанные геометрией, но НЕ учтённые: таблицы СП 230 для этой
        /// конструкции нет, а каталожное Ψ не сверено. Итог по ним занижен, и
        /// сводка прогона обязана это назвать.
        /// </summary>
        public List<ThermalBridgeType> SkippedNodes { get; } = new List<ThermalBridgeType>();

        /// <summary>Тип конструкции, по которому подбирались таблицы СП 230.</summary>
        public WallConstructionType Construction { get; set; } = WallConstructionType.Unknown;

        /// <summary>Откуда взята конструкция: из слоёв модели или допущением.</summary>
        public WallConstructionOrigin ConstructionOrigin { get; set; } = WallConstructionOrigin.FromLayers;

        /// <summary>
        /// У помещения есть наружные ограждения. Ложь — узлов нет ПО ПОСТРОЕНИЮ
        /// (внутреннее помещение), и это не недолёт расчёта.
        /// </summary>
        public bool HasEnclosures { get; set; }

        /// <summary>
        /// Добавка к U от тарельчатых анкеров, Вт/(м²·К) — χ · плотность
        /// (СП 230 таблица Г.4). Ноль — плотность крепежа не задана, и анкеры
        /// не считались: теплопотери на этом занижены, и сводка обязана это сказать.
        /// </summary>
        public double AnchorU { get; set; }

        /// <summary>χ одного анкера, Вт/°С, по таблице Г.4 — для отчёта.</summary>
        public double AnchorChi { get; set; }

        /// <summary>Принятая плотность анкеров, шт/м² — для отчёта.</summary>
        public double AnchorsPerM2 { get; set; }

        public double ConditionalR => ConditionalU > 0 ? 1.0 / ConditionalU : 0;
        public double ReducedR     => ReducedU     > 0 ? 1.0 / ReducedU     : 0;
    }

    public class ThermalBridgeContribution
    {
        public ThermalBridgeType Type { get; set; }
        /// <summary>Исполнение узла: у угла — выпуклый или вогнутый (СП 230 раздел Г.4).</summary>
        public BridgeVariant Variant { get; set; }
        public string Name { get; set; }
        public double Psi { get; set; }
        public double Length { get; set; }
        /// <summary>Ψ·l, Вт/К.</summary>
        public double Loss { get; set; }
        /// <summary>Откуда значение: номер таблицы СП 230 либо пометка каталога.</summary>
        public string Reference { get; set; }
        /// <summary>Значение взято из норматива, а не из предварительной оценки.</summary>
        public bool FromNorm { get; set; }

        /// <summary>Исполнение узла из каталога СП: положение рамы, перфорация плиты и т. п.</summary>
        public string Execution { get; set; }

        /// <summary>
        /// Исполнение узла инженером НЕ задано, и из равных по числовым признакам
        /// таблиц взята худшая по потерям. Значение остаётся нормативным, но это
        /// оценка В ЗАПАС, а не описание конкретного проекта: у оконного узла
        /// разница между лучшим и худшим исполнением доходит до восьми раз.
        /// </summary>
        public bool IsExecutionAssumed { get; set; }
    }
}
