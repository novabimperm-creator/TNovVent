using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using QOVETER.UI;
using System;
using System.Windows;

namespace QOVETER.Commands
{
    /// <summary>
    /// Расчёт теплопотерь помещений: открывает главное окно.
    ///
    /// <para><b>Своей кнопки у команды нет.</b> До переезда в TNovVent рядом жил
    /// <c>QoveterApplication : IExternalApplication</c>, который создавал вкладку
    /// «QOVETER» и панель с кнопкой. В сборке TNov ленту собирает
    /// <c>TNov.Application</c> на все модули сразу, второй
    /// <see cref="IExternalApplication"/> в той же сборке создал бы вторую вкладку
    /// и вторую регистрацию. Поэтому здесь остаётся только команда.</para>
    ///
    /// <para>Точка входа для кнопки — <see cref="TNovVent.Heatloss"/>: она делает
    /// принятую в TNov преамбулу (инициализация RevitAPI, конфиг, журнал) и зовёт
    /// эту команду. Напрямую вешать кнопку на этот класс можно, но тогда расчёт
    /// не попадёт в общий конфиг и журнал TNov.</para>
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CalculateHeatLossCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var loc = asm.Location;
                var build = System.IO.File.GetLastWriteTime(loc);
                QOVETER.Services.Logger.Info(
                    $"=== QOVETER plugin started === DLL={loc} build={build:yyyy-MM-dd HH:mm:ss}");
                // Путь пишем и в сам журнал, и наружу: temp процесса Revit может
                // отличаться от того, где его ищут.
                QOVETER.Services.Logger.Info($"Журнал: {QOVETER.Services.Logger.LogFilePath}");
                QOVETER.Services.Logger.Flush();

                var uiApp = commandData.Application;
                var doc = uiApp.ActiveUIDocument.Document;

                var mainWindow = new MainWindow(doc);

                // Привязываем окно к Revit-окну, иначе диалог может уйти за главное окно.
                var helper = new System.Windows.Interop.WindowInteropHelper(mainWindow)
                {
                    Owner = uiApp.MainWindowHandle
                };

                mainWindow.ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка запуска плагина:\n{ex.Message}",
                    "QOVETER - Расчет теплопотерь",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return Result.Failed;
            }
            finally
            {
                // Лог пишется через буфер — досбрасываем хвост, иначе последние
                // строки сессии не доедут до файла.
                QOVETER.Services.Logger.Flush();
            }
        }
    }
}
