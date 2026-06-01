using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Microsoft.Office.Interop.Excel;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using TNovCommon;
using Parameter = Autodesk.Revit.DB.Parameter;
using View = Autodesk.Revit.DB.View;

namespace TNovVent
{
    
    [Transaction(TransactionMode.Manual)]
    public class Duct3D : IExternalCommand
    {
        
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            
            #region Исходные
            DateTime dateTime = DateTime.Now;
            string TNovVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            string DBCommandName = "Схемы вентиляции";
            //подключение приложения и документа
            if (RevitAPI.UiApplication == null) { RevitAPI.Initialize(commandData); }
            UIDocument uidoc = RevitAPI.UiDocument; Document doc = RevitAPI.Document;
            UIApplication uiApp = RevitAPI.UiApplication; Autodesk.Revit.ApplicationServices.Application rvtApp = uiApp.Application;
            string docName = doc.Title.ToString(); docName = docName.Replace(",", " ");
            string userName = rvtApp.Username; userName = userName.Replace(",", "");
            string docNameUserName = "_" + userName; docName = docName.Replace(docNameUserName, "");
            docName = docName.Replace(",", "");
            #endregion

            TNovConfig config = TNovConfigLoad.LoadConfig(DBCommandName, TNovVersion);

            #region Настройки логов
            // создание log - файла
            Logger.Initialize(DBCommandName, dateTime, TNovVersion);

            var viewModel0 = new AppVersionViewModel();

            string jsonpath0 = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TNovClient/TNovSettings.json");
            viewModel0 = JsonConvert.DeserializeObject<AppVersionViewModel>(File.ReadAllText(jsonpath0));
            if (viewModel0.extendedLogs)

            {
                var qViewModel = new QuestionWindowViewModel();
                qViewModel.headtxt = "Включены расширенные логи. " +
                    "Плагин будет работать медленнее, но соберет больше данных. " +
                    "Выключить расширенные логи для ускорения работы?";
                var qwpfview = new QuestionWindow280(qViewModel);
                qViewModel.CloseRequest += (s, e) => qwpfview.Close();
                bool? qok = qwpfview.ShowDialog();
                if (qok != null && qok == true) { Logger.TurnOffExtendedLogs(); } else Logger.Log("Расширенные логи вкл", 2);
            }
            #endregion


            string viewPrefix = "TNov_3D_";
            string filterPrefix = "TNov_Вент_не ";

            #region Сбор элементов
            List<Element> Vozd = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DuctCurves)
                .WhereElementIsNotElementType()
                .Cast<Element>()
                .ToList();

