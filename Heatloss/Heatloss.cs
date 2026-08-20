using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using TNovCommon;

namespace TNovVent
{
    /// <summary>
    /// Расчёт теплопотерь помещений по ГОСТ 30494 / СП 50.13330 / СП 60.13330 /
    /// СП 131.13330 — точка входа для кнопки ленты TNov.
    ///
    /// <para><b>Зачем обёртка, а не кнопка прямо на команду.</b> Расчёт живёт
    /// в подпапке <c>Heatloss</c> со своими пространствами имён <c>QOVETER.*</c>:
    /// это 45 файлов, у которых свой журнал, свой каталог нормативов и свои тесты,
    /// и переименовывать их означало бы потерять связь с историей правок и с
    /// проектной документацией. Здесь же — тонкий слой, который делает принятую
    /// в TNov преамбулу (RevitAPI, конфиг, журнал) и передаёт управление расчёту.</para>
    ///
    /// <para><b>Журналов два, и это намеренно.</b> <see cref="Logger"/> из
    /// TNovCommon регистрирует запуск команды в общей системе; собственный журнал
    /// расчёта (<c>%APPDATA%\QOVETER\logs</c>) — диагностический, в нём сотни тысяч
    /// строк на прогон, и именно по нему разбираются расхождения с расчётом
    /// проектировщика. Сливать их в один нельзя: у них разные читатели.</para>
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Heatloss : IExternalCommand
    {
        /// <summary>Имя команды в конфиге и журнале TNov.</summary>
        public const string DBCommandName = "Теплопотери";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            DateTime dateTime = DateTime.Now;
            string tnovVersion = System.Reflection.Assembly
                .GetExecutingAssembly().GetName().Version.ToString();

            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }

            TNovConfigLoad.LoadConfig(DBCommandName, tnovVersion);
            Logger.Initialize(DBCommandName, dateTime, tnovVersion);

            return new QOVETER.Commands.CalculateHeatLossCommand()
                .Execute(commandData, ref message, elements);
        }
    }
}
