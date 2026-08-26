using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Тесты расчёта теплопотерь (папка Heatloss) проверяют в том числе внутренние
// методы: воздухообмен, разбор номера квартиры, интерполяцию каталога СП 230,
// распознавание узлов. Это отдельный консольный проект QOVETER.Tests со 125
// проверками; без этого атрибута он не собирается вовсе. Раньше он ссылался
// на отдельную сборку QOVETER.dll, где атрибут и стоял, — после переезда
// расчёта сюда ссылаться ему больше не на что.
[assembly: InternalsVisibleTo("QOVETER.Tests")]

// Общие сведения об этой сборке предоставляются следующим набором
// набора атрибутов. Измените значения этих атрибутов для изменения сведений,
// связанные со сборкой.
[assembly: AssemblyTitle("TNovVent")]
[assembly: AssemblyDescription("TNov. Вентиляция")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Новация")]
[assembly: AssemblyProduct("TNov. Вентиляция")]
[assembly: AssemblyCopyright("Copyright © ПМ Новация 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Установка значения False для параметра ComVisible делает типы в этой сборке невидимыми
// для компонентов COM. Если необходимо обратиться к типу в этой сборке через
// COM, задайте атрибуту ComVisible значение TRUE для этого типа.
[assembly: ComVisible(false)]

// Следующий GUID служит для идентификации библиотеки типов, если этот проект будет видимым для COM
[assembly: Guid("eda54d6b-e20d-410b-a88f-2b02194d9a54")]

// Версия сборки задаётся в Properties\VersionInfo.cs
// (GenerateAssemblyInfo=false — поля «Версия сборки» в .csproj игнорируются)