            List<View3D> templates = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Views)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .ToList();

            List<View3D> tmpl = templates.Where(t=>t.IsTemplate&&t.Name== "О_3D_Вентиляция").ToList();
            Logger.Log("Результат поиска шаблона вида О_3D_Вентиляция: найдено видов " + tmpl.Count.ToString(),2);

            if (Vozd.Count == 0) 
            { 
                new InfoWindow280("Нечего обрабатывать.").ShowDialog(); 
                Logger.Log("Нечего обрабатывать. Завершение работы.", 3); 
                return Result.Failed; 
            }
            #endregion

            //проверка наличия шаблона вида О_3D_Вентиляция

            //ElementId viewtemplId = new ElementId(74641); //id нужного шаблона вида
            ElementId viewtemplId = tmpl[0].Id; Logger.Log("ID нужного шаблона вида: "+ viewtemplId.ToString(), 2);

            try
            {
                Element e = doc.GetElement(viewtemplId);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentNullException)
            {
                string mes = "В проекте отсутствует шаблон вида О_3D_Вентиляция.";
                new InfoWindow280(mes).ShowDialog();
                Logger.Log(mes+". Завершение работы.",3);
                return Result.Failed;
            }

            #region Диалог
            Logger.Log("Элементы собраны. Диалог",1);

            var viewModel = new Duct3DViewModel();
            // Десериализация
            bool forProject = true;
            json js = new json(in DBCommandName, in forProject, out bool canserialize, out string jsonpath);
            if (canserialize)
            {
                viewModel = JsonConvert.DeserializeObject<Duct3DViewModel>(File.ReadAllText(jsonpath));
                Logger.Log("Десериализация прошла успешно",1);
            }
            var wpfview = new Duct3DWPF(viewModel);
            viewModel.CloseRequest += (s, e) => wpfview.Close();
            bool? ok = wpfview.ShowDialog();
            if (ok != null && ok == true) { }
            else { Logger.Log("Запуск отменен пользователем. Завершение работы.", 3); return Result.Cancelled; }
            //Сериализация
            try
            {
                File.WriteAllText(jsonpath, JsonConvert.SerializeObject(viewModel));
                Logger.Log("Сериализация прошла успешно",1);
            }
            catch (Exception ex) { Logger.Log("Ошибка при сериализации: " + ex.Message,4); }

            string parameter = viewModel.output1;
            ElementId systemnameparamId = new ElementId(-1140324); //id параметра Имя системы
            if (parameter == "ADSK_Группирование") { systemnameparamId = new ElementId(2606); }
            #endregion

            #region Рабочий вид
            ElementId workviewid = uidoc.ActiveView.Id;

            //Создаем 3д-вид, где видны все элементы

            Logger.Log("Настраиваем вид TNov",1);

            List<View> views = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Views)   //фильтр по категории Виды
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .Cast<View>()                     //элементы категории Виды
                                                                         .ToList();                         //формируем список

            ViewFamilyType viewFamilyType3D = new FilteredElementCollector(doc)
                                                                            .OfClass(typeof(ViewFamilyType))
                                                                            .Cast<ViewFamilyType>()
                                                                            .FirstOrDefault<ViewFamilyType>(
                                                                            x => ViewFamily.ThreeDimensional == x.ViewFamily);
            
            bool viewexist = false;
            foreach (View view in views) { if (view.Name == "TNov") { viewexist = true; } }

            XYZ eye = new XYZ(209.059336076, -96.075550675, 226.195776737);
            XYZ up = new XYZ(-0.408248290, 0.408248290, 0.816496581);
            XYZ forward = new XYZ(-0.577350269, 0.577350269, -0.577350269);

            View tNovView = doc.ActiveView;
            if (viewexist == false)
            {
                using (Transaction transaction0 = new Transaction(doc))
                {

                    transaction0.Start("TNov - рабочий 3D-вид");

                    View3D view3d = View3D.CreateIsometric(doc, viewFamilyType3D.Id);

                    view3d.SetOrientation(new ViewOrientation3D(eye, up, forward));

                    view3d.Name = "TNov";

                    workviewid = view3d.Id;

                    Parameter viewtemplate = view3d.get_Parameter(BuiltInParameter.VIEW_TEMPLATE);
                    try
                    {
                        viewtemplate.Set(viewtemplId);
                    }
                    catch (Exception ex) { Logger.Log("Ошибка при назначении шаблона вида: " + ex.Message,4); }


                    transaction0.Commit();
                }
            }
            else
            {
                //3d-вид создан либо существует, сбрасываем его подрезку
                List<View> views1 = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Views)   //фильтр по категории Виды
                                                                             .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                             .Cast<View>()                     //элементы категории Виды
                                                                             .ToList();                         //формируем список
                foreach (View view in views1) { if (view.Name == "TNov") { /*uidoc.ActiveView = view*/; workviewid = view.Id; } }
                Autodesk.Revit.DB.View3D workview3d;
                workview3d = (View3D)doc.GetElement(workviewid);

                using (Transaction transaction0 = new Transaction(doc))
                {

                    transaction0.Start("TNov - рабочий 3D-вид");

                    workview3d.IsSectionBoxActive = false;

                    workview3d.SetOrientation(new ViewOrientation3D(eye, up, forward));

                    Parameter viewtemplate = workview3d.get_Parameter(BuiltInParameter.VIEW_TEMPLATE);
                    try
                    {
                        viewtemplate.Set(viewtemplId);
                    }
                    catch (Exception ex) { Logger.Log("Ошибка при назначении шаблона вида: " + ex.Message, 4); }

                    transaction0.Commit();
                }

            }
            Logger.Log("Вид TNov настроен для работы",1);
            List<View> views2 = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Views)   //фильтр по категории Виды
                                                                             .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                             .Cast<View>()                     //элементы категории Виды
                                                                             .ToList();                         //формируем список
            foreach (View view in views2)
            {
                if (view.Name == "TNov")
                {
                    tNovView = view; break;
                }
            }
            #endregion

            //Список категорий
            IList<BuiltInCategory> builtInCategoryList = categories();
            ICollection<ElementId> catIds = new List<ElementId>();
            foreach (BuiltInCategory category in builtInCategoryList)
            {
                ElementId catId = new ElementId((int)category);
                catIds.Add(catId);
            }
            int categoriesCount = catIds.Count;

            #region Проверка шаблона
            //проверяем шаблон вида на предмет отключенной галочки "Фильтры"

            ElementId elementId = new ElementId(-1006964); //id отключенной галочки Фильтры (получен опытным путем)
            ElementId templateId = tNovView.ViewTemplateId;
            View3D template = (View3D)doc.GetElement(templateId);
            ICollection<ElementId> elementIds = template.GetNonControlledTemplateParameterIds();
            bool filtersDisabled = elementIds.Contains(elementId);

            //получаем фильтры в шаблоне вида

            IList<ElementId> templatefilters = template.GetOrderedFilters();
            int tfCount = templatefilters.Count; //кол-во фильтров в шаблоне
            if (tfCount <1)
            {
                string mes = "В шаблоне вида О_3D_Вентиляция не хватает фильтров - проверьте фильтры в шаблоне вида.";
                var info = new InfoWindow280(mes); info.ShowDialog();
                Logger.Log(mes + ". Завершение работы.",3);
                return Result.Failed;
            }
            #endregion

            #region Сбор систем и фильтров
            //Ищем системы воздуховодов и фильтры по ним

            List<MechanicalSystem> systems = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DuctSystem)
                .WhereElementIsNotElementType()
                .Cast<MechanicalSystem>()
                .ToList(); Logger.Log("системы собраны",2);
            List<ParameterFilterElement> filters = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .ToList(); Logger.Log("фильтры собраны",2);
            List<string> filternames = new List<string>(); //имена необходимых фильтров
            foreach (MechanicalSystem system in systems)
            {
                string name = system.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM).AsValueString();
                filternames.Add(name);
            }
            Logger.Log("Системы: " + String.Join(", ", filternames),1);
            List<string> filternamestoadd = new List<string>(); //имена недостающих фильтров и фильтров для пересоздания
            List<ParameterFilterElement> filterstorestore = new List<ParameterFilterElement>();
            foreach (string filtername in filternames) //проверяем наличие фильтров, заполняем список имен недостающих фильтров
            {
                bool exist = false;
                //фильтр существует 
                
                
                foreach (ParameterFilterElement filter in filters)
                {
                    //if (filter.Name.Contains("TNov_Вент_не")) { filteridstorestore.Add(filter.Id); }
                    
                    string name = filter.Name.Replace(filterPrefix,"");
                    if (name == filtername) 
                    { 
                        exist = true;
                        //проверка фильтра по назначенному параметру
                        ElementId filterParamId = filter.GetElementFilterParameters().First();
                        if(filterParamId != systemnameparamId)
                        {
                            filterstorestore.Add(filter);
                        }
                    }
                    
                }

                if (!exist) //фильтра нет
                {
                    filternamestoadd.Add(filtername);
                }

                
            }
            if (filternamestoadd.Count > 0) { Logger.Log("Необходимо создать фильтры: " + String.Join(", ", filternamestoadd), 1); }
            else { Logger.Log("Необходимые фильтры уже созданы",1); }
            #endregion

            bool unhandledError = false;

            #region Основной код
            using (Transaction transaction = new Transaction(doc))
            {
                try { 
                transaction.Start("TNov - Схемы вентиляции");
                Logger.Log("Открываем транзакцию",1);

                //выключаем галочку Фильтры в шаблоне вида

                if (!filtersDisabled)
                {
                    elementIds.Add(elementId);
                    template.SetNonControlledTemplateParameterIds(elementIds);
                    Logger.Log("В шаблоне вида О_3D_Вентиляция не была отключена галочка для Фильтров. Отключили",1);
                }

                //исправляем нужные фильтры
                
                if (filterstorestore.Count > 0) 
                {
                    string errors = ""; int errorcount = 0;
                    foreach (ParameterFilterElement ftr in filterstorestore)
                    {
                        string name = ftr.Name.Replace(filterPrefix, "");
                        ElementFilter elementFilter1 = (ElementFilter)new ElementParameterFilter(ParameterFilterRuleFactory.CreateNotEqualsRule(systemnameparamId, name, true));
                        try
                        {
                            
                            ftr.ClearRules(); //удаляем все правила
                            ftr.SetElementFilter(elementFilter1); //назначаем правило
                        }
                        catch (Exception ex)
                        {
                            errorcount++; errors += ftr.Name + " ";
                            Logger.Log("Ошибка при исправлении фильтра " + ftr.Name + ": " + ex.Message,4);
                            continue;
                        }
                    }
                    if (errorcount > 0)
                    {
                        string mes = "Не удалось исправить фильтры:\n" + errors;
                        var info = new InfoWindow280(mes); info.ShowDialog();
                    }
                }
                

                //создаем недостающий фильтр либо выбираем существующий
                foreach (string fname in filternamestoadd)
                {
                    
                    ElementFilter elementFilter = (ElementFilter)new ElementParameterFilter(ParameterFilterRuleFactory.CreateNotEqualsRule(systemnameparamId, fname, true));
                    ParameterFilterElement parameterFilterElement;
                    try
                    {
                        parameterFilterElement = ParameterFilterElement.Create(doc, filterPrefix + fname, catIds, elementFilter);
                        Logger.Log("Фильтр создан: " + filterPrefix + fname,1);
                    }
                    catch (ArgumentException ex)
                    {
                        Logger.Log("Ошибка при создании фильтра: " + ex.Message,4);
                        //parameterFilterElement = ((IEnumerable<Element>)new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))).Where<Element>(new Func<Element, bool>(f=>f.Name==fname)).First<Element>() as ParameterFilterElement;
                        //parameterFilterElement.SetElementFilter(elementFilter);
                    }
                }

                //обновляем список фильтров
                List<ParameterFilterElement> filters1 = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .ToList();

                //создаем недостающие виды, проверяем существующие виды
                List<View> views1 = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Views)   //фильтр по категории Виды
                                                                         .WhereElementIsNotElementType()    //фильтр только экземпляры
                                                                         .Cast<View>()                     //элементы категории Виды
                                                                         .ToList();                         //формируем список
                List<string>names1 = new List<string>();//список систем, для которых виды нужно создать
                List<View> viewstorestore = new List<View>(); //виды на изменение
                
                foreach (string f in filternames)
                {
                    bool exist = false;
                    foreach (View view in views1)
                    {
                        if (view.Name.Contains(viewPrefix))
                        {
                            string viewname = view.Name.Replace(viewPrefix, ""); //Logger.Log("   "+viewname);
                            // проверяем корректность существующего вида 
                            if (viewname == f) //нашли нужный вид
                            {
                                exist = true;
                                IList<ElementId> flt = view.GetOrderedFilters(); //фильтры вида
                                int j = 0; //счетчик кол-ва фильтров
                                foreach (ElementId el in flt)
                                {
                                    //проходим по фильтрам вида, если все нужные id есть - ок
                                    foreach(ElementId tfId in templatefilters) //фильтры из шаблона
                                    {
                                        if (el == tfId) { j++; }
                                    }
                                    foreach (ParameterFilterElement f1 in filters1) //фильтр по системе
                                    {
                                        string f1Name = f1.Name.Replace(filterPrefix, "");
                                        if (f == f1Name && el == f1.Id) { j++; }
                                    }
                                }
                                if (j < tfCount + 1) 
                                {
                                    viewstorestore.Add(view); //фильтров не хватает - поправим
                                    break;
                                }
                            }
                        }
                    }
                    if (!exist) { names1.Add(f); } //вид не найден - создадим
                }
                names1.Distinct();
                if (names1.Count > 0) { Logger.Log("Необходимо создать виды для систем: " + String.Join(", ", names1), 1); }
                else { Logger.Log("Необходимые виды уже созданы", 1); }

                //исправляем нужные виды

                if (viewstorestore.Count > 0) 
                {
                    string errors = ""; int errorcount = 0;
                    foreach (View v in viewstorestore)
                    {
                        string name = v.Name.Replace(viewPrefix, "");
                        List<ElementId> filtersToAdd = new List<ElementId>(); //коллекция фильтров на добавление
                        foreach (ElementId tfId in templatefilters) //фильтры из шаблона
                        {
                            filtersToAdd.Add(tfId);
                        }
                        foreach (ParameterFilterElement f1 in filters1) //фильтр по системе
                        {
                            string f1Name = f1.Name.Replace(filterPrefix, "");
                            if (name == f1Name) { filtersToAdd.Add(f1.Id); }
                        }

                        try
                        {
                            IList<ElementId> vFilters = v.GetOrderedFilters();
                            if(vFilters.Count > 0)
                            {
                                foreach (ElementId vF in vFilters) //удаляем все фильтры
                                {
                                    v.RemoveFilter(vF);
                                }
                            }
                            foreach(ElementId fTA in filtersToAdd)
                            {
                                v.AddFilter(fTA);
                                v.SetFilterVisibility(fTA, false);
                            }
                        }
                        catch (Exception ex)
                        {
                            errorcount++; errors += v.Name + " ";
                            Logger.Log("Ошибка при исправлении вида "+v.Name+": " + ex.Message,4);
                            continue;
                        }
                    }
                    if (errorcount > 0) 
                    {
                        string mes = "Не удалось исправить виды:\n"+errors+"\nЗакройте виды, созданные при помощи плагина.";
                        var info = new InfoWindow280(mes); info.ShowDialog();
                    }
                }

                //создаем виды
                ElementId filter = filters1.First().Id;
                foreach (string name in names1)
                {
                    foreach(ParameterFilterElement f1 in filters1)
                    {
                        string fname = f1.Name.Replace("TNov_Вент_не ", "");
                        if (fname == name) { filter = f1.Id; break; }
                    }
                    try
                    {
                        //создаем вид
                        View newview = CreateViewCopy(tNovView);
                        newview.Name = viewPrefix + name;
                        Logger.Log("Вид создан: " + viewPrefix + name,1);
                        //назначаем виду фильтр по системе
                        newview.AddFilter(filter);
                        //выключаем фильтр по системе
                        newview.SetFilterVisibility(filter, false);
                        //назначаем фильтры из шаблона вида
                        foreach(ElementId tf in templatefilters)
                        {
                            newview.AddFilter(tf);
                            newview.SetFilterVisibility(tf, false);
                        }
                    }
                    catch (Exception ex) { Logger.Log("Ошибка при создании вида: " + ex.Message, 4); }
                }


                transaction.Commit();
                Logger.Log("Закрываем транзакцию", 1);
                }
                catch (Exception ex)
                {
                    Logger.Log("Ошибка: " + ex.Message, 4);
                    new InfoWindow280("Ошибка: " + ex.Message).ShowDialog();
                    unhandledError = true;
                }
            }
            #endregion
            if (unhandledError)
            {
                Logger.Log("Завершение работы с ошибками.", 4);
                return Result.Succeeded;
            }
            Logger.Log("Завершение работы.", 5);
            return Result.Succeeded;
        }
        private IList<BuiltInCategory> categories()
        {
              List<BuiltInCategory> builtInCategoryList = new List<BuiltInCategory>();
              builtInCategoryList.Add((BuiltInCategory) (-2008016));
              builtInCategoryList.Add((BuiltInCategory) (-2008000));
              builtInCategoryList.Add((BuiltInCategory) (-2008010));
              builtInCategoryList.Add((BuiltInCategory) (-2008123));
              builtInCategoryList.Add((BuiltInCategory) (-2008013));
              builtInCategoryList.Add((BuiltInCategory) (-2008020));
              builtInCategoryList.Add((BuiltInCategory) (-2008160));
              builtInCategoryList.Add((BuiltInCategory) (-2001140));
              return (IList<BuiltInCategory>) builtInCategoryList;
        }
        private View CreateViewCopy(View view)
        {
            View newView = null;
            ElementId newViewId = ElementId.InvalidElementId;
            if (view.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
            {
                newViewId = view.Duplicate(ViewDuplicateOption.Duplicate);
                newView = view.Document.GetElement(newViewId) as View;
            }

            return newView;
        }
    }
}
