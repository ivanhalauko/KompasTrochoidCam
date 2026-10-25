using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using KompasTrochoidCam.Models;

namespace KompasTrochoidCam.Services
{
    /// <summary>
    /// Подключение к запущенному КОМПАС-3D по COM. Подключение идёт через приложение
    /// API 7 (у него зарегистрирован ProgID), а геометрия читается по API 5
    /// (KompasObject), где все интерфейсы Automation-совместимые и вызываются штатным
    /// поздним связыванием через IDispatch. Ссылки на библиотеки КОМПАС при сборке
    /// не требуются.
    /// </summary>
    public class KompasService
    {
        private object? _kompasApp;
        private object? _kompas5;
        private object? _activeDocument;

        // Ошибки, которые возвращает GetActiveObject.
        private const int MkEUnavailable = unchecked((int)0x800401E3);
        private const int CoEClassString = unchecked((int)0x800401F3);
        private const int RegDbEClassNotReg = unchecked((int)0x80040154);

        /// <summary>
        /// ProgID, под которыми регистрируется КОМПАС-3D. Официальный ProgID приложения —
        /// «KOMPAS.Application.7» (см. SDK: IApplication); ряд версий регистрирует также API 5.
        /// </summary>
        private static readonly string[] KompasProgIds =
        {
            "KOMPAS.Application.7",
            "KOMPAS.Application.6",
            "KOMPAS.Application.5",
            "KOMPAS.Application"
        };

        /// <summary>Подробный отчёт о последней попытке подключения — для окна диагностики.</summary>
        public string LastDiagnostics { get; private set; } = string.Empty;

        // Нативная функция Windows: берёт уже запущенный объект из Running Object Table.
        [DllImport("oleaut32.dll", PreserveSig = true)]
        private static extern int GetActiveObject(ref Guid rclsid, IntPtr pReserved, [MarshalAs(UnmanagedType.Interface)] out object? ppunk);

        [DllImport("ole32.dll")]
        private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable? pprot);

        public bool IsConnected => _kompasApp != null;

        public string ActiveDocumentName => GetActiveDocName();

        private object? GetProperty(object? target, string propertyName, object[]? args = null)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().InvokeMember(
                    propertyName,
                    BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    args);
            }
            catch
            {
                return null;
            }
        }

        private static object? GetItem(object? collection, int index)
        {
            if (collection == null) return null;
            try
            {
                return collection.GetType().InvokeMember(
                    "Item",
                    BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    collection,
                    new object[] { index });
            }
            catch
            {
                return null;
            }
        }

        private static int GetCount(object? collection)
        {
            if (collection == null) return 0;
            try
            {
                object? count = collection.GetType().InvokeMember(
                    "Count",
                    BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    collection,
                    null);
                return count == null ? 0 : Convert.ToInt32(count, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static double ToDouble(object? value)
        {
            if (value == null) return 0;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static int ToInt(object? value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static TrochoidEngine.Point2D ToPoint2D(object? point)
            => new TrochoidEngine.Point2D(ToDouble(GetMember(point, "X")), ToDouble(GetMember(point, "Y")));

        private static object? GetMember(object? target, string name)
        {
            if (target == null) return null;
            try
            {
                return target.GetType().InvokeMember(
                    name,
                    BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Подключается к запущенному КОМПАС-3D.
        /// CLSID не зашит жёстко: он берётся из ProgID через реестр COM, потому что GUID
        /// зависит от версии и всегда был главной причиной ошибки MK_E_UNAVAILABLE.
        /// </summary>
        public bool Connect(out string errorMessage)
        {
            _kompasApp = null;
            _kompas5 = null;
            _activeDocument = null;

            var log = new List<string> { "=== Подключение к КОМПАС-3D ===", $"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
            var attempts = new List<string>();

            var registered = new List<(string ProgId, Guid Clsid)>();
            foreach (string progId in KompasProgIds)
            {
                Guid? clsid = ResolveClsidFromProgId(progId);
                if (clsid.HasValue) registered.Add((progId, clsid.Value));
            }

            log.Add(registered.Count > 0
                ? "Реестр COM: " + string.Join("; ", registered.Select(r => $"{r.ProgId} = {{{r.Clsid}}}"))
                : "Реестр COM: ProgID КОМПАС не найден (проверены KOMPAS.Application.7/.6/.5).");

            var processes = GetKompasProcesses();
            log.Add(processes.Count > 0
                ? "Процессы КОМПАС: " + string.Join(", ", processes)
                : "Процессы КОМПАС: не запущены.");

            log.Add($"Наше приложение: PID {Environment.ProcessId}, {RuntimeInformation.ProcessArchitecture}, " +
                    $"сессия {CurrentSessionId()}, " +
                    (Environment.IsPrivilegedProcess ? "права администратора" : "обычные права"));

            object? attached = null;
            string attachedVia = string.Empty;
            int lastHresult = 0;

            // --- Попытка 1: штатный путь «ProgID → CLSID → GetActiveObject». ---
            foreach (var (progId, clsid) in registered)
            {
                int hr = TryGetActiveObject(clsid, out object? instance);
                lastHresult = hr;

                if (hr == 0 && instance != null)
                {
                    object? application = ResolveApplicationObject(instance, out string via);
                    if (application != null)
                    {
                        attached = application;
                        attachedVia = $"{progId}, {via}";
                        break;
                    }

                    attempts.Add($"{progId}: объект получен, но приложение КОМПАС не опознано");
                }
                else
                {
                    attempts.Add($"{progId}: GetActiveObject = 0x{hr:X8} ({DescribeHresult(hr)})");
                }
            }

            // --- Попытка 2: перебор объектов в Running Object Table. ---
            if (attached == null)
            {
                var running = EnumerateRunningObjects();
                log.Add(running.Count > 0
                    ? $"ROT: объектов {running.Count}: " + string.Join("; ", running.Take(10).Select(FormatRotEntry))
                    : "ROT: пусто.");

                foreach (var entry in running)
                {
                    object? application = ResolveApplicationObject(entry.Instance, out string via);
                    if (application == null) continue;

                    // В ROT живут и чужие приложения (Word, Excel). Свой объект берём только если
                    // его CLSID совпал с зарегистрированным ProgID КОМПАС либо сработал
                    // специфичный для API 5 метод ksGetApplication7.
                    bool knownClsid = entry.Clsid.HasValue &&
                                      registered.Any(r => r.Clsid == entry.Clsid.Value);
                    bool kompasMarker = via == Api5ApplicationVia;

                    if (!knownClsid && !kompasMarker) continue;

                    attached = application;
                    attachedVia = $"ROT: {FormatRotEntry(entry)}, {via}";
                    break;
                }
            }

            if (attached == null)
            {
                log.Add("Результат: подключиться не удалось.");
                log.AddRange(attempts.Select(a => "  • " + a));
                log.Add(string.Empty);
                log.Add(BuildRecommendations(registered.Count, processes.Count));
                LastDiagnostics = string.Join(Environment.NewLine, log);

                errorMessage = BuildConnectFailure(registered.Count, processes.Count, lastHresult);
                return false;
            }

            _kompasApp = attached;
            _activeDocument = GetProperty(_kompasApp, "ActiveDocument");

            log.AddRange(attempts.Select(a => "  • " + a));
            log.Add($"Результат: подключено ({attachedVia}).");
            log.Add("Активный документ: " + (_activeDocument != null ? GetActiveDocName() : "не открыт"));
            LastDiagnostics = string.Join(Environment.NewLine, log);

            errorMessage = _activeDocument != null
                ? $"Подключено к КОМПАС-3D · документ: {GetActiveDocName()}"
                : "Подключено к КОМПАС-3D, но активный документ не открыт — откройте деталь .m3d и нажмите «Обновить геометрию»";
            return true;
        }

        private static int CurrentSessionId()
        {
            using var process = Process.GetCurrentProcess();
            return process.SessionId;
        }

        private const string SelfObjectVia = "сам объект";
        private const string Api5ApplicationVia = "метод ksGetApplication7";

        /// <summary>CLSID класса по ProgID — читается из реестра COM (HKCR), ничего не зашито.</summary>
        private static Guid? ResolveClsidFromProgId(string progId)
        {
            try
            {
                Type? type = Type.GetTypeFromProgID(progId, throwOnError: false);
                if (type == null || type.GUID == Guid.Empty) return null;
                return type.GUID;
            }
            catch
            {
                return null;
            }
        }

        private static int TryGetActiveObject(Guid clsid, out object? instance)
        {
            instance = null;
            try
            {
                return GetActiveObject(ref clsid, IntPtr.Zero, out instance);
            }
            catch (Exception ex)
            {
                return ex is COMException com ? com.HResult : unchecked((int)0x80004005);
            }
        }

        private static string DescribeHresult(int hr) => hr switch
        {
            0 => "успешно",
            MkEUnavailable => "объект не найден в Running Object Table (MK_E_UNAVAILABLE)",
            CoEClassString => "некорректный ProgID (CO_E_CLASSSTRING)",
            RegDbEClassNotReg => "CLSID не зарегистрирован (REGDB_E_CLASSNOTREG)",
            _ => "ошибка COM"
        };

        /// <summary>Проверяет, является ли объект приложением КОМПАС, и возвращает интерфейс приложения.</summary>
        private object? ResolveApplicationObject(object? candidate, out string via)
        {
            via = string.Empty;
            if (candidate == null) return null;

            object? application = GetProperty(candidate, "Application");
            if (LooksLikeApplication(application))
            {
                via = "свойство Application";
                return application;
            }

            object? api7 = InvokeMethod(candidate, "ksGetApplication7");
            if (LooksLikeApplication(api7))
            {
                via = Api5ApplicationVia;
                return api7;
            }

            if (LooksLikeApplication(candidate))
            {
                via = SelfObjectVia;
                return candidate;
            }

            return null;
        }

        private static bool LooksLikeApplication(object? candidate)
            => TryGetProperty(candidate, "ActiveDocument", out _)
               || TryGetProperty(candidate, "Documents", out _);

        private static bool TryGetProperty(object? target, string name, out object? value)
        {
            value = null;
            if (target == null) return false;

            try
            {
                value = target.GetType().InvokeMember(
                    name,
                    BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static object? InvokeMethod(object? target, string methodName)
        {
            if (target == null) return null;

            try
            {
                return target.GetType().InvokeMember(
                    methodName,
                    BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Список объектов, зарегистрированных в Running Object Table.</summary>
        private static List<(string DisplayName, Guid? Clsid, object? Instance)> EnumerateRunningObjects()
        {
            var entries = new List<(string DisplayName, Guid? Clsid, object? Instance)>();

            try
            {
                if (GetRunningObjectTable(0, out IRunningObjectTable? rot) != 0 || rot == null) return entries;

                rot.EnumRunning(out IEnumMoniker enumMoniker);
                if (enumMoniker == null) return entries;

                IntPtr fetched = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    var monikers = new IMoniker[1];
                    while (enumMoniker.Next(1, monikers, fetched) == 0 && Marshal.ReadInt32(fetched) == 1)
                    {
                        IMoniker? moniker = monikers[0];
                        monikers[0] = null!;
                        if (moniker == null) continue;

                        string display = string.Empty;
                        try { moniker.GetDisplayName(null!, null!, out display); } catch { /* необязательно */ }

                        object? instance = null;
                        try
                        {
                            if (rot.GetObject(moniker, out object? bound) == 0) instance = bound;
                        }
                        catch
                        {
                            // объект мог уже завершиться
                        }

                        entries.Add((display ?? string.Empty, ParseClassClsid(display), instance));
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(fetched);
                }
            }
            catch
            {
                // Диагностика не должна мешать подключению.
            }

            return entries;
        }

        private static string FormatRotEntry((string DisplayName, Guid? Clsid, object? Instance) entry)
            => string.IsNullOrWhiteSpace(entry.DisplayName) ? "(без имени)" : entry.DisplayName;

        /// <summary>Класс-моникер в ROT выглядит как «!{CLSID}».</summary>
        private static Guid? ParseClassClsid(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return null;

            string name = displayName.Trim();
            if (!name.StartsWith("!", StringComparison.Ordinal)) return null;

            name = name.Substring(1).Trim();
            if (name.StartsWith("{", StringComparison.Ordinal) && name.EndsWith("}", StringComparison.Ordinal))
                name = name.Substring(1, name.Length - 2);

            return Guid.TryParse(name, out Guid clsid) ? clsid : null;
        }

        private static List<string> GetKompasProcesses()
        {
            var result = new List<string>();

            try
            {
                foreach (Process process in Process.GetProcesses())
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (!name.StartsWith("kompas", StringComparison.OrdinalIgnoreCase) &&
                            !name.StartsWith("ksinvisible", StringComparison.OrdinalIgnoreCase))
                            continue;

                        result.Add($"{name}.exe (PID {process.Id})");
                    }
                    catch
                    {
                        // защищённые процессы недоступны
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // перечисление процессов недоступно — не критично
            }

            return result;
        }

        private static string BuildConnectFailure(int registeredCount, int processCount, int lastHresult)
        {
            if (registeredCount == 0)
            {
                return "КОМПАС-3D не зарегистрирован в COM: не найден ProgID KOMPAS.Application.7. " +
                       "Проверьте установку, а если КОМПАС 32-битный — соберите приложение под x86. Подробности — кнопка «Диагностика».";
            }

            if (processCount == 0)
            {
                return "КОМПАС-3D не запущен. Запустите программу, откройте деталь .m3d и повторите подключение.";
            }

            string hint = Environment.IsPrivilegedProcess
                ? "Приложение запущено от администратора, а КОМПАС — с обычными правами. Запустите оба одинаково."
                : "Если КОМПАС запущен от администратора, а приложение — нет, уровни прав нужно уравнять.";

            return $"КОМПАС-3D запущен, но COM-объект недоступен (0x{lastHresult:X8}). {hint} " +
                   "Подробности — кнопка «Диагностика».";
        }

        private static string BuildRecommendations(int registeredCount, int processCount)
        {
            var lines = new List<string> { "Что проверить:" };

            if (registeredCount == 0)
            {
                lines.Add("1. КОМПАС-3D установлен и зарегистрирован в COM.");
                lines.Add("1a. Разрядность: 32-битный КОМПАС регистрирует COM только в WOW6432Node и не виден 64-битному приложению — соберите проект под x86.");
            }

            if (processCount == 0)
                lines.Add("1. КОМПАС-3D запущен и в нём открыта деталь .m3d.");

            lines.Add("2. Приложение и КОМПАС запущены с одинаковыми правами (оба от администратора либо оба без).");
            lines.Add("3. Оба работают в одной сессии Windows под одним пользователем.");
            lines.Add("4. Антивирус не блокирует межпроцессное взаимодействие.");

            return string.Join(Environment.NewLine, lines);
        }

        public string GetActiveDocName()
        {
            if (_activeDocument == null) return "Нет подключения";
            try
            {
                var nameObj = GetProperty(_activeDocument, "Name");
                return nameObj?.ToString() ?? "Без имени";
            }
            catch
            {
                return "Ошибка чтения имени";
            }
        }

        /// <summary>Перечитывает указатель на активный документ (пользователь мог переключить файл).</summary>
        private void RefreshActiveDocument()
        {
            if (_kompasApp == null) return;
            var document = GetProperty(_kompasApp, "ActiveDocument");
            if (document != null) _activeDocument = document;
        }

        // Тип объектов 3D-документа (ksObj3dTypeEnum): 6 — грань.
        private const short Obj3dFace = 6;

        /// <summary>Индекс верхнего компонента детали (pTop_Part) в API 5.</summary>
        private const short TopPartIndex = -1;

        /// <summary>Разброс Z, при котором грань считается горизонтальной, мм.</summary>
        private const double HorizontalTolerance = 0.05;

        /// <summary>Шаг полигона при разложении кривых рёбер, мм.</summary>
        private const double CurveStepMm = 0.2;

        /// <summary>Точка в пространстве детали (внутреннее представление при чтении из КОМПАС).</summary>
        private readonly struct Point3
        {
            public Point3(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public double X { get; }
            public double Y { get; }
            public double Z { get; }
        }

        /// <summary>
        /// Приложение КОМПАС в терминах API 5 (KompasObject): ActiveDocument3D, GetParamStruct,
        /// Document3D и т.д. Все интерфейсы этого API — Automation-совместимые
        /// (ksPart, ksBody, ksFaceDefinition, ksLoop, ksEdgeDefinition, ksCurve3D),
        /// поэтому вызываются обычным поздним связыванием через IDispatch.
        /// </summary>
        private object? ResolveApi5(out string message)
        {
            if (_kompas5 != null)
            {
                message = "API 5: уже подключено";
                return _kompas5;
            }

            var notes = new List<string>();
            var candidates = new List<(object Instance, string Source)>();

            // Уже подключённое приложение API 7: часть версий отдаёт через него и API 5.
            if (_kompasApp != null)
                candidates.Add((_kompasApp, "приложение из подключения"));

            // Отдельное подключение по ProgID API 5 — штатный путь для API 5
            // (в v19-v22 ProgID «KOMPAS.Application.5» зарегистрирован).
            foreach (string progId in new[] { "KOMPAS.Application.5", "KOMPAS.Application" })
            {
                Guid? clsid = ResolveClsidFromProgId(progId);
                if (clsid == null)
                {
                    notes.Add($"{progId}: нет в реестре COM");
                    continue;
                }

                int hr = TryGetActiveObject(clsid.Value, out object? instance);
                if (hr != 0 || instance == null)
                {
                    notes.Add($"{progId}: GetActiveObject = 0x{hr:X8} ({DescribeHresult(hr)})");
                    continue;
                }

                candidates.Add((instance, progId));
                notes.Add($"{progId}: объект получен");
            }

            object? fallback = null;
            string fallbackSource = string.Empty;

            foreach (var (instance, source) in candidates)
            {
                object? api5 = ProbeApi5(instance, notes, out bool verified);
                if (api5 == null) continue;

                if (verified)
                {
                    _kompas5 = api5;
                    message = $"API 5: {source} — " + string.Join("; ", notes);
                    return _kompas5;
                }

                if (fallback == null)
                {
                    fallback = api5;
                    fallbackSource = source;
                }
            }

            // Ни один кандидат не подтвердил API 5 методом ActiveDocument3D:
            // берём первый для чтения, но не запоминаем — при следующем обновлении
            // проверим заново (документ могли открыть уже после подключения).
            if (fallback != null)
            {
                message = $"API 5: {fallbackSource} (без подтверждения) — " + string.Join("; ", notes);
                return fallback;
            }

            message = "API 5 недоступен — " + string.Join("; ", notes);
            return null;
        }

        /// <summary>
        /// Проверяет, отдаёт ли объект интерфейс приложения API 5. Признак API 5 —
        /// работающий метод ActiveDocument3D/ActiveDocument2D (в API 7 их нет).
        /// </summary>
        private static object? ProbeApi5(object instance, List<string> notes, out bool verified)
        {
            verified = false;

            // CastTo("KompasObject") поддерживается не всеми версиями — пробуем и уходим дальше.
            foreach (string typeName in new[] { "KompasObject", "API5::KompasObject" })
            {
                object? cast = Call(instance, "CastTo", typeName);
                if (cast == null || ReferenceEquals(cast, instance)) continue;

                notes.Add($"CastTo(\"{typeName}\"): ок");
                verified = HasActiveDocument(cast);
                return cast;
            }

            if (HasActiveDocument(instance))
            {
                notes.Add("ActiveDocument3D: доступен");
                verified = true;
                return instance;
            }

            notes.Add("ActiveDocument3D: недоступен");
            return instance;
        }

        private static bool HasActiveDocument(object application)
            => Call(application, "ActiveDocument3D") != null
               || Call(application, "ActiveDocument2D") != null;

        /// <summary>
        /// Читает геометрию активной детали через API 5:
        /// ActiveDocument3D → GetPart(pTop_Part) → BodyCollection → FaceCollection
        /// → ksFaceDefinition::LoopCollection → ksLoop::EdgeCollection
        /// → ksEdgeDefinition::GetCurve3D → ksCurve3D::CalculatePolygon.
        /// Каждый вызов изолирован: сбой одного элемента не прерывает чтение модели.
        /// </summary>
        public PartGeometry ReadPartGeometry(out string message)
        {
            var geometry = new PartGeometry
            {
                LoadedAt = DateTime.Now,
                Source = "kompas"
            };

            var log = new List<string> { "=== Чтение геометрии (API 5) ===" };

            object? api5 = null;
            string apiMessage = "API 5: не инициализировано";
            try
            {
                api5 = ResolveApi5(out apiMessage);
            }
            catch (Exception ex)
            {
                apiMessage = "API 5: ошибка " + DescribeException(ex);
            }

            log.Add(apiMessage);

            if (api5 == null)
            {
                AppendDiagnostics(log);
                message = "Нет доступа к API 5 КОМПАС-3D. Запустите КОМПАС-3D, откройте деталь .m3d и нажмите «Обновить геометрию».";
                return geometry;
            }

            try
            {
                object? document3D = Call(api5, "ActiveDocument3D");
                if (document3D == null)
                {
                    AppendDiagnostics(log);
                    message = "Активный документ КОМПАС не является 3D-моделью — сделайте активным окно детали .m3d и нажмите «Обновить геометрию»";
                    return geometry;
                }

                geometry.DocumentName = GetActiveDocName();
                log.Add($"Документ: {geometry.DocumentName}");

                object? part = ResolvePart(document3D, log);
                if (part == null)
                {
                    AppendDiagnostics(log);
                    message = "Не удалось получить компонент детали (ksDocument3D.GetPart)";
                    return geometry;
                }

                var faceObjects = GetFaceObjects(part, log);
                log.Add($"Граней в модели: {faceObjects.Count}");

                if (faceObjects.Count == 0)
                {
                    AppendDiagnostics(log);
                    message = "В документе не найдено тел с гранями — проверьте, что деталь содержит твёрдое тело";
                    return geometry;
                }

                var edges = new Dictionary<string, PartEdge>(StringComparer.Ordinal);
                int faceId = 0;
                int withoutLoops = 0;
                int notHorizontal = 0;

                foreach (object faceObject in faceObjects)
                {
                    PartFace? face = ReadHorizontalFace(faceObject, ++faceId, edges, out bool hasLoops);
                    if (face != null)
                    {
                        geometry.Faces.Add(face);
                        continue;
                    }

                    if (hasLoops) notHorizontal++;
                    else withoutLoops++;
                }

                log.Add($"Пропущено граней: без циклов {withoutLoops}, не горизонтальных {notHorizontal}");

                foreach (PartEdge edge in edges.Values)
                {
                    edge.Id = geometry.Edges.Count + 1;
                    geometry.Edges.Add(edge);
                }

                log.Add($"Горизонтальных граней: {geometry.Faces.Count}; рёбер: {geometry.Edges.Count}");

                geometry.RecalculateBounds();
                if (!geometry.IsEmpty) log.Add($"Габариты детали: {geometry.SizeText}");

                AppendDiagnostics(log);

                message = geometry.IsEmpty
                    ? "В активном документе не найдено горизонтальных граней — проверьте, что деталь содержит тело, и нажмите «Диагностику»"
                    : $"Геометрия загружена: граней {geometry.Faces.Count}, рёбер {geometry.Edges.Count}";

                return geometry;
            }
            catch (Exception ex)
            {
                log.Add("Ошибка: " + DescribeException(ex));
                AppendDiagnostics(log);
                message = $"Ошибка чтения геометрии: {DescribeException(ex)}";
                return geometry;
            }
        }

        private void AppendDiagnostics(List<string> lines)
        {
            string text = string.Join(Environment.NewLine, lines);
            string combined = string.IsNullOrEmpty(LastDiagnostics)
                ? text
                : LastDiagnostics + Environment.NewLine + Environment.NewLine + text;

            // Отчёт читают глазами: старые записи обрезаем, чтобы файл не разрастался.
            const int limit = 40000;
            LastDiagnostics = combined.Length <= limit ? combined : combined[^limit..];
        }

        /// <summary>
        /// Компонент детали. Штатный индекс — pTop_Part (-1); для одиночной детали
        /// часть версий отдаёт компонент по номеру 0 или 1. Выбирается тот, у которого
        /// есть тела.
        /// </summary>
        private static object? ResolvePart(object document3D, List<string> log)
        {
            object? first = null;

            foreach (int index in new[] { (int)TopPartIndex, 0, 1 })
            {
                object? part = Call(document3D, "GetPart", index)
                               ?? Call(document3D, "GetPart", (short)index);

                if (part == null)
                {
                    log.Add($"GetPart({index}): пусто");
                    continue;
                }

                int bodies = CollectionItems(Call(part, "BodyCollection")).Count;
                log.Add($"GetPart({index}): компонент получен, тел {bodies}");

                if (bodies > 0)
                {
                    log.Add($"TopPart: выбран GetPart({index})");
                    return part;
                }

                first ??= part;
            }

            if (first != null) log.Add("TopPart: тела не найдены, взят первый полученный компонент");
            return first;
        }

        /// <summary>
        /// Грани детали. Основной путь — BodyCollection → ksBody::FaceCollection;
        /// запасные — GetMainBody() и EntityCollection(o3d_face).
        /// </summary>
        private static List<object> GetFaceObjects(object part, List<string> log)
        {
            var faces = new List<object>();

            var bodies = CollectionItems(Call(part, "BodyCollection"));
            log.Add($"BodyCollection: {bodies.Count} тел");

            foreach (object body in bodies)
                faces.AddRange(CollectionItems(Call(body, "FaceCollection")));

            if (faces.Count == 0)
            {
                var mainBodyFaces = CollectionItems(Call(Call(part, "GetMainBody"), "FaceCollection"));
                if (mainBodyFaces.Count > 0) log.Add($"GetMainBody: {mainBodyFaces.Count} граней");
                faces.AddRange(mainBodyFaces);
            }

            if (faces.Count == 0)
            {
                var entities = CollectionItems(
                    Call(part, "EntityCollection", Obj3dFace)
                    ?? Call(part, "EntityCollection", (int)Obj3dFace));

                log.Add($"EntityCollection(o3d_face): {entities.Count} объектов");

                foreach (object entity in entities)
                {
                    object? definition = Call(entity, "GetDefinition");
                    faces.Add(definition ?? entity);
                }
            }

            return faces;
        }

        /// <summary>Элементы automation-коллекции API 5 (GetCount / GetByIndex).</summary>
        private static List<object> CollectionItems(object? collection)
        {
            var items = new List<object>();
            if (collection == null) return items;

            int count = ToInt(Call(collection, "GetCount"));
            for (int i = 0; i < count; i++)
            {
                object? item = Call(collection, "GetByIndex", i)
                               ?? Call(collection, "GetByIndex", (short)i);
                if (item != null) items.Add(item);
            }

            return items;
        }

        /// <summary>
        /// Читает плоскую горизонтальную грань. Наклонные и вертикальные грани отбрасываются:
        /// для 2.5D-фрезерования по Z-уровням нужны только плоскости XY.
        /// </summary>
        private static PartFace? ReadHorizontalFace(
            object faceDefinition,
            int id,
            Dictionary<string, PartEdge> edges,
            out bool hasLoops)
        {
            var loopObjects = CollectionItems(Call(faceDefinition, "LoopCollection"));
            hasLoops = loopObjects.Count > 0;
            if (!hasLoops) return null;

            var face = new PartFace { Id = id, Name = $"Грань #{id}" };
            double minZ = double.MaxValue;
            double maxZ = double.MinValue;
            double zSum = 0;
            int zCount = 0;

            var loops = new List<(List<TrochoidEngine.Point2D> Points, double Area, bool IsOuter)>();

            foreach (object loopObject in loopObjects)
            {
                bool isOuter = ToBool(Call(loopObject, "IsOuter"), false);

                // ksLoop::EdgeCollection — рёбра в порядке обхода;
                // OrientedEdgeCollection — запасной вариант.
                var edgeObjects = CollectionItems(Call(loopObject, "EdgeCollection"));
                if (edgeObjects.Count == 0)
                    edgeObjects = CollectionItems(Call(loopObject, "OrientedEdgeCollection"));

                var pieces = new List<List<Point3>>();
                foreach (object edgeObject in edgeObjects)
                {
                    List<Point3> piece = ReadEdgePoints(edgeObject);
                    if (piece.Count < 2) continue;

                    pieces.Add(piece);
                    RememberEdge(edges, piece, id);
                }

                List<Point3> chained = ChainPieces(pieces);
                if (chained.Count < 3) continue;

                var flat = new List<TrochoidEngine.Point2D>(chained.Count);
                foreach (Point3 point in chained)
                {
                    flat.Add(new TrochoidEngine.Point2D(point.X, point.Y));
                    minZ = Math.Min(minZ, point.Z);
                    maxZ = Math.Max(maxZ, point.Z);
                    zSum += point.Z;
                    zCount++;
                }

                var normalized = GeometryMath.NormalizeLoop(flat);
                if (normalized.Count < 3) continue;

                loops.Add((normalized, GeometryMath.Area(normalized), isOuter));
            }

            if (loops.Count == 0 || zCount == 0) return null;

            // Все вершины грани должны лежать в одной плоскости XY.
            if (maxZ - minZ > HorizontalTolerance) return null;

            // Внешний цикл — по признаку IsOuter; если система его не дала, берём наибольший по площади.
            int outerIndex = loops.FindIndex(loop => loop.IsOuter);
            if (outerIndex < 0)
                outerIndex = loops.IndexOf(loops.OrderByDescending(loop => loop.Area).First());

            for (int i = 0; i < loops.Count; i++)
                face.Loops.Add(new PartLoop { Points = loops[i].Points, IsOuter = i == outerIndex });

            face.Z = zSum / zCount;
            face.NormalZ = GeometryMath.SignedArea(face.Loops[outerIndex].Points) < 0 ? -1.0 : 1.0;
            face.NormalX = 0;
            face.NormalY = 0;
            face.Area = GeometryMath.FaceArea(face);

            string suffix = face.NormalZ > 0 ? "верх" : "низ";
            face.Name = $"Грань #{id} · Z={face.Z:F2} ({suffix})";

            return face;
        }

        /// <summary>
        /// Точки ребра. Основной путь — GetCurve3D().CalculatePolygon(шаг);
        /// запасной — две вершины ребра (прямая).
        /// </summary>
        private static List<Point3> ReadEdgePoints(object edgeObject)
        {
            object? curve = Call(edgeObject, "GetCurve3D");

            if (curve == null)
            {
                // У ориентированного ребра кривая лежит на вложенном ребре.
                object? nested = Call(edgeObject, "GetEdge") ?? Call(edgeObject, "Edge");
                curve = Call(nested, "GetCurve3D")
                        ?? Call(Call(nested, "GetDefinition"), "GetCurve3D");
            }

            var points = ReadCurvePoints(curve);
            if (points.Count >= 2) return points;

            points.Clear();
            foreach (bool start in new[] { true, false })
            {
                object? vertex = Call(edgeObject, "GetVertex", start);
                Point3? point = ReadPoint(vertex) ?? ReadPoint(Call(vertex, "GetPoint"));
                if (point != null) points.Add(point.Value);
            }

            return points;
        }

        /// <summary>Точки кривой ребра: ksCurve3D::CalculatePolygon отдаёт плоский массив x,y,z.</summary>
        private static List<Point3> ReadCurvePoints(object? curve)
        {
            if (curve == null) return new List<Point3>();

            object? polygon = Call(curve, "CalculatePolygon", CurveStepMm)
                              ?? Call(curve, "CalculatePolygon", -1.0)
                              ?? Call(curve, "CalculatePolygon", 0.0);

            return ReadPointsArray(polygon);
        }

        /// <summary>Читает точку по свойствам X/Y/Z (ksVertexDefinition::GetPoint и подобные).</summary>
        private static Point3? ReadPoint(object? pointObject)
        {
            if (pointObject == null) return null;

            object? x = Call(pointObject, "X") ?? GetMember(pointObject, "X");
            object? y = Call(pointObject, "Y") ?? GetMember(pointObject, "Y");
            object? z = Call(pointObject, "Z") ?? GetMember(pointObject, "Z");

            if (x == null && y == null) return null;

            return new Point3(ToDouble(x), ToDouble(y), ToDouble(z));
        }

        /// <summary>
        /// Сшивает куски в один контур по близости концов: направление обхода и порядок
        /// рёбер в массиве не гарантированы, а для построения области это критично.
        /// Берётся ближайший к текущему концу кусок (с возможным разворотом).
        /// </summary>
        private static List<Point3> ChainPieces(List<List<Point3>> pieces)
        {
            var remaining = pieces.Where(piece => piece.Count >= 2).ToList();
            if (remaining.Count == 0) return new List<Point3>();

            var points = new List<Point3>();
            List<Point3> start = remaining.OrderByDescending(piece => piece.Count).First();
            remaining.Remove(start);
            foreach (Point3 point in start) AppendDistinct(points, point);

            while (remaining.Count > 0)
            {
                Point3 tail = points[points.Count - 1];

                int bestIndex = -1;
                bool bestForward = true;
                double bestDistance = double.MaxValue;

                for (int i = 0; i < remaining.Count; i++)
                {
                    List<Point3> candidate = remaining[i];

                    double toStart = Distance2D(tail, candidate[0]);
                    if (toStart < bestDistance)
                    {
                        bestDistance = toStart;
                        bestIndex = i;
                        bestForward = true;
                    }

                    double toEnd = Distance2D(tail, candidate[candidate.Count - 1]);
                    if (toEnd < bestDistance)
                    {
                        bestDistance = toEnd;
                        bestIndex = i;
                        bestForward = false;
                    }
                }

                // Разрыв контура: несшитые куски отбрасываем, иначе контур «схлопнется».
                if (bestIndex < 0 || bestDistance > 0.5) break;

                List<Point3> next = remaining[bestIndex];
                remaining.RemoveAt(bestIndex);

                if (bestForward)
                {
                    for (int i = 1; i < next.Count; i++) AppendDistinct(points, next[i]);
                }
                else
                {
                    for (int i = next.Count - 2; i >= 0; i--) AppendDistinct(points, next[i]);
                }
            }

            if (points.Count > 1 && Distance2D(points[0], points[points.Count - 1]) < 1e-3)
                points.RemoveAt(points.Count - 1);

            return points;
        }

        private static void RememberEdge(Dictionary<string, PartEdge> edges, List<Point3> points, int faceId)
        {
            if (points.Count < 2) return;

            Point3 first = points[0];
            Point3 last = points[points.Count - 1];

            string key = EdgeKey(first, last);
            if (edges.ContainsKey(key)) return;

            var edge = new PartEdge
            {
                Name = "Ребро",
                Kind = PartCurveKind.Line,
                Start = new TrochoidEngine.Point2D(first.X, first.Y),
                End = new TrochoidEngine.Point2D(last.X, last.Y),
                Z = first.Z,
                FaceId = faceId
            };

            if (points.Count > 2 && TryFitCircle(points, out double cx, out double cy, out double radius))
            {
                edge.Kind = PartCurveKind.Arc;
                edge.Center = new TrochoidEngine.Point2D(cx, cy);
                edge.Radius = radius;

                double a0 = Math.Atan2(edge.Start.Y - cy, edge.Start.X - cx);
                double a1 = Math.Atan2(edge.End.Y - cy, edge.End.X - cx);
                double spanCcw = GeometryMath.NormalizeAngle(a1 - a0);

                Point3 mid = points[points.Count / 2];
                double midAngle = Math.Atan2(mid.Y - cy, mid.X - cx);

                // У замкнутого кругового ребра начало и конец совпадают: это полный оборот.
                bool closed = Distance2D(first, last) < 1e-3;
                if (closed)
                {
                    edge.Clockwise = false;
                    edge.Sweep = Math.PI * 2;
                }
                else
                {
                    bool ccw = GeometryMath.NormalizeAngle(midAngle - a0) < spanCcw;
                    edge.Clockwise = !ccw;
                    edge.Sweep = ccw ? spanCcw : Math.PI * 2 - spanCcw;
                }
            }

            edges[key] = edge;
        }

        private static bool TryFitCircle(List<Point3> points, out double cx, out double cy, out double radius)
        {
            cx = 0;
            cy = 0;
            radius = 0;

            if (points.Count < 3) return false;

            Point3 a = points[0];
            Point3 b = points[points.Count / 2];
            Point3 c = points[points.Count - 1];

            double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
            if (Math.Abs(d) < 1e-9) return false;

            double a2 = a.X * a.X + a.Y * a.Y;
            double b2 = b.X * b.X + b.Y * b.Y;
            double c2 = c.X * c.X + c.Y * c.Y;

            cx = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
            cy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
            radius = Math.Sqrt((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy));

            return radius > 1e-3;
        }

        /// <summary>Плоский массив координат x1, y1, z1, x2, y2, z2, … → точки.</summary>
        private static List<Point3> ReadPointsArray(object? value)
        {
            var points = new List<Point3>();
            if (value is not Array array) return points;

            var coordinates = new List<double>(array.Length);
            foreach (object? item in array)
            {
                if (item == null) continue;
                coordinates.Add(ToDouble(item));
            }

            for (int i = 0; i + 2 < coordinates.Count; i += 3)
                points.Add(new Point3(coordinates[i], coordinates[i + 1], coordinates[i + 2]));

            return points;
        }

        private static void AppendDistinct(List<Point3> points, Point3 point)
        {
            if (points.Count > 0 && Distance(points[points.Count - 1], point) < 1e-4) return;
            points.Add(point);
        }

        private static double Distance(Point3 a, Point3 b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double Distance2D(Point3 a, Point3 b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Ключ ребра не зависит от направления обхода: одно ребро читается из двух граней.</summary>
        private static string EdgeKey(Point3 a, Point3 b)
        {
            double ax = Math.Round(a.X, 3), ay = Math.Round(a.Y, 3), az = Math.Round(a.Z, 3);
            double bx = Math.Round(b.X, 3), by = Math.Round(b.Y, 3), bz = Math.Round(b.Z, 3);

            bool forward = ax < bx || (ax == bx && (ay < by || (ay == by && az <= bz)));

            string head = FormattableString.Invariant($"{ax:F3},{ay:F3},{az:F3}");
            string tail = FormattableString.Invariant($"{bx:F3},{by:F3},{bz:F3}");

            return forward ? head + "|" + tail : tail + "|" + head;
        }

        /// <summary>
        /// Вызов метода automation-объекта API 5. Никогда не выбрасывает исключение:
        /// сбой одного метода не должен ломать чтение всей модели.
        /// </summary>
        private static object? Call(object? target, string member, params object?[] args)
        {
            if (target == null) return null;

            try
            {
                return target.GetType().InvokeMember(
                    member,
                    BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    target,
                    args.Length == 0 ? null : args);
            }
            catch
            {
                // Часть методов API 5 объявлена как свойства — пробуем чтение свойства.
                try
                {
                    return target.GetType().InvokeMember(
                        member,
                        BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                        null,
                        target,
                        args.Length == 0 ? null : args);
                }
                catch
                {
                    return null;
                }
            }
        }

        private static string DescribeException(Exception ex)
        {
            Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return inner is COMException com ? $"0x{com.HResult:X8} {com.Message}" : inner.Message;
        }

        private static bool ToBool(object? value, bool fallback)
        {
            if (value == null) return fallback;
            if (value is bool flag) return flag;

            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0; }
            catch { return fallback; }
        }

        /// <summary>
        /// Устаревший сценарий: контур выделенной плоской грани.
        /// Оставлен для совместимости; основной путь — <see cref="ReadPartGeometry"/>.
        /// </summary>
        public List<TrochoidEngine.Point2D> GetSelectedFaceContours(out double faceZ)
        {
            faceZ = 0.0;
            var points = new List<TrochoidEngine.Point2D>();
            if (_activeDocument == null) return points;

            try
            {
                object? selectionManager = GetProperty(_activeDocument, "SelectionManager");
                object? selectedObjects = GetProperty(selectionManager, "SelectedObjects");
                int count = GetCount(selectedObjects);
                if (count == 0) return points;

                object? selectedObject = GetItem(selectedObjects, 0);
                if (selectedObject == null) return points;

                int objType = ToInt(GetProperty(selectedObject, "ObjectType"));
                if (objType != 4) return points; // oType_Face

                object? surface = GetProperty(selectedObject, "Geometry");
                int surfaceType = ToInt(GetProperty(surface, "SurfaceType"));
                if (surfaceType != 1) return points;

                object? planeOrigin = GetProperty(surface, "Origin");
                faceZ = ToDouble(GetMember(planeOrigin, "Z"));

                object? loops = GetProperty(selectedObject, "Loops");
                int loopsCount = GetCount(loops);
                if (loopsCount == 0) return points;

                object? outerLoop = GetItem(loops, 0);
                object? edges = GetProperty(outerLoop, "Edges");
                int edgesCount = GetCount(edges);

                for (int i = 0; i < edgesCount; i++)
                {
                    object? edge = GetItem(edges, i);
                    object? edgeGeometry = GetProperty(edge, "Geometry");
                    object? startPoint = GetProperty(edgeGeometry, "PointStart");
                    if (startPoint == null) continue;
                    points.Add(ToPoint2D(startPoint));
                }

                if (points.Count > 0) points.Add(points[0]);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка парсинга грани: {ex.Message}");
            }

            return points;
        }
    }
}
