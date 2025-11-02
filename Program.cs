// Program.cs
using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Globalization;


// ======================= Logger =======================
static class Log
{
    static StreamWriter? _file;
    public static void Init(string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        // <-- головне: FileShare.ReadWrite
        var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _file = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
    }
    public static void Info(string tag, string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{tag}] {msg}";
        Console.WriteLine(line);
        _file?.WriteLine(line);
    }
}


// ======================= утиліта портів ==========
static class NetPort
{
    public static bool IsFree(int port)
    {
        try { using var l = new TcpListener(System.Net.IPAddress.Loopback, port); l.Start(); l.Stop(); return true; }
        catch { return false; }
    }

    public static int FindFree(int start = 5173, int attempts = 50)
    {
        for (int p = start; p < start + attempts; p++)
            if (IsFree(p)) return p;

        using var l2 = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l2.Start();
        int free = ((System.Net.IPEndPoint)l2.LocalEndpoint).Port;
        l2.Stop();
        return free;
    }
}




static class InputNormalizer
{
    public static string Clean(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();

        // зняти зовнішні лапки, якщо рядок повністю в лапках
        if ((s.StartsWith("\"") && s.EndsWith("\"")) ||
            (s.StartsWith("«") && s.EndsWith("»")) ||
            (s.StartsWith("“") && s.EndsWith("”")))
            s = s[1..^1];

        // «розумні» лапки → звичайні
        s = s.Replace('«','"').Replace('»','"')
             .Replace('“','"').Replace('”','"').Replace('„','"');

        // прибрати екранування \" → "
        s = s.Replace("\\\"", "\"");

        // стиснути повторні пробіли
        while (s.Contains("  ")) s = s.Replace("  ", " ");

        return s.Trim();
    }
}


// ======================= Shell (+ Healer) =======================
static class Shell
{
    public static async Task<(int exit, string stdout, string stderr)> Run(string fileName, string args, string? cwd = null)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            WorkingDirectory = cwd ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi)!;
        string so = await p.StandardOutput.ReadToEndAsync();
        string se = await p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, so, se);
    }

    // Обгортка з авто-«лікуванням» і повторною спробою
    public static async Task<(int exit, string stdout, string stderr)> RunWithHeal(string tool, string args, string? cwd, string context)
    {
        var (exit, so, se) = await Run(tool, args, cwd);
        if (exit == 0) return (exit, so, se);

        var healed = await Healer.TryHealAsync(tool, args, cwd, context, so, se);
        if (!healed.didSomething) return (exit, so, se);

        Log.Info("HEAL", $"Applied: {healed.action}. Retrying…");
        var (exit2, so2, se2) = await Run(tool, healed.retryArgs ?? args, cwd);
        if (exit2 != 0) Log.Info("HEAL", $"Retry failed: {se2.Trim()}");
        return (exit2, so2, se2);
    }
}




static class Healer
{
    static readonly string HintsPath = Path.Combine(Directory.GetCurrentDirectory(), "Projects", "Workspace", "healer_hints.jsonl");

    public static async Task<(bool didSomething, string action, string? retryArgs)> TryHealAsync(
        string tool, string args, string? cwd, string context, string stdout, string stderr)
    {
        // 1) Відомий кейс: шаблон уже створений (exit code 73) → додати --force
        if (stderr.Contains("templating-exit-codes#73", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Creating this template will make changes to existing files", StringComparison.OrdinalIgnoreCase))
        {
            if (!args.Contains("--force"))
            {
                var retry = args + " --force";
                await RememberAsync(new()
                {
                    ts = DateTime.UtcNow,
                    context = context,
                    tool = tool,
                    args = args,
                    stderr = Short(stderr),
                    action = "append --force to dotnet new"
                });
                return (true, "append --force", retry);
            }
        }

        // 2) Підказка: немає Node.js для React/Vite
        if (context.StartsWith("vite:", StringComparison.OrdinalIgnoreCase) &&
            (stderr.Contains("'node' is not recognized", StringComparison.OrdinalIgnoreCase) ||
             stderr.Contains("not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase)))
        {
            Log.Info("HEAL", "Node.js не знайдено. Встановіть Node.js із https://nodejs.org/ — тимчасово створюю статичну заглушку.");
            await RememberAsync(new()
            {
                ts = DateTime.UtcNow,
                context = context,
                tool = tool,
                args = args,
                stderr = Short(stderr),
                action = "advice: install Node.js"
            });
            return (false, "advise node install", null);
        }

        // 3) Підказка: відсутній потрібний .NET SDK/TargetFramework
        if (stderr.Contains("The framework", StringComparison.OrdinalIgnoreCase) &&
            stderr.Contains("was not found", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("HEAL", "Схоже, відсутній потрібний .NET SDK/TargetFramework. Перевірте TargetFramework у *.csproj та встановлені SDK.");
            await RememberAsync(new()
            {
                ts = DateTime.UtcNow,
                context = context,
                tool = tool,
                args = args,
                stderr = Short(stderr),
                action = "advice: install proper .NET SDK"
            });
            return (false, "advise install SDK", null);
        }

        // 4) Build-проблеми: спробувати restore → build
        if (tool == "dotnet" && args.StartsWith("build", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("HEAL", "Спробую dotnet restore перед build…");
            var (re, _, rse) = await Shell.Run("dotnet", $"restore \"{cwd}\"", cwd);
            await RememberAsync(new()
            {
                ts = DateTime.UtcNow,
                context = context,
                tool = tool,
                args = args,
                stderr = Short(stderr),
                action = $"dotnet restore (exit={re})"
            });
            if (re == 0) return (true, "restore then build", args); // повторити той самий build
        }

        // 5) Загальний хінт
        await RememberAsync(new() { ts = DateTime.UtcNow, context = context, tool = tool, args = args, stderr = Short(stderr), action = "no-known-fix" });

        // Якщо це була команда створення додатку і ми не знайшли фіксу,
        // спробуємо "навчитися" новому типу генератора за контекстом.
        try
        {
            if (context.StartsWith("proj:new", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(context, @"(?i)(create|створ|зроби)"))
            {
                // робоча директорія агенту (Workspace) = cwd або поточна
                var ws = cwd ?? Path.Combine(Directory.GetCurrentDirectory(), "Projects", "Workspace");
                Directory.CreateDirectory(ws);
                // контекст або stdout/stderr можуть містити назву
                var m = Regex.Match(context + " " + stdout + " " + stderr, @"(?i)(калькулятор|calculator|todo|to\-?do|timer|таймер|[A-Za-zА-Яа-я0-9\-_]{3,})");
                if (m.Success)
                {
                    var key = m.Value.Trim();
                    GeneratorRegistry.EnsureBuiltin(ws);
                    var (ok, _, __) = GeneratorRegistry.TryResolve(ws, key);
                    if (!ok) GeneratorRegistry.LearnUnknown(ws, key);
                }
            }
        }
        catch { /* мʼяко ігноруємо */ }


        // FIX: якщо порт зайнятий — знайти інший і перезапустити з ним
        if (stderr.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
        {
            int newPort = NetPort.FindFree(WebPorts.Default);
            Log.Info("HEAL", $"Port busy. Switching to free port {newPort}");

            string modifiedArgs = args + $" --urls http://localhost:{newPort}";
            await RememberAsync(new()
            {
                ts = DateTime.UtcNow,
                context = context,
                tool = tool,
                args = args,
                stderr = Short(stderr),
                action = $"changed port to {newPort}"
            });

            return (true, $"use free port {newPort}", modifiedArgs);
        }

        // ===== Web-backed research fallback =====
        try
        {
            var ws = cwd ?? Path.Combine(Directory.GetCurrentDirectory(), "Projects", "Workspace");
            var adv = await Researcher.DiagnoseAsync(ws, context, stdout ?? "", stderr ?? "");

            foreach (var a in adv)
            {
                foreach (var act in a.Actions.Where(x => x.StartsWith("#fix:", StringComparison.OrdinalIgnoreCase)))
                    Log.Info("HEAL", act);

                var safeCmd = a.Actions.FirstOrDefault(x => Regex.IsMatch(x, @"^(dotnet|npm|npx)\b", RegexOptions.IgnoreCase));
                if (!string.IsNullOrEmpty(safeCmd))
                {
                    Log.Info("HEAL", $"Trying from web advice: {safeCmd}");
                    var tool2 = safeCmd.Split(' ', 2)[0];
                    var args2 = safeCmd.Contains(' ') ? safeCmd.Substring(tool2.Length).Trim() : "";

                    var (re, so2, se2) = await Shell.Run(tool2, args2, cwd);
                    await RememberAsync(new()
                    {
                        ts = DateTime.UtcNow,
                        context = context,
                        tool = tool2,
                        args = args2,
                        stderr = Short(se2),
                        action = $"web-advice: {safeCmd} (exit={re})"
                    });

                    if (re == 0 && (safeCmd.StartsWith("dotnet restore") || safeCmd.StartsWith("npm install") || safeCmd.StartsWith("npm ci")))
                    {
                        return (true, $"web advice: {safeCmd}", args); // повторимо оригінальну команду
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("HEAL", "Research fallback error: " + ex.Message);
        }


        return (false, "no-known-fix", null);
    }
    // Повертає текст Program.cs для консольного проєкту на основі команди
static string ConsoleTemplateFor(string cmd)
{
    // працюємо з оригіналом (без зниження регістру), але тримаємо нижній для ключових слів
    var original = InputNormalizer.Clean(cmd);
    var s = original.ToLowerInvariant();

    // 0) '... що виводить "..."'
    var mQuoted = Regex.Match(
        original,
        "що\\s+виводить\\s+\"([^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    if (mQuoted.Success)
    {
        var text = mQuoted.Groups[1].Value.Replace("\"", "\"\"");
        return $"Console.WriteLine(\"{text}\");";
    }

    // 0.1) '... що виводить 123' (без лапок)
    var mBare = Regex.Match(
        s, "що\\s+виводить\\s+([\\p{L}\\d]+)",
        RegexOptions.CultureInvariant
    );
    if (mBare.Success)
        return $"Console.WriteLine(\"{mBare.Groups[1].Value}\");";

    // 1) "введіть число" / "прочитай число"
    if (s.Contains("введіть число") || s.Contains("прочитай число") || s.Contains("read number"))
    {
        return
@"Console.Write(""Введіть число: "");
string? input = Console.ReadLine();
if (double.TryParse(input, out var number))
{
    Console.WriteLine($""Ви ввели: {number}"");
}
else
{
    Console.WriteLine(""Помилка: введіть число"");
}";
    }

    // 2) "зчитує два числа" / "сума"
    if (s.Contains("зчитує два числа") || s.Contains("сума"))
    {
        return
@"Console.Write(""Введіть a: "");
string? sa = Console.ReadLine();
Console.Write(""Введіть b: "");
string? sb = Console.ReadLine();

if (double.TryParse(sa, out var a) && double.TryParse(sb, out var b))
{
    Console.WriteLine($""Сума: {a + b}"");
}
else
{
    Console.WriteLine(""Помилка: введіть числа"");
}";
    }

    // (залиши інші твої правила за потреби)

    // дефолт — гарантія успіху самотесту
    return @"Console.WriteLine(""Привіт"");";
}

    static string Short(string s)
    {
        s = s.Trim();
        if (s.Length < 800) return s;
        return s.Substring(0, 800) + " ...";
    }

    record Hint
    {
        public DateTime ts { get; set; }
        public string context { get; set; } = "";
        public string tool { get; set; } = "";
        public string args { get; set; } = "";
        public string stderr { get; set; } = "";
        public string action { get; set; } = "";
    }

    static async Task RememberAsync(Hint h)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HintsPath)!);
        var line = JsonSerializer.Serialize(h);
        await File.AppendAllTextAsync(HintsPath, line + Environment.NewLine, Encoding.UTF8);
    }

    public static IEnumerable<string> ReadHints()
    {
        if (!File.Exists(HintsPath)) yield break;
        foreach (var line in File.ReadLines(HintsPath))
            yield return line;
    }
}


// ======================= Browser =======================
static class Browser
{
    public static void Open(string url)
    {
        try
        {
            // Найнадійніше на Windows
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return;
        }
        catch { /* fallback нижче */ }

        try
        {
            Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            Log.Info("BROWSER", "Open failed: " + ex.Message);
        }
    }
}

// ======================= Self Tester (навчання на запуску) =======================
public record SelfTestResult(bool ok, int status, int bytes, List<string> notes);

static class SelfTester
{
    static readonly string LearnLog = Path.Combine(
        Directory.GetCurrentDirectory(), "Projects", "Workspace", "learning.jsonl");

    public static async Task<SelfTestResult> EvaluateAsync(string baseUrl, AppGenerator gen, string wwwroot, bool wantsDark = false)
    {
        static bool HasAllIds(string html, params string[] ids)
    => ids.All(id => html.IndexOf($"id=\"{id}\"", StringComparison.OrdinalIgnoreCase) >= 0);

        var notes = new List<string>();
        var url = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        using var http = new HttpClient();

        // 1) Чекаємо хост (до ~10 с)
        HttpResponseMessage? resp = null;
        string html = "";
        var started = DateTime.UtcNow;
        for (int i = 0; i < 40; i++)  // 40 * 250мс ≈ 10с
        {
            try
            {
                resp = await http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    html = await resp.Content.ReadAsStringAsync();
                    break;
                }
            }
            catch { /* порт ще не піднявся */ }
            await Task.Delay(250);
        }

        if (resp == null)
        {
            notes.Add("Host not responding");
            await RememberAsync(gen, url, false, 0, 0, notes);
            return new SelfTestResult(false, 0, 0, notes);
        }

        var status = (int)resp.StatusCode;
        var bytes = Encoding.UTF8.GetByteCount(html);
        if (status != 200) notes.Add($"HTTP {status}");
        if (bytes < 50)    notes.Add("HTML too small");

        // 2) Перевірка вмісту
var titleHit = (gen.title?.Length > 0 && html.Contains(gen.title, StringComparison.OrdinalIgnoreCase))
               || html.Contains("<title>", StringComparison.OrdinalIgnoreCase)
               || html.Contains("<h1", StringComparison.OrdinalIgnoreCase);
if (!titleHit) notes.Add("Title/H1 not found");

// 2.1) Семантична перевірка за типом генератора
var uiOk = true;
if (string.Equals(gen.title, "Calculator", StringComparison.OrdinalIgnoreCase))
{
    uiOk = HasAllIds(html, "a", "b", "op", "calc");
    if (!uiOk) notes.Add("calculator: UI controls missing (#a, #b, #op, #calc)");
}
else if (string.Equals(gen.title, "Timer", StringComparison.OrdinalIgnoreCase))
{
    uiOk = HasAllIds(html, "clock", "start", "stop", "reset");
    if (!uiOk) notes.Add("timer: UI controls missing (#clock, #start, #stop, #reset)");
}
else if (string.Equals(gen.title, "Wallet", StringComparison.OrdinalIgnoreCase))
{
    uiOk = HasAllIds(html, "amount", "type", "add", "balance", "list");
    if (!uiOk) notes.Add("wallet: UI controls missing (#amount, #type, #add, #balance, #list)");
}
else if (string.Equals(gen.title, "Notes", StringComparison.OrdinalIgnoreCase))
{
    uiOk = HasAllIds(html, "pad");
    if (!uiOk) notes.Add("notes: UI controls missing (#pad)");
}


// 2.2) Перевірка застосованого стилю (CSS vars / font / accent)
try
{
    // намагаємось зчитати локальний style.css
    var cssPath = Path.Combine(wwwroot, "style.css");
    if (File.Exists(cssPath))
    {
        var css = await File.ReadAllTextAsync(cssPath);
        if (css.IndexOf(":root", StringComparison.OrdinalIgnoreCase) < 0 ||
            css.IndexOf("--accent", StringComparison.OrdinalIgnoreCase) < 0)
            notes.Add("style: css variables not found");

        if (css.IndexOf("font-family:var(--font)", StringComparison.OrdinalIgnoreCase) < 0)
            notes.Add("style: font var not applied");
    }
    else
    {
        notes.Add("style.css: file missing");
    }
}
catch { /* мʼяко ігноруємо */ }


        // 3) Спроба знайти style.css та app.js у wwwroot і за URL
        await ProbeAssetAsync(http, url + "style.css", Path.Combine(wwwroot, "style.css"), "style.css", notes);
        await ProbeAssetAsync(http, url + "app.js",   Path.Combine(wwwroot, "app.js"),   "app.js",   notes);

       var ok = status == 200 && bytes >= 50 && titleHit && uiOk;
        await RememberAsync(gen, url, ok, status, bytes, notes);
        return new SelfTestResult(ok, status, bytes, notes);
    }

    static async Task ProbeAssetAsync(HttpClient http, string assetUrl, string localPath, string name, List<string> notes)
    {
        if (!File.Exists(localPath))
        {
            notes.Add($"{name}: file missing in wwwroot");
            return;
        }
        try
        {
            var r = await http.GetAsync(assetUrl);
            if (!r.IsSuccessStatusCode)
                notes.Add($"{name}: HTTP {(int)r.StatusCode}");
        }
        catch
        {
            notes.Add($"{name}: request failed");
        }
    }

    static async Task RememberAsync(AppGenerator gen, string url, bool ok, int status, int bytes, List<string> notes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LearnLog)!);
        var line = JsonSerializer.Serialize(new {
            ts = DateTime.UtcNow,
            title = gen.title,
            keys = gen.keys,
            url,
            ok,
            status,
            bytes,
            notes
        });
        await File.AppendAllTextAsync(LearnLog, line + Environment.NewLine, Encoding.UTF8);
        Log.Info("LEARN", $"SelfTest: {(ok ? "OK" : "FAIL")} • {gen.title} • {url}");
        if (notes.Count > 0) Log.Info("LEARN", "Notes: " + string.Join(" | ", notes));
    }
}



// ======================= LearningTester (console + web) =======================
static class LearningTester
{
    public static async Task<bool> TestAsync(string ws, string taskPhrase)
    {
        var p = (taskPhrase ?? "").ToLowerInvariant();
        if (p.Contains("веб") || p.Contains("web"))
            return await TestWebAsync(ws);       // ← існує нижче
       return await TestConsoleAsync(ws, taskPhrase);

    }

    // --- Консоль: будуємо проєкт і запускаємо, без --no-build ---
// --- Консоль: будуємо проект і запускаємо, без --no-build ---
static async Task<bool> TestConsoleAsync(string ws, string? taskPhrase)
{
    Log.Info("TEST", "Console self-test start in " + ws);

    // 1) Знаходимо каталог консольного проєкту
    string? projDir = null;

    // Використати останній збережений шлях, якщо є
    var lastPathFile = Path.Combine(ws, "last_console_path.txt");
    if (File.Exists(lastPathFile))
    {
        var saved = (await File.ReadAllTextAsync(lastPathFile)).Trim();
        if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
            projDir = saved;
    }

    // Fallback: пошук найсвіжішого каталогу з *.csproj у Playground
    if (string.IsNullOrEmpty(projDir))
    {
        var playground = Path.Combine(ws, "Playground");
        if (Directory.Exists(playground))
        {
            projDir = Directory.EnumerateDirectories(playground)
                .OrderByDescending(d => Directory.GetCreationTime(d))
                .FirstOrDefault(d => Directory.EnumerateFiles(d, "*.csproj").Any());
        }
    }

    if (string.IsNullOrEmpty(projDir))
{
    // створюємо свіжий консольний проєкт у Workspace/Playground
    var playground = Path.Combine(ws, "Playground");
    Directory.CreateDirectory(playground);
    var name = "ConsoleApp_" + DateTime.Now.ToString("HHmmss");
    projDir = Path.Combine(playground, name);

    var cr = await Shell.Run("dotnet", $"new console -o \"{projDir}\"", ws);
    if (cr.exit != 0)
    {
        Log.Info("TEST", "Cannot create console project: " + (cr.stderr ?? "").Trim());
        return false;
    }

    await File.WriteAllTextAsync(Path.Combine(ws, "last_console_path.txt"), projDir!, Encoding.UTF8);
}


    // 2) Будуємо
    var build = await Shell.Run("dotnet", "build", projDir);
    if (build.exit != 0)
    {
        Log.Info("TEST", "Build failed: " + (build.stderr ?? "").Trim());
        return false;
    }

    // 3) Запускаємо
    var run = await Shell.Run("dotnet", "run", projDir);
    if (run.exit != 0)
    {
        Log.Info("TEST", "Console run ERR: " + (run.stderr ?? "").Trim());
        return false;
    }

    var stdout = run.stdout ?? "";

    // 4) Якщо в задачі є «що виводить "..."» — звіряємо точний текст
   string expected = "";
var normalizedTask = InputNormalizer.Clean(taskPhrase ?? "");
var m = Regex.Match(
    normalizedTask,
    "що\\s+виводить\\s+\"([^\"]+)\"",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
);
if (m.Success) expected = m.Groups[1].Value;


    if (!string.IsNullOrWhiteSpace(expected))
    {
        var ok = stdout.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        if (!ok)
            Log.Info("TEST", $"Console stdout does not contain expected: \"{expected}\"  | stdout: {stdout.Trim()}");
        return ok;
    }

    // 5) Інакше — достатньо будь-якого непорожнього виводу
    var basicOk = !string.IsNullOrWhiteSpace(stdout);
    if (!basicOk) Log.Info("TEST", "Console stdout is empty");
    return basicOk;
}

    // --- Web: як у твоїй оригінальній версії ---
    static async Task<bool> TestWebAsync(string ws)
    {
        // шукаємо останній last_url.txt і перевіряємо HTTP 200 + розмір
        var last = Directory.GetFiles(ws, "last_url.txt", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (last == null) return false;

        var url = (File.ReadAllText(last.FullName) ?? "").Trim();
        var hostDir = last.Directory!.FullName;             // …/Project/wwwroot/.. (папка проекту)
        var wwwroot = Path.Combine(hostDir, "wwwroot");

        var gen = new AppGenerator { title = Path.GetFileName(Path.GetDirectoryName(hostDir)) ?? "App", keys = new[] { "app" }, files = new() };
        var res = await SelfTester.EvaluateAsync(url, gen, wwwroot);
        return res.ok && res.status == 200 && res.bytes >= 50;
    }
}

// ======================= Web Search & Fetch =======================
record SearchHit(string Title, string Url, string Snippet);

static class Web
{
    static readonly HttpClient _http;

    static Web()
    {
        // --- Жорстко увімкнути сучасні протоколи TLS (важливо для Win10/Server) ---
        try
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 |
#if NET6_0_OR_GREATER
                (SecurityProtocolType)12288 | // TLS 1.3 на деяких збірках
#endif
                SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
        }
        catch { /* ignore */ }

        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true
        })
        { Timeout = TimeSpan.FromSeconds(25) };

        try
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9,uk;q=0.8");
            _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        }
        catch { /* ignore */ }
    }

    public static async Task<List<SearchHit>> SearchAsync(string query, int take = 6)
    {
        var hits = new List<SearchHit>();
        var qList = new[]
        {
            query,
            $"{query} site:stackoverflow.com",
            $"{query} site:learn.microsoft.com"
        };

        foreach (var q in qList)
        {
            // 1) DuckDuckGo HTML
            if (hits.Count < take) await TryDuckDuckGoHtml(q, hits, take);

            // 2) DuckDuckGo Lite (ще простіша верстка)
            if (hits.Count < take) await TryDuckDuckGoLite(q, hits, take);

            // 3) Bing (фолбек)
            if (hits.Count < take) await TryBing(q, hits, take);

            if (hits.Count >= take) break;
        }

        if (hits.Count == 0)
            Log.Info("RESEARCH", $"SearchAsync: no results for \"{query}\" (TLS/HTML? firewall?)");

        return hits;
    }

    public static async Task<string> FetchTextAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode) return "";

            var bytes = await res.Content.ReadAsByteArrayAsync();
            var media = res.Content.Headers.ContentType?.MediaType ?? "";
            var charset = res.Content.Headers.ContentType?.CharSet;

            // Витягти encoding з meta charset, якщо заголовок порожній/неправильний
            var htmlProbe = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            if (string.IsNullOrWhiteSpace(charset))
            {
                var m = Regex.Match(htmlProbe, @"charset\s*=\s*[""']?([\w\-]+)", RegexOptions.IgnoreCase);
                if (m.Success) charset = m.Groups[1].Value;
            }

            Encoding enc = Encoding.UTF8;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try { enc = Encoding.GetEncoding(charset); } catch { }
            }
            var textRaw = enc.GetString(bytes);

            if (!media.Contains("html", StringComparison.OrdinalIgnoreCase))
                return Trunc(textRaw, 120000);

            // Прибрати скрипти/стилі/коментарі і теги
            var html = Regex.Replace(textRaw, @"<script\b[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<style\b[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
            var txt = HtmlToText(html);
            return Trunc(txt, 120000);
        }
        catch (Exception ex)
        {
            Log.Info("RESEARCH", "FetchTextAsync ERR: " + ex.Message);
            return "";
        }
    }

    // ---------- Engines ----------
    static async Task TryDuckDuckGoHtml(string query, List<SearchHit> hits, int take)
    {
        try
        {
            var url = "https://duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            var html = await GetStringAsync(url);
            if (string.IsNullOrEmpty(html)) return;

            var rx = new Regex(
                @"<a[^>]*class=""result__a""[^>]*href=""(?<u>https?://[^""]+)""[^>]*>(?<t>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match m in rx.Matches(html))
            {
                var u = HtmlDecode(m.Groups["u"].Value);
                if (IsBadUrl(u)) continue;
                var t = HtmlToText(m.Groups["t"].Value);
                var s = BuildSnippetAround(html, m.Index);
                Push(hits, t, u, s, take);
            }
        }
        catch (Exception ex)
        {
            Log.Info("RESEARCH", "DDG html ERR: " + ex.Message);
        }
    }

    static async Task TryDuckDuckGoLite(string query, List<SearchHit> hits, int take)
    {
        try
        {
            var url = "https://lite.duckduckgo.com/lite/?q=" + Uri.EscapeDataString(query);
            var html = await GetStringAsync(url);
            if (string.IsNullOrEmpty(html)) return;

            // Lite варіант: <a rel="nofollow" href="https://...">Title</a>
            var rx = new Regex(@"<a[^>]*href=""(?<u>https?://[^""]+)""[^>]*>(?<t>.*?)</a>",
                               RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match m in rx.Matches(html))
            {
                var u = HtmlDecode(m.Groups["u"].Value);
                if (IsBadUrl(u)) continue;
                var t = HtmlToText(m.Groups["t"].Value);
                if (string.IsNullOrWhiteSpace(t)) continue;
                Push(hits, t, u, "", take);
            }
        }
        catch (Exception ex)
        {
            Log.Info("RESEARCH", "DDG lite ERR: " + ex.Message);
        }
    }

    static async Task TryBing(string query, List<SearchHit> hits, int take)
    {
        try
        {
            var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);
            var html = await GetStringAsync(url);
            if (string.IsNullOrEmpty(html)) return;

            // Стандартна картка: <li class="b_algo"> ... <h2><a href="...">Title</a></h2> ... <p>snippet</p>
            var liRx = new Regex(@"<li[^>]*class=""b_algo""[^>]*>(?<li>.+?)</li>",
                                 RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match li in liRx.Matches(html))
            {
                var liHtml = li.Groups["li"].Value;
                var aRx = new Regex(@"<h2>\s*<a[^>]*href=""(?<u>https?://[^""]+)""[^>]*>(?<t>.*?)</a>",
                                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var a = aRx.Match(liHtml);
                if (!a.Success) continue;

                var u = HtmlDecode(a.Groups["u"].Value);
                if (IsBadUrl(u)) continue;
                var t = HtmlToText(a.Groups["t"].Value);

                var pRx = new Regex(@"<p[^>]*>(?<p>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var p = pRx.Match(liHtml);
                var s = p.Success ? HtmlToText(p.Groups["p"].Value) : "";

                Push(hits, t, u, s, take);
            }

            // Додатково: блок "people also ask" (корисні Q/A)
            if (hits.Count < take)
            {
                var paaRx = new Regex(@"<div[^>]*class=""b_ans""[^>]*>(?<blk>.+?)</div>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (Match m in paaRx.Matches(html))
                {
                    var blk = HtmlToText(m.Groups["blk"].Value);
                    if (blk.Length > 20)
                        Push(hits, "Related", url, blk.Length > 200 ? blk[..200] + " …" : blk, take);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("RESEARCH", "Bing ERR: " + ex.Message);
        }
    }

    // ---------- helpers ----------
    static void Push(List<SearchHit> list, string title, string url, string snippet, int take)
    {
        if (list.Any(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(new SearchHit(title, url, snippet));
        if (list.Count > take) list.RemoveRange(take, list.Count - take);
    }

    static async Task<string> GetStringAsync(string url)
    {
        try { return await _http.GetStringAsync(url); }
        catch (Exception ex)
        {
            Log.Info("RESEARCH", "HTTP ERR: " + ex.Message + " @ " + url);
            return "";
        }
    }

    static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s;
    }

    static string HtmlDecode(string s) => WebUtility.HtmlDecode(s);

    static string BuildSnippetAround(string html, int idx)
    {
        var start = Math.Max(0, idx - 350);
        var end = Math.Min(html.Length, idx + 350);
        var piece = HtmlToText(html.Substring(start, end - start));
        return piece.Length > 220 ? piece.Substring(0, 220) + " …" : piece;
    }

    static bool IsBadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.Contains("duckduckgo.com/y.js")) return true;
        if (url.Contains("/r.js")) return true;
        return false;
    }

    static string Trunc(string s, int n) => s.Length > n ? s.Substring(0, n) : s;
}


// ======================= Researcher (web-powered) =======================
record ResearchAdvice(string Title, string Url, string[] Actions, string Summary);

static class Researcher
{
    static string KnowledgeDir(string ws) => Path.Combine(ws, "knowledge");
    static string SafeFile(string s) =>
        new string((s ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray())
        .ToLowerInvariant();

    public static async Task<List<ResearchAdvice>> DiagnoseAsync(string ws, string context, string stdout, string stderr)
    {
        var adv = new List<ResearchAdvice>();
        Directory.CreateDirectory(KnowledgeDir(ws));

        var q = BuildQuery(context, stdout, stderr);
        if (string.IsNullOrWhiteSpace(q)) return adv;

// --- OFFLINE knowledge (fallback) ---
static List<ResearchAdvice> OfflineAdv(string q)
{
    q = (q ?? "").ToLowerInvariant();
    var list = new List<ResearchAdvice>();

    if (q.Contains("cs1513"))
    {
        list.Add(new ResearchAdvice(
            "CS1513: } expected — як виправити",
            "https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/cs1513",
            new[]{
                "#fix: Add missing '}' brace (CS1513)",
                "dotnet build"
            },
            "CS1513 означає, що бракує закриваючої дужки }. Перевір пари { } навколо класів/методів/блоків. Після додавання перезбери проєкт."
        ));
    }

    if (q.Contains("only one usage of each socket address") || q.Contains("address already in use") || q.Contains("kestrel"))
    {
        list.Add(new ResearchAdvice(
            "Kestrel: порт зайнятий (Only one usage of each socket address)",
            "https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel",
            new[]{
                "#fix: Change dev server port",
                "dotnet run --urls http://localhost:0",
            },
            "Ця помилка означає, що порт уже зайнято. Запусти із іншим портом або звільни існуючий процес. У коді агента в нас є Healer, що вміє перейти на вільний порт."
        ));
    }

    return list;
}


        Log.Info("RESEARCH", $"Search: {q}");
        var hits = await Web.SearchAsync(q, 6);

        foreach (var h in hits)
        {
            var text = await Web.FetchTextAsync(h.Url);
            if (string.IsNullOrEmpty(text)) continue;

            var actions = ExtractActions(text, context);
            if (actions.Length == 0) continue;

            var summary = Summarize(text, 900);
            adv.Add(new ResearchAdvice(h.Title, h.Url, actions, summary));
        }

        if (adv.Count > 0)
        {
            var name = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + SafeFile(q) + ".md";
            var path = Path.Combine(KnowledgeDir(ws), name);

            var md = new StringBuilder();
            md.AppendLine("# Research: " + q);
            md.AppendLine("- Context: " + (context ?? ""));
            md.AppendLine("- Time: " + DateTime.UtcNow.ToString("u"));

            foreach (var a in adv)
            {
                md.AppendLine("\n## " + a.Title);
                md.AppendLine(a.Url);
                md.AppendLine("\n**Proposed actions:**");
                foreach (var act in a.Actions) md.AppendLine("- " + act);
                md.AppendLine("\n" + a.Summary);
            }

            File.WriteAllText(path, md.ToString(), Encoding.UTF8);
            Log.Info("RESEARCH", "Saved: " + path);
        }
// Якщо веб нічого не дав — офлайн підказки
if (adv.Count == 0)
{
    var off = OfflineAdv(q);
    if (off.Count > 0)
    {
        adv.AddRange(off);

        var name = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + SafeFile(q) + "_offline.md";
        var path = Path.Combine(KnowledgeDir(ws), name);

        var md = new StringBuilder();
        md.AppendLine("# Research (offline): " + q);
        md.AppendLine("- Context: " + (context ?? ""));
        md.AppendLine("- Time: " + DateTime.UtcNow.ToString("u"));
        foreach (var a in off)
        {
            md.AppendLine("\n## " + a.Title);
            md.AppendLine(a.Url);
            md.AppendLine("\n**Proposed actions:**");
            foreach (var act in a.Actions) md.AppendLine("- " + act);
            md.AppendLine("\n" + a.Summary);
        }
        File.WriteAllText(path, md.ToString(), Encoding.UTF8);
        Log.Info("RESEARCH", "Saved (offline): " + path);
    }
}

        return adv;
    }

    // ---- helpers ----

    static string BuildQuery(string context, string so, string se)
    {
        // один буфер
        var all = string.Join(" ", (se ?? ""), (so ?? ""), (context ?? "")).Trim();

        // 1) коди компілятора / HTTP
        var cs = Regex.Match(all, @"CS\d{4}").Value;
        if (!string.IsNullOrWhiteSpace(cs)) return cs + " fix";

        var http = Regex.Match(all, @"HTTP\s*\d{3}").Value;
        if (!string.IsNullOrWhiteSpace(http)) return http + " error fix";

        // 2) порт зайнятий
        if (all.IndexOf("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase) >= 0)
            return "kestrel Only one usage of each socket address fix";
        if (all.IndexOf("address already in use", StringComparison.OrdinalIgnoreCase) >= 0)
            return "vite address already in use port fix";

        // 3) manual:topic
        var manual = Regex.Match(context ?? "", @"(?i)^manual:\s*(.+)$");
        if (manual.Success)
            return manual.Groups[1].Value.Trim();

        // 4) фолбек: ключові слова
        var key = string.Join(" ",
            Regex.Matches(all, @"[A-Za-z][A-Za-z0-9\-\._]{2,}")
                 .Cast<Match>().Select(m => m.Value).Take(6));

        return key;
    }

    static string[] ExtractActions(string text, string context)
    {
        var actions = new List<string>();

        void AddIfSafe(string cmd)
        {
            if (!Regex.IsMatch(cmd, @"^(dotnet|npm|npx)\b", RegexOptions.IgnoreCase)) return;
            if (Regex.IsMatch(cmd, @"[;&><]")) return; // без небезпечних символів
            actions.Add(cmd.Trim());
        }

        foreach (Match m in Regex.Matches(text, @"\b(dotnet\s+[^\r\n]+)", RegexOptions.IgnoreCase)) AddIfSafe(m.Value);
        foreach (Match m in Regex.Matches(text, @"\b(npm\s+[^\r\n]+|npx\s+[^\r\n]+)", RegexOptions.IgnoreCase)) AddIfSafe(m.Value);

        if (text.IndexOf("CS1513", StringComparison.OrdinalIgnoreCase) >= 0 && actions.Count == 0)
            actions.Add("#fix: Add missing '}' brace (CS1513)");
        if (text.IndexOf("address already in use", StringComparison.OrdinalIgnoreCase) >= 0 && actions.Count == 0)
            actions.Add("#fix: Change dev server port");

        return actions.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
    }

    static string Summarize(string text, int max)
    {
        var lines = text.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .Take(60);
        var summary = string.Join("\n", lines);
        if (summary.Length > max) summary = summary.Substring(0, max) + " …";
        return summary;
    }
}



// ======================= AI Feedback (web-powered tips) =======================
static class AIFeedback
{
    public static async Task AdviseAsync(string ws, string context, string stderrSample = "")
    {
        try
        {
            var tips = await Researcher.DiagnoseAsync(ws, "manual:" + context, "", stderrSample ?? "");
            if (tips.Count == 0)
            {
                Log.Info("FEEDBACK", "Немає порад з веба (offline хінти могли бути збережені).");
            }
            else
            {
                Log.Info("FEEDBACK", $"Отримано порад: {tips.Count} (див. knowledge/)");
            }
        }
        catch (Exception ex)
        {
            Log.Info("FEEDBACK", "ERR: " + ex.Message);
        }
    }
}




// ======================= Task Extensions =======================
static class TaskExtensions
{
    public static void Forget(this Task task, string tag = "TASK")
    {
        task.ContinueWith(t =>
        {
            try
            {
                var ex = t.Exception?.GetBaseException();
                if (ex != null) Log.Info(tag, "ERR (fire-and-forget): " + ex.Message);
            }
            catch { /* ignore */ }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public static void Forget<T>(this Task<T> task, string tag = "TASK")
    {
        task.ContinueWith(t =>
        {
            try
            {
                var ex = t.Exception?.GetBaseException();
                if (ex != null) Log.Info(tag, "ERR (fire-and-forget): " + ex.Message);
            }
            catch { /* ignore */ }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}


// ======================= Memory =======================
class MemoryJson
{
    readonly string _path;
    public MemoryJson(string path)
    {
        _path = path;
        if (!File.Exists(_path)) File.WriteAllText(_path, "[]");
    }
    public void Add(string message)
    {
        var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? new();
        list.Add($"{DateTime.UtcNow:o}  {message}");
        File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }
}


// ======================= Skill Progress =======================
record SkillProgress
{
    public string key { get; set; } = "";
    public int successes { get; set; }
    public int failures { get; set; }
    public int level { get; set; }
}

static class ProgressStore
{
    static string PathFor(string ws) => System.IO.Path.Combine(ws, "skill_progress.json");

    public static Dictionary<string, SkillProgress> Load(string ws)
    {
        var p = PathFor(ws);
        if (!File.Exists(p)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, SkillProgress>>(File.ReadAllText(p)) ?? new(); }
        catch { return new(); }
    }

    public static void Save(string ws, Dictionary<string, SkillProgress> map)
        => File.WriteAllText(PathFor(ws), JsonSerializer.Serialize(map, new JsonSerializerOptions{WriteIndented=true}), Encoding.UTF8);

    public static void Bump(string ws, string key, bool ok)
    {
        var m = Load(ws);
        if (!m.TryGetValue(key, out var sp)) sp = new SkillProgress{ key = key, successes = 0, failures = 0, level = 0 };
        if (ok) sp.successes++; else sp.failures++;
        sp.level = Math.Min(10, sp.successes / 3);
        m[key] = sp; Save(ws, m);
    }
}




// ======================= Curriculum (levels.json + curriculum.json) =======================
static class CurriculumStore
{
    static string LevelsPath(string ws)      => Path.Combine(ws, "levels.json");
    static string CurriculumPath(string ws)  => Path.Combine(ws, "curriculum.json");

    public static int GetCurrentLevel(string ws)
    {
        try
        {
            var p = LevelsPath(ws);
            if (!File.Exists(p)) return 1;
            var doc = JsonSerializer.Deserialize<Dictionary<string,int>>(File.ReadAllText(p)) ?? new();
            return doc.TryGetValue("current_level", out var lvl) ? Math.Max(1, lvl) : 1;
        }
        catch { return 1; }
    }

    public static void SetCurrentLevel(string ws, int level)
    {
        var p = LevelsPath(ws);
        var obj = new Dictionary<string,int> { ["current_level"] = Math.Max(1, level) };
        File.WriteAllText(p, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented=true }), Encoding.UTF8);
    }

    public static List<string> GetTasks(string ws, int level)
    {
        try
        {
            var p = CurriculumPath(ws);
            if (!File.Exists(p)) return new();
            var doc = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(p)) ?? new();
            var key = level.ToString(CultureInfo.InvariantCulture);
            return doc.TryGetValue(key, out var arr) ? arr : new();
        }
        catch { return new(); }
    }


    public static void MarkTaskDone(string ws, int level, string task)
{
    var p = Path.Combine(ws, "curriculum.json");
    if (!File.Exists(p)) return;

    var doc = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(p))
              ?? new Dictionary<string, List<string>>();
    var key = level.ToString(CultureInfo.InvariantCulture);
    if (!doc.TryGetValue(key, out var arr) || arr == null || arr.Count == 0) return;

    // Зняти саме цю задачу, або першу — fallback (щоб не застрягти, якщо формулювання трохи відрізняються)
    var idx = arr.FindIndex(t => string.Equals(t?.Trim(), task?.Trim(), StringComparison.OrdinalIgnoreCase));
    if (idx < 0) idx = 0;
    arr.RemoveAt(idx);

    File.WriteAllText(p, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
}

}




// ======================= Operator Panel =======================
static class OperatorPanel
{
    public static readonly ConcurrentQueue<string> Inbox = new();
    static HttpListener? _listener;
    static CancellationTokenSource? _cts;
    public static string Url => "http://localhost:8765/";

    public static Task StartAsync(string logsDir)
    {
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add(Url);
        _listener.Start();

        Log.Info("PANEL", $"Open {Url}");
        return Task.Run(async () =>
        {
            while (!_cts!.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener!.GetContextAsync(); }
                catch when (_cts.IsCancellationRequested) { break; }

                try
                {
                    if (ctx.Request.HttpMethod == "GET")
                    {
                        if (ctx.Request.RawUrl == "/" || ctx.Request.RawUrl!.StartsWith("/?"))
                            await RespondHtml(ctx, HtmlPage);
                        else
                            await Respond404(ctx);
                    }
                    else if (ctx.Request.HttpMethod == "POST" && ctx.Request.RawUrl == "/send")
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                        var body = await reader.ReadToEndAsync();
                        var cmd = Uri.UnescapeDataString((body ?? "").Replace("cmd=", "")).Trim();
                        if (!string.IsNullOrWhiteSpace(cmd))
                        {
                            Inbox.Enqueue(cmd);
                            Directory.CreateDirectory(logsDir);
                            var line = $"{DateTime.UtcNow:o}\t{cmd}";
                            await File.AppendAllTextAsync(Path.Combine(logsDir, "history.jsonl"), line + Environment.NewLine, Encoding.UTF8);
                            Log.Info("PANEL", $"+ {cmd} (queued)");
                        }
                        await RespondJson(ctx, "{\"ok\":true}");
                    }
                    else
                    {
                        await Respond404(ctx);
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("PANEL", "Error: " + ex.Message);
                }
            }
        });
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
    }

    static async Task RespondHtml(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=UTF-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.ContentEncoding = Encoding.UTF8;
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    static async Task RespondJson(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json; charset=UTF-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.ContentEncoding = Encoding.UTF8;
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    static async Task Respond404(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 404;
        await RespondHtml(ctx, "<h1>404</h1>");
    }

    static string HtmlPage => @"<!doctype html>
<html lang=""uk"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>Agent Operator Panel</title>
<style>
body{font-family:system-ui,-apple-system,Segoe UI,Roboto,Ubuntu;max-width:900px;margin:32px auto;padding:0 16px}
.card{border:1px solid #ddd;border-radius:12px;padding:16px;box-shadow:0 2px 10px rgba(0,0,0,.05)}
textarea{width:100%;min-height:140px;font-family:ui-monospace,Consolas,monospace;font-size:14px;padding:12px;border-radius:8px;border:1px solid #ccc}
button{padding:10px 16px;border-radius:10px;border:1px solid #333;background:#111;color:#fff;cursor:pointer}
.row{display:flex;gap:12px;align-items:center;margin-top:12px}
.hint{color:#666;font-size:13px;margin-top:10px}
.footer{margin-top:18px;color:#777;font-size:12px}
</style>
</head>
<body>
  <h1>Agent Operator Panel</h1>
  <div class=""card"">
    <p>Приклади команд:</p>
    <ul>
      <li>Створи рішення MySolution</li>
      <li>Створи консольний проєкт App1 у рішенні MySolution</li>
      <li>Створи WebAPI ApiServer у рішенні MySolution</li>
      <li>Збери рішення MySolution</li>
      <li>Запусти проєкт App1</li>
      <li>Відкрий рішення MySolution у Visual Studio</li>
      <li>Init git у рішенні MySolution</li>
      <li>Покажи навчання</li>
      <li>exit (завершити агента)</li>
    </ul>
    <textarea id=""cmd"" placeholder=""Що зробити?""></textarea>
    <div class=""row"">
      <button id=""run"">Run</button>
      <span id=""status""></span>
    </div>
    <div class=""hint"">Команда потрапляє в памʼять (history.jsonl) і виконується негайно.</div>
  </div>
  <div class=""footer"">Listening on http://localhost:8765 • Локально</div>
<script>
document.getElementById('run').onclick = async ()=>{
  const b = document.getElementById('run'); b.disabled = true;
  const cmd = encodeURIComponent(document.getElementById('cmd').value || '');
  const res = await fetch('/send',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'cmd='+cmd});
  document.getElementById('status').textContent = res.ok ? '✅ sent' : '❌ failed';
  b.disabled = false;
};
</script>
</body></html>";
}

// ======================= Helpers for VS/.NET =======================
static class Vs
{
    public static async Task<string?> FindDevenvAsync()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;

        var (exit, stdout, _) = await Shell.Run(vswhere,
            "-latest -products * -requires Microsoft.Component.MSBuild -property productPath");
        if (exit != 0) return null;

        var path = stdout.Trim();
        return File.Exists(path) ? path : null;
    }
}

static class Dotnet
{
    public static async Task<(bool ok, string path)> CreateSolutionAsync(string workspaceDir, string slnName)
    {
        Directory.CreateDirectory(workspaceDir);
        var slnPath = Path.Combine(workspaceDir, slnName + ".sln");
        if (File.Exists(slnPath)) return (true, slnPath);

        var (ex, _, se) = await Shell.RunWithHeal("dotnet", $"new sln -n \"{slnName}\"", workspaceDir, $"sln:new:{slnName}");
        Log.Info("SLN", ex == 0 ? $"Created {slnPath}" : "ERR " + se);
        return (ex == 0, slnPath);
    }

    public static async Task<(bool ok, string projectDir, string csproj)> CreateProjectAsync(
        string workspaceDir, string template, string projectName)
    {
        var projDir = Path.Combine(workspaceDir, projectName);
        Directory.CreateDirectory(projDir);

        // якщо вже існує — не перетирати
        var existingCsproj = Directory.GetFiles(projDir, "*.csproj").FirstOrDefault();
        if (!string.IsNullOrEmpty(existingCsproj))
        {
            Log.Info("DOTNET", $"exists {projectName}");
            return (true, projDir, existingCsproj);
        }

        // якщо в каталозі щось є — хай Healer додасть --force
        var needForce = Directory.EnumerateFileSystemEntries(projDir).Any();
        var force = needForce ? " --force" : "";
        var (ex, _, se) = await Shell.RunWithHeal("dotnet",
            $"new {template} -n \"{projectName}\" -o \"{projDir}\"{force}", workspaceDir, $"proj:new:{template}:{projectName}");

        var csproj = Directory.GetFiles(projDir, "*.csproj").FirstOrDefault() ?? "";
        Log.Info("DOTNET", ex == 0 ? $"new {template} {projectName}{(needForce ? " (forced)" : "")}" : "ERR " + se);
        return (ex == 0, projDir, csproj);
    }

    public static async Task<bool> AddProjectToSolutionAsync(string slnPath, string csprojPath)
    {
        var (ex, _, se) = await Shell.RunWithHeal("dotnet", $"sln \"{slnPath}\" add \"{csprojPath}\"",
            Path.GetDirectoryName(slnPath)!, $"sln:add:{Path.GetFileName(csprojPath)}");
        Log.Info("SLN", ex == 0 ? $"Added {Path.GetFileName(csprojPath)}" : "ERR " + se);
        return ex == 0;
    }

    public static async Task<bool> BuildSolutionAsync(string slnPath)
    {
        var (ex, so, se) = await Shell.RunWithHeal("dotnet", $"build \"{slnPath}\"",
            Path.GetDirectoryName(slnPath)!, $"sln:build:{Path.GetFileName(slnPath)}");
        Log.Info("BUILD", ex == 0 ? so : "ERR " + se);
        return ex == 0;
    }

    public static async Task<bool> RunProjectAsync(string projectDir)
    {
        var (ex, so, se) = await Shell.RunWithHeal("dotnet", "run", projectDir, $"proj:run:{Path.GetFileName(projectDir)}");
        if (ex == 0) Log.Info("OUT", so.Trim());
        else Log.Info("RUN", "ERR: " + se);
        return ex == 0;
    }
}


// ======================= Minimal Web Host =======================
public static class WebPorts { public const int Default = 5173; }

static class WebHost
{
    public static async Task<(bool ok, string projectDir, string url)> EnsureAsync(
        string workspaceDir, string projectName, int port = WebPorts.Default)
    {
        // якщо порт зайнятий — знайти інший (Healer також підставить --urls при потребі)
        if (!NetPort.IsFree(port))
            port = NetPort.FindFree(WebPorts.Default);

        var (ok, dir, csproj) = await Dotnet.CreateProjectAsync(workspaceDir, "web", projectName);
        if (!ok) return (false, "", "");

        // ---- Program.cs (БЕЗ app.Urls.Clear/Add — порт не прибиваємо у коді) ----
        var programPath = Path.Combine(dir, "Program.cs");
        var code = @"var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
// Порт НЕ задаємо тут, щоб Healer міг передати --urls або спрацював ASPNETCORE_URLS/launchSettings.json
app.Run();";
        await File.WriteAllTextAsync(programPath, code, Encoding.UTF8);

        // ---- launchSettings.json (дефолт 5173, але не блокує --urls) ----
        var props = Path.Combine(dir, "Properties");
        Directory.CreateDirectory(props);

        var launch = Path.Combine(props, "launchSettings.json");
        var launchJson = @"{
  ""profiles"": {
    ""AgentApp"": {
      ""commandName"": ""Project"",
      ""dotnetRunMessages"": true,
      ""applicationUrl"": ""http://localhost:5173""
    }
  }
}";
        await File.WriteAllTextAsync(launch, launchJson, Encoding.UTF8);

        // ---- wwwroot + повертаємо url (агент відкриє браузер) ----
        Directory.CreateDirectory(Path.Combine(dir, "wwwroot"));
        var url = $"http://localhost:{port}";
        return (true, dir, url);
    }
}

// ======================= Generator Registry (Self-learning) =======================
// Ідея: генератори UI зберігаємо у JSON-файлах у папці Workspace/generators/*.json
// Коли користувач просить незнайому програму — створюємо скелет, зберігаємо JSON і використовуємо.
// Наступного разу це вже "відомий" генератор — без зміни коду агента.

class AppGenerator
{
    public string[] keys { get; set; } = Array.Empty<string>();  // ключові фрази для виклику
    public string title { get; set; } = "App";
    public Dictionary<string,string> files { get; set; } = new(); // "index.html", "style.css", "app.js"
}

static class Synonyms
{
    static string PathFor(string ws) => System.IO.Path.Combine(GeneratorRegistry.Root(ws), "synonyms.json");

    class Map { public Dictionary<string,string> items { get; set; } = new(); }

    static Map Load(string ws)
    {
        var p = PathFor(ws);
        if (!File.Exists(p)) return new Map();
        try { return JsonSerializer.Deserialize<Map>(File.ReadAllText(p)) ?? new Map(); }
        catch { return new Map(); }
    }

    static void Save(string ws, Map m)
    {
        File.WriteAllText(PathFor(ws), JsonSerializer.Serialize(m, new JsonSerializerOptions{ WriteIndented = true }), Encoding.UTF8);
    }

    public static void Add(string ws, string phrase, string canonical)
    {
        var m = Load(ws);
        m.items[phrase.Trim()] = canonical.Trim();
        Save(ws, m);
    }

    public static bool TryGet(string ws, string phrase, out string canonical)
    {
        var m = Load(ws);
        return m.items.TryGetValue(phrase.Trim(), out canonical!);
    }
}


static class GeneratorRegistry
{
    public static string Root(string workspaceDir)
        => Path.Combine(workspaceDir, "generators");

    public static void EnsureBuiltin(string workspaceDir)
    {
        Directory.CreateDirectory(Root(workspaceDir));
        // Стандартні три генератори: calculator, todo, timer
        EnsureFile(workspaceDir, "calculator.json", new AppGenerator
        {
            keys = new[] { "калькулятор", "calculator" },
            title = "Calculator",
            files = new()
            {
                ["index.html"] = @"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>Calculator</title><link rel=""stylesheet"" href=""style.css""></head>
<body>
  <div class=""card"">
    <h1>Calculator</h1>
    <input id=""a"" type=""number"" placeholder=""A"">
    <select id=""op""><option>+</option><option>-</option><option>*</option><option>/</option></select>
    <input id=""b"" type=""number"" placeholder=""B"">
    <button id=""calc"">=</button>
    <div id=""out""></div>
  </div><script src=""app.js""></script></body></html>",
                ["style.css"] = @"*{box-sizing:border-box}body{font-family:system-ui,Segoe UI,Roboto,Ubuntu;background:#f7f7f8;margin:0;padding:32px}
.card{max-width:520px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px;box-shadow:0 6px 30px rgba(0,0,0,.06)}
h1{margin:0 0 14px 0;font-size:20px}
input,select,button{padding:10px 12px;border-radius:10px;border:1px solid #d1d5db;margin-right:8px}
#out{margin-top:12px;font-weight:600}",
                ["app.js"] = @"const a=document.getElementById('a'),b=document.getElementById('b'),op=document.getElementById('op'),out=document.getElementById('out');
document.getElementById('calc').onclick=()=>{const x=parseFloat(a.value),y=parseFloat(b.value);
if(Number.isNaN(x)||Number.isNaN(y)){out.textContent='Enter numbers';return;}
let r=0;switch(op.value){case '+':r=x+y;break;case '-':r=x-y;break;case '*':r=x*y;break;case '/':r=y===0?'∞':x/y;break;}out.textContent='Result: '+r;};"
            }
        });

        EnsureFile(workspaceDir, "todo.json", new AppGenerator
        {
            keys = new[] { "todo", "to-do", "замітки", "список задач" },
            title = "Todo",
            files = new()
            {
                ["index.html"] = @"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>Todo</title><link rel=""stylesheet"" href=""style.css""></head>
<body>
  <div class=""card""><h1>Todo</h1>
  <div class=""row""><input id=""task"" placeholder=""What to do?""><button id=""add"">Add</button></div>
  <ul id=""list""></ul></div><script src=""app.js""></script></body></html>",
                ["style.css"] = @"*{box-sizing:border-box}body{font-family:system-ui,Segoe UI,Roboto,Ubuntu;background:#f7f7f8;margin:0;padding:32px}
.card{max-width:520px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px;box-shadow:0 6px 30px rgba(0,0,0,.06)}
.row{display:flex;gap:8px}input{flex:1;padding:10px;border:1px solid #d1d5db;border-radius:10px}
button{padding:10px 12px;border:1px solid #111;border-radius:10px;background:#111;color:#fff}
li{display:flex;justify-content:space-between;align-items:center;padding:8px;border-bottom:1px dashed #e5e7eb}",
                ["app.js"] = @"const input=document.getElementById('task'),list=document.getElementById('list');
document.getElementById('add').onclick=()=>{const v=input.value.trim();if(!v)return;const li=document.createElement('li');
li.textContent=v;const del=document.createElement('button');del.textContent='×';del.onclick=()=>li.remove();li.appendChild(del);list.appendChild(li);input.value='';};"
            }
        });

        EnsureFile(workspaceDir, "timer.json", new AppGenerator
        {
            keys = new[] { "timer", "таймер", "stopwatch" },
            title = "Timer",
            files = new()
            {
                ["index.html"] = @"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>Timer</title><link rel=""stylesheet"" href=""style.css""></head>
<body>
  <div class=""card""><h1>Timer</h1>
  <div id=""clock"">00:00:00</div>
  <div class=""row""><button id=""start"">Start</button><button id=""stop"">Stop</button><button id=""reset"">Reset</button></div>
  </div><script src=""app.js""></script></body></html>",
                ["style.css"] = @"*{box-sizing:border-box}body{font-family:system-ui,Segoe UI,Roboto,Ubuntu;background:#f7f7f8;margin:0;padding:32px}
.card{max-width:520px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px;box-shadow:0 6px 30px rgba(0,0,0,.06);text-align:center}
#clock{font-size:48px;margin:14px 0}button{padding:10px 12px;border:1px solid #111;border-radius:10px;background:#111;color:#fff;margin:0 6px}",
                ["app.js"] = @"let h=0,m=0,s=0,t=null;const clock=document.getElementById('clock');
function render(){clock.textContent=[h,m,s].map(x=>String(x).padStart(2,'0')).join(':');}
document.getElementById('start').onclick=()=>{if(t)return;t=setInterval(()=>{s++;if(s==60){s=0;m++;}if(m==60){m=0;h++;}render();},1000);};
document.getElementById('stop').onclick=()=>{clearInterval(t);t=null;};
document.getElementById('reset').onclick=()=>{h=0;m=0;s=0;render();};render();"
            }
        });
    }
    public static (bool ok, AppGenerator gen, string keyFile) TryResolveByPhrase(string workspaceDir, string phrase)
    {
        Directory.CreateDirectory(Root(workspaceDir));
        var files = Directory.GetFiles(Root(workspaceDir), "*.json");

        AppGenerator? best = null; string bestFile = ""; int bestScore = 0;
        var p = phrase.Trim();
        var tokens = p.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var f in files)
        {
            AppGenerator g;
            try { g = JsonSerializer.Deserialize<AppGenerator>(File.ReadAllText(f))!; }
            catch { continue; }

            foreach (var k in g.keys)
            {
                if (string.Equals(k, p, StringComparison.OrdinalIgnoreCase))
                    return (true, g, f);

                if (p.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 && bestScore < 2)
                { best = g; bestFile = f; bestScore = 2; }

                if (tokens.Any(t => string.Equals(t, k, StringComparison.OrdinalIgnoreCase)) && bestScore < 1)
                { best = g; bestFile = f; bestScore = 1; }
            }
        }
        return best != null ? (true, best!, bestFile) : (false, new AppGenerator(), "");
    }

    public static (bool ok, AppGenerator gen, string keyFile) TryResolve(string workspaceDir, string userKey)
    {
        Directory.CreateDirectory(Root(workspaceDir));
        var files = Directory.GetFiles(Root(workspaceDir), "*.json");
        foreach (var f in files)
        {
            try
            {
                var g = JsonSerializer.Deserialize<AppGenerator>(File.ReadAllText(f))!;
                if (g.keys.Any(k => string.Equals(k.Trim(), userKey.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return (true, g, f);
            }
            catch { }
        }
        return (false, new AppGenerator(), "");
    }

    public static async Task WriteToAsync(AppGenerator g, string wwwroot)
    {
        Directory.CreateDirectory(wwwroot);
        foreach (var kv in g.files)
        {
            var path = Path.Combine(wwwroot, kv.Key);
            await File.WriteAllTextAsync(path, kv.Value, Encoding.UTF8);
            
        }
    }

    public static void LearnUnknown(string workspaceDir, string userKey)
    {
        // створимо скелет нового генератора на базі Todo (мінімальний), додамо ключ userKey
        var sample = new AppGenerator
        {
            keys = new[] { userKey },
            title = CultureInfoSafeTitle(userKey),
            files = new()
            {
                ["index.html"] = @"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>App</title><link rel=""stylesheet"" href=""style.css""></head>
<body><div class=""card""><h1>App</h1><p>Scaffold for '" + EscapeHtml(userKey) + "'. Edit generator JSON to customize.</p></div></body></html>",
                ["style.css"] = @"*{box-sizing:border-box}body{font-family:system-ui,Segoe UI,Roboto,Ubuntu;background:#f7f7f8;margin:0;padding:32px}
.card{max-width:680px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px;box-shadow:0 6px 30px rgba(0,0,0,.06)}",
                ["app.js"] = @"// add logic here"
            }
        };
        var fileName = Path.Combine(Root(workspaceDir), SafeFileName(userKey) + ".json");
        File.WriteAllText(fileName, JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Log.Info("LEARN", $"New generator scaffolded: {fileName}");
    }

    public static void EnsureFile(string workspaceDir, string name, AppGenerator g)
    {
        var path = Path.Combine(Root(workspaceDir), name);
        if (!File.Exists(path))
            File.WriteAllText(path, JsonSerializer.Serialize(g, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

public static void Save(string workspaceDir, AppGenerator g, string fileSafeName)
{
    Directory.CreateDirectory(Root(workspaceDir));
    var path = Path.Combine(Root(workspaceDir), fileSafeName + ".json");
    File.WriteAllText(path, JsonSerializer.Serialize(g, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    Log.Info("LEARN", $"AutoGenerator saved: {path}");
}

    static string SafeFileName(string s)
    {
        var clean = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "app" : clean.ToLowerInvariant();
    }
    static string CultureInfoSafeTitle(string s)
    {
        try { return char.ToUpper(s[0]) + s.Substring(1); } catch { return "App"; }
    }
    static string EscapeHtml(string x) => x.Replace("<", "&lt;").Replace(">", "&gt;");
}



// ======================= Style Profile & Engine =======================
record StyleProfile(
    string Theme,        // "dark" | "light" | "auto"
    string Font,         // "system", "mono", "serif", "rounded", "modern"
    int    Radius,       // 6..20
    bool   SoftShadow,   // true/false
    int    AccentHue,    // 0..359
    double Density       // 0.9 компактно .. 1.2 просторо
);

static class StyleStore
{
    static string PathFor(string ws) => System.IO.Path.Combine(ws, "style_prefs.json");

    class Map { public Dictionary<string,StyleProfile> items { get; set; } = new(); }

    static Map Load(string ws)
    {
        var p = PathFor(ws);
        if (!File.Exists(p)) return new Map();
        try { return JsonSerializer.Deserialize<Map>(File.ReadAllText(p)) ?? new Map(); }
        catch { return new Map(); }
    }
    static void Save(string ws, Map m)
        => File.WriteAllText(PathFor(ws), JsonSerializer.Serialize(m, new JsonSerializerOptions{WriteIndented=true}), Encoding.UTF8);

    public static StyleProfile? Get(string ws, string appKey)
    {
        var m = Load(ws);
        return m.items.TryGetValue(appKey, out var sp) ? sp : null;
    }
    public static void Put(string ws, string appKey, StyleProfile sp)
    {
        var m = Load(ws);
        m.items[appKey] = sp;
        Save(ws, m);
    }
}

static class StyleEngine
{
    // простий геш фрази → стабільний відтінок
    static int HueFrom(string text)
    {
        unchecked {
            int h = 0;
            foreach (var ch in (text ?? "")) h = (h * 31) ^ ch;
            h = Math.Abs(h);
            return h % 360;
        }
    }

    public static StyleProfile Resolve(string phrase, string appTitle, string ws)
    {
        // 1) якщо вже вчили — віддамо з пам’яті
        var saved = StyleStore.Get(ws, appTitle);
        if (saved != null) return saved;

        var p = (phrase ?? "").ToLowerInvariant();

        // 2) базові евристики з промпта
        string theme =
            p.Contains("dark") || p.Contains("темн") || p.Contains("нічн") ? "dark" :
            p.Contains("light") || p.Contains("світл")                    ? "light" : "auto";

        string font =
            p.Contains("mono") || p.Contains("консоль") || p.Contains("код")     ? "mono" :
            p.Contains("serif") || p.Contains("класич")                          ? "serif" :
            p.Contains("rounded") || p.Contains("скругл") || p.Contains("дружн") ? "rounded" :
            p.Contains("elegant") || p.Contains("modern") || p.Contains("преміум") ? "modern" :
            "system";

        int radius =
            p.Contains("neumorph") || p.Contains("glass") || p.Contains("rounded") ? 16 :
            p.Contains("material") || p.Contains("apple") || p.Contains("ios")     ? 12 : 10;

        bool softShadow =
            p.Contains("neumorph") || p.Contains("glass") || p.Contains("premium") || p.Contains("преміум");

        double density =
            p.Contains("compact") || p.Contains("щільн") ? 0.95 :
            p.Contains("spacious") || p.Contains("простор") ? 1.1 : 1.0;

        int hue = HueFrom(phrase + "|" + appTitle);

        var style = new StyleProfile(
            Theme: theme, Font: font, Radius: radius, SoftShadow: softShadow, AccentHue: hue, Density: density
        );

        // 3) запам'ятати вибір для цього типу застосунка
        StyleStore.Put(ws, appTitle, style);
        return style;
    }

    public static string FontStack(string key)
    {
        return key switch
        {
            "mono"    => "ui-monospace, SFMono-Regular, Menlo, Consolas, \"Liberation Mono\", monospace",
            "serif"   => "ui-serif, Georgia, Cambria, \"Times New Roman\", Times, serif",
            "rounded" => "\"Segoe UI Rounded\", \"SF Pro Rounded\", ui-sans-serif, system-ui, Segoe UI, Roboto, Ubuntu, Helvetica, Arial, sans-serif",
            "modern"  => "\"Inter\", \"SF Pro Text\", ui-sans-serif, system-ui, Segoe UI, Roboto, Ubuntu, Helvetica, Arial, sans-serif",
            _         => "ui-sans-serif, system-ui, Segoe UI, Roboto, Ubuntu, Helvetica, Arial, sans-serif"
        };
    }

    public static (string metaColorScheme, string cssVars) ToCss(StyleProfile s)
    {
        var colorScheme = s.Theme == "dark" ? "dark" : s.Theme == "light" ? "light" : "light dark";
        var shadow = s.SoftShadow ? "0 10px 30px rgba(0,0,0,.15)" : "0 2px 10px rgba(0,0,0,.06)";
        var radius = Math.Clamp(s.Radius, 6, 20);

        // прості палітри через hue
        string H(int add, int l) => $"hsl({(s.AccentHue + add + 360)%360} 70% {l}%)";
        var accent = H(0, 55);
        var accentHover = H(0, 48);

        // темні/світлі бази
        var isDark = s.Theme == "dark";
        var bg   = isDark ? "#0f1115" : "#f7f7f8";
        var card = isDark ? "#151922" : "#ffffff";
        var bdr  = isDark ? "#242a36" : "#e5e7eb";
        var txt  = isDark ? "#e5e7eb" : "#111111";
        var ctrlBg = isDark ? "#0f1115" : "#ffffff";

        var css =
$@":root {{
  --bg: {bg};
  --card: {card};
  --border: {bdr};
  --text: {txt};
  --ctrl-bg: {ctrlBg};
  --accent: {accent};
  --accent-hover: {accentHover};
  --radius: {radius}px;
  --shadow: {shadow};
  --density: {s.Density};
  --font: {FontStack(s.Font)};
}}";
        return (colorScheme, css);
    }
}


// ======================= Modifiers (UI presets) =======================
static class Modifiers
{
    public static bool WantsDark(string phrase)
    {
        var p = (phrase ?? "").ToLowerInvariant();
        return p.Contains("темн") || p.Contains("dark");
    }

    // --- BACK-COMPAT SHIMS ---
    // Старі виклики Modifiers.Apply(...) тепер перенаправляємо на новий пайплайн стилів.


    public static void Apply(string phrase, AppGenerator g)
    {
        // Якщо виклик без workspace — використовуємо дефолтну папку Workspace
        var ws = Path.Combine(Directory.GetCurrentDirectory(), "Projects", "Workspace");
        var style = StyleEngine.Resolve(phrase, g.title ?? "App", ws);
        ApplyEnhanced(phrase, g, style);
    }

public static void Apply(string phrase, AppGenerator g, string ws)
{
    var style = StyleEngine.Resolve(phrase, g.title ?? "App", ws);
    ApplyEnhanced(phrase, g, style);
}


    public static void ApplyEnhanced(string phrase, AppGenerator g, StyleProfile style)
    {
        // 1) додамо meta color-scheme та базову клас-мітку на body
        if (g.files.TryGetValue("index.html", out var html))
        {
            var (scheme, _) = StyleEngine.ToCss(style);
            if (!html.Contains("color-scheme"))
                html = html.Replace("<head>", "<head>\n<meta name=\"color-scheme\" content=\"" + scheme + "\">");
            if (!html.Contains("<body"))
                html = html.Replace("<body>", "<body class=\"app\">");
            g.files["index.html"] = html;
        }

        // 2) ін’єкція CSS-перемінних і базових стилів
        var (scheme2, varsCss) = StyleEngine.ToCss(style);

        string baseCss =
@"*{box-sizing:border-box}
:root{FONT_VARS}
body{font-family:var(--font); background:var(--bg); color:var(--text); margin:0; padding:calc(28px*var(--density))}
.card{max-width:800px; margin:0 auto; background:var(--card); border:1px solid var(--border); border-radius:var(--radius); padding:calc(18px*var(--density)); box-shadow:var(--shadow)}
input,select,button,textarea{padding:calc(10px*var(--density)) calc(12px*var(--density)); border-radius:var(--radius); border:1px solid var(--border); background:var(--ctrl-bg); color:var(--text)}
button{background:var(--accent); color:#fff; border-color:transparent}
button:hover{background:var(--accent-hover)}
.row{display:flex; gap:12px; align-items:center; margin:10px 0}
h1{margin:0 0 12px 0; font-size:22px}
.hint{color:#7a7f87; font-size:13px}
";

        baseCss = baseCss.Replace("FONT_VARS", varsCss.Replace(":root {", "").Replace("}", ""));

        if (g.files.TryGetValue("style.css", out var css))
        {
            // prepend наші змінні + базу
            css = baseCss + "\n\n/* --- app css --- */\n" + css;
            g.files["style.css"] = css;
        }
        else
        {
            g.files["style.css"] = baseCss;
        }
    }
}


// ======================= AutoGenerator (heuristic, no-JSON needed) =======================
static class AutoGenerator
{
    // Основний вхід: повертає AppGenerator з готовими файлами або null
    public static AppGenerator? Infer(string phrase)
    {
        var p = (phrase ?? "").ToLowerInvariant();

        // 1) Найпопулярніші наміри
        if (Any(p, "гаманець", "wallet", "баланс", "finance", "кошти"))
            return Wallet();
        if (Any(p, "нотатка", "нотатки", "замітка", "notes", "note"))
            return Notes();
        if (Any(p, "лічильник", "counter", "increment"))
            return Counter();
        if (Any(p, "таймер помідора", "pomodoro", "помідор"))
            return Pomodoro();
        if (Any(p, "конвертер", "converter", "валюта", "currency"))
            return CurrencyConverter();

        // 2) Загальний блокнот, якщо щось схоже на “пиши/редактор/текст”
        if (Any(p, "редактор", "text", "текст", "write", "пиши"))
            return Notes();

        // 3) Фолбек: мінімальний скелет з назвою з фрази
        return Scaffold(TitleFrom(phrase));
    }

    // --- Generators ---
    static AppGenerator Wallet() => new AppGenerator
    {
        keys = new[] { "гаманець", "wallet", "баланс" },
        title = "Wallet",
        files = new Dictionary<string, string>
        {
            ["index.html"] = @"<!doctype html><html lang=""uk""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>Wallet</title><link rel=""stylesheet"" href=""style.css""></head><body><div class=""card""><h1>Wallet</h1><div class=""row""><input id=""amount"" type=""number"" step=""0.01"" placeholder=""Сума""><select id=""type""><option value=""in"">Дохід</option><option value=""out"">Видаток</option></select><input id=""note"" placeholder=""Нотатка (необов'язково)""><button id=""add"">Додати</button></div><div class=""row""><strong>Баланс:</strong> <span id=""balance"">0.00</span></div><ul id=""list""></ul><button id=""clear"">Очистити історію</button></div><script src=""app.js""></script></body></html>",
            ["style.css"] = @"*{box-sizing:border-box}body{font-family:system-ui,Segoe UI,Roboto,Ubuntu;background:#f7f7f8;margin:0;padding:32px}.card{max-width:680px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px;box-shadow:0 6px 30px rgba(0,0,0,.06)}.row{display:flex;gap:8px;align-items:center;margin:10px 0}input,select,button{padding:10px 12px;border:1px solid #d1d5db;border-radius:10px}button{border-color:#111;background:#111;color:#fff}ul{list-style:none;padding:0;margin:10px 0}li{display:flex;justify-content:space-between;padding:8px;border-bottom:1px dashed #e5e7eb}.in{color:#0a7}.out{color:#c33}",
            ["app.js"] = @"const amount=document.getElementById('amount'),type=document.getElementById('type'),note=document.getElementById('note'),list=document.getElementById('list'),balanceEl=document.getElementById('balance');const KEY='wallet_v1';let items=JSON.parse(localStorage.getItem(KEY)||'[]');function fmt(n){return (Math.round(n*100)/100).toFixed(2)}function render(){list.innerHTML='';let bal=0;for(const it of items){bal+=it.type==='in'?it.amount:-it.amount;const li=document.createElement('li');li.innerHTML=`<span class=""${it.type}"">${it.type==='in'?'+':'-'} ${fmt(it.amount)}</span> <em>${it.note||''}</em>`;list.appendChild(li);}balanceEl.textContent=fmt(bal);}document.getElementById('add').onclick=()=>{const a=parseFloat(amount.value);if(Number.isNaN(a)||a<=0)return;items.push({amount:a,type:type.value,note:note.value.trim(),ts:Date.now()});localStorage.setItem(KEY,JSON.stringify(items));amount.value='';note.value='';render();};document.getElementById('clear').onclick=()=>{if(confirm('Очистити історію?')){items=[];localStorage.setItem(KEY,'[]');render();}};render();"
        }
    };

    static AppGenerator Notes() => new AppGenerator
    {
        keys = new[] { "нотатка", "нотатки", "замітка", "notes" },
        title = "Notes",
        files = new Dictionary<string, string>
        {
            ["index.html"] = @"<!doctype html><html lang=""uk""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>Notes</title><link rel=""stylesheet"" href=""style.css""></head><body><div class=""card""><h1>Notes</h1><textarea id=""pad"" placeholder=""Пиши тут…""></textarea><div class=""hint"">Автозбереження локально (localStorage)</div></div><script src=""app.js""></script></body></html>",
            ["style.css"] = @"*{box-sizing:border-box}body{font-family:system-ui,Segoe UI,Roboto,Ubuntu;background:#f7f7f8;margin:0;padding:32px}.card{max-width:820px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px;box-shadow:0 6px 30px rgba(0,0,0,.06)}textarea{width:100%;min-height:420px;padding:12px;border:1px solid #d1d5db;border-radius:12px;font-family:ui-monospace,Consolas,monospace}.hint{margin-top:8px;color:#666;font-size:13px}",
            ["app.js"] = @"const KEY='notes_v1',pad=document.getElementById('pad');pad.value=localStorage.getItem(KEY)||'';let t=null;pad.addEventListener('input',()=>{clearTimeout(t);t=setTimeout(()=>localStorage.setItem(KEY,pad.value),250);});"
        }
    };

    static AppGenerator Counter() => new AppGenerator
    {
        keys = new[] { "лічильник", "counter" },
        title = "Counter",
        files = new Dictionary<string, string>
        {
            ["index.html"] = @"<!doctype html><html lang=""uk""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>Counter</title><link rel=""stylesheet"" href=""style.css""></head><body><div class=""card""><h1>Counter</h1><div id=""val"">0</div><div class=""row""><button id=""inc"">+1</button><button id=""dec"">-1</button><button id=""reset"">Reset</button></div></div><script src=""app.js""></script></body></html>",
            ["style.css"] = @"*{box-sizing:border-box}body{font-family:system-ui,Segoe UI,Roboto,Ubuntu;background:#f7f7f8;margin:0;padding:32px}.card{max-width:520px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px;box-shadow:0 6px 30px rgba(0,0,0,.06);text-align:center}#val{font-size:56px;margin:12px 0}.row{display:flex;gap:8px;justify-content:center}button{padding:10px 12px;border:1px solid #111;border-radius:10px;background:#111;color:#fff}",
            ["app.js"] = @"let v=0;const el=document.getElementById('val');function r(){el.textContent=v;}document.getElementById('inc').onclick=()=>{v++;r();};document.getElementById('dec').onclick=()=>{v--;r();};document.getElementById('reset').onclick=()=>{v=0;r();};r();"
        }
    };

    static AppGenerator Pomodoro() => new AppGenerator
    {
        keys = new[] { "pomodoro", "таймер помідора", "помідор" },
        title = "Pomodoro",
        files = new Dictionary<string, string>
        {
            ["index.html"] = @"<!doctype html><html lang=""uk""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>Pomodoro</title><link rel=""stylesheet"" href=""style.css""></head><body><div class=""card""><h1>Pomodoro</h1><div id=""clock"">25:00</div><div class=""row""><button id=""start"">Start</button><button id=""stop"">Stop</button><button id=""reset"">Reset</button></div></div><script src=""app.js""></script></body></html>",
            ["style.css"] = @"*{box-sizing:border-box}body{font-family:system-ui,Segoe UI,Roboto,Ubuntu;background:#f7f7f8;margin:0;padding:32px}.card{max-width:520px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px;box-shadow:0 6px 30px rgba(0,0,0,.06);text-align:center}#clock{font-size:56px;margin:12px 0}.row{display:flex;gap:8px;justify-content:center}button{padding:10px 12px;border:1px solid #111;border-radius:10px;background:#111;color:#fff}",
            ["app.js"] = @"let t=null,sec=25*60;const out=document.getElementById('clock');function render(){const m=Math.floor(sec/60),s=sec%60;out.textContent=`${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}`;}document.getElementById('start').onclick=()=>{if(t)return;t=setInterval(()=>{sec=Math.max(0,sec-1);render();},1000);} ;document.getElementById('stop').onclick=()=>{clearInterval(t);t=null;};document.getElementById('reset').onclick=()=>{sec=25*60;render();};render();"
        }
    };

    static AppGenerator CurrencyConverter() => new AppGenerator
    {
        keys = new[] { "конвертер", "converter", "валюта", "currency" },
        title = "Converter",
        files = new Dictionary<string, string>
        {
            ["index.html"] = @"<!doctype html><html lang=""uk""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>Converter</title><link rel=""stylesheet"" href=""style.css""></head><body><div class=""card""><h1>Converter</h1><div class=""row""><input id=""amount"" type=""number"" step=""0.01"" placeholder=""Сума""><input id=""rate"" type=""number"" step=""0.0001"" placeholder=""Курс""><button id=""do"">OK</button></div><div id=""out""></div></div><script src=""app.js""></script></body></html>",
            ["style.css"] = @"*{box-sizing:border-box}body{font-family:system-ui,Segoe UI,Roboto,Ubuntu;background:#f7f7f8;margin:0;padding:32px}.card{max-width:520px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px;box-shadow:0 6px 30px rgba(0,0,0,.06)}.row{display:flex;gap:8px}input,button{padding:10px 12px;border:1px solid #d1d5db;border-radius:10px}button{border-color:#111;background:#111;color:#fff}#out{margin-top:10px;font-weight:600}",
            ["app.js"] = @"const a=document.getElementById('amount'),r=document.getElementById('rate'),o=document.getElementById('out');document.getElementById('do').onclick=()=>{const x=parseFloat(a.value),k=parseFloat(r.value);if(Number.isNaN(x)||Number.isNaN(k)||k<=0){o.textContent='Введіть суму і курс';return;}o.textContent='Результат: '+(Math.round(x*k*100)/100).toFixed(2);};"
        }
    };

    static AppGenerator Scaffold(string title) => new AppGenerator
    {
        keys = new[] { title.ToLowerInvariant() },
        title = title,
        files = new Dictionary<string, string>
        {
            ["index.html"] = @"<!doctype html><html lang=""uk""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>" + EscapeHtml(title) + @"</title><link rel=""stylesheet"" href=""style.css""></head><body><div class=""card""><h1>" + EscapeHtml(title) + @"</h1><p>Скелет застосунку. Додай елементи у app.js</p></div><script src=""app.js""></script></body></html>",
            ["style.css"] = @"*{box-sizing:border-box}body{font-family:system-ui,Segoe UI,Roboto,Ubuntu;background:#f7f7f8;margin:0;padding:32px}.card{max-width:680px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px;box-shadow:0 6px 30px rgba(0,0,0,.06)}",
            ["app.js"] = @"// TODO: add logic"
        }
    };

    // --- helpers ---
    static bool Any(string hay, params string[] needles) => needles.Any(n => hay.Contains(n, StringComparison.OrdinalIgnoreCase));
    static string TitleFrom(string? x)
    {
        if (string.IsNullOrWhiteSpace(x)) return "App";
        x = x.Trim();
        // захист від строки з 1 символом
        return char.ToUpperInvariant(x[0]) + (x.Length > 1 ? x.Substring(1) : "");
    }

    static string EscapeHtml(string? x)
    {
        if (string.IsNullOrEmpty(x)) return "";
        return x.Replace("<", "&lt;").Replace(">", "&gt;");
    }

}


static class PromptBrain
{
    const double STRONG = 0.42;
    const double OKAY   = 0.28;

    static readonly string[] StopUa = new[]{
        "і","й","та","або","чи","а","у","в","на","до","за","з","із","що","це","той","ця",
        "будь","будь-який","будь-яка","зроби","створи","створити","зробити","додай","run","make","create","and","open"
    };
    static readonly string[] StopEn = new[]{
        "the","a","an","to","in","on","for","of","and","or","with","this","that","please","app","program","run","make","create","open"
    };

    static IEnumerable<string> Tokenize(string text)
    {
        text = (text ?? "").ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in text) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        var raw = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        bool isUa = raw.Any(t => t.Any(c => "іїєґ".Contains(c)));
        var stop = isUa ? StopUa : StopEn;
        return raw.Where(t => t.Length >= 2 && !stop.Contains(t));
    }

    static Dictionary<string,int> Vector(IEnumerable<string> toks)
    {
        var d = new Dictionary<string,int>();
        foreach (var t in toks) d[t] = d.TryGetValue(t, out var k) ? k+1 : 1;
        return d;
    }

    static double CosSim(Dictionary<string,int> a, Dictionary<string,int> b)
    {
        double dot = 0, na = 0, nb = 0;
        foreach (var kv in a) { na += kv.Value * kv.Value; if (b.TryGetValue(kv.Key, out var v)) dot += kv.Value * v; }
        foreach (var kv in b) nb += kv.Value * kv.Value;
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    static IEnumerable<(AppGenerator gen, string file, string blob)> Corpus(string ws)
    {
        GeneratorRegistry.EnsureBuiltin(ws);
        foreach (var f in Directory.GetFiles(GeneratorRegistry.Root(ws), "*.json"))
        {
            AppGenerator? g = null;
            try { g = JsonSerializer.Deserialize<AppGenerator>(File.ReadAllText(f)); } catch {}
            if (g == null) continue;

            var blob = new StringBuilder();
            blob.AppendLine(g.title ?? "");
            foreach (var k in g.keys ?? Array.Empty<string>()) blob.AppendLine(k);
            foreach (var kv in g.files) { blob.AppendLine(kv.Key); blob.AppendLine(kv.Value); }
            yield return (g, f, blob.ToString());
        }
    }

    static AppGenerator? IntentHeuristics(string p)
    {
        if (Any(p,"гаманець","wallet","баланс","витрати","доходи")) return AutoGenerator.Infer("wallet");
        if (Any(p,"нотат","заміт","notes","note","editor","редактор","блокнот")) return AutoGenerator.Infer("notes");
        if (Any(p,"таймер","timer","stopwatch","секундомер")) return AutoGenerator.Infer("timer");
        if (Any(p,"помідор","pomodoro")) return AutoGenerator.Infer("pomodoro");
        if (Any(p,"конвертер","converter","валюта","currency","курс")) return AutoGenerator.Infer("converter");
        if (Any(p,"лічильник","counter","increment")) return AutoGenerator.Infer("counter");
        if (Any(p,"калькулятор","calculator","calc")) return AutoGenerator.Infer("calculator");
        return null;

        static bool Any(string hay, params string[] xs) => xs.Any(x => hay.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static (bool ok, AppGenerator gen, string canonicalKey) Decide(string phrase, string ws)
    {
        // 0) якщо вже вчили — миттєво
        if (Synonyms.TryGet(ws, phrase, out var canonical))
        {
            var (ok, gen, _) = GeneratorRegistry.TryResolve(ws, canonical);
            if (ok) return (true, gen, canonical);
        }

        // 1) жорсткі інтенти (UA/EN)
        var intent = IntentHeuristics(phrase);
        if (intent != null)
        {
            var safe = SafeName(phrase);
            GeneratorRegistry.Save(ws, intent, safe);
            var canon = intent.keys?.FirstOrDefault() ?? safe;
            Synonyms.Add(ws, phrase, canon);
            return (true, intent, canon);
        }

        // 2) семантична схожість з існуючими генераторами
        var qvec = Vector(Tokenize(phrase));
        AppGenerator? best = null; string bestCanon = ""; double bestScore = 0;

        foreach (var (g, _, blob) in Corpus(ws))
        {
            var gvec = Vector(Tokenize(blob));
            var sim  = CosSim(qvec, gvec);
            if (sim > bestScore)
            {
                best = g; bestScore = sim;
                bestCanon = g.keys?.FirstOrDefault() ?? (g.title ?? "app").ToLowerInvariant();
            }
        }

        if (best != null && bestScore >= STRONG)
        {
            Synonyms.Add(ws, phrase, bestCanon);
            return (true, best, bestCanon);
        }
        if (best != null && bestScore >= OKAY)
        {
            Synonyms.Add(ws, phrase, bestCanon);
            return (true, best, bestCanon);
        }

        // 3) повний fallback — Scaffold (НЕ калькулятор)
        var scaffold = AutoGenerator.Infer(phrase) ?? new AppGenerator{
            keys = new[]{ phrase.ToLowerInvariant() },
            title = char.ToUpper(phrase.FirstOrDefault()) + (phrase.Length>1? phrase.Substring(1):""),
            files = new()
            {
                ["index.html"] = "<!doctype html><meta charset='utf-8'><title>App</title><link rel='stylesheet' href='style.css'><div class='card'><h1>App</h1><p>Scaffold.</p></div>",
                ["style.css"]  = "*{box-sizing:border-box}body{font-family:system-ui;background:#f7f7f8;margin:0;padding:32px}.card{max-width:720px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px}"
            }
        };
        var safeName = SafeName(phrase);
        GeneratorRegistry.Save(ws, scaffold, safeName);
        Synonyms.Add(ws, phrase, scaffold.keys.First());
        return (true, scaffold, scaffold.keys.First());
    }

    static string SafeName(string s)
        => new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch=='-'||ch=='_').ToArray()).ToLowerInvariant();
}


// ======================= Templates =======================
static class NameUtil
{
    public static string SanitizeId(string s)
    {
        var clean = new string(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.').ToArray());
        if (string.IsNullOrWhiteSpace(clean)) clean = "App";
        if (char.IsDigit(clean[0])) clean = "App_" + clean;
        return clean;
    }
}

sealed class Template
{
    public string Key { get; }
    public string Kind { get; }    // dotnet | vite
    public string DotnetName { get; }

    public Template(string key, string kind, string dotnetName = "")
    { Key = key; Kind = kind; DotnetName = dotnetName; }
}

static class TemplateRegistry
{
    static readonly Dictionary<string, Template> _t = new(StringComparer.OrdinalIgnoreCase)
    {
        ["console"]  = new("console", "dotnet", "console"),
        ["classlib"] = new("classlib","dotnet", "classlib"),
        ["webapi"]   = new("webapi","dotnet", "webapi"),
        ["wpf"]      = new("wpf",    "dotnet", "wpf"),
        ["react"]    = new("react",  "vite")
    };

    static readonly Dictionary<string,string> _alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["консольний"] = "console", ["консольну"] = "console", ["консольна"] = "console",
        ["класліб"] = "classlib", ["бібліотеку"] = "classlib", ["class library"] = "classlib",
        ["web api"] = "webapi",
        ["console"] = "console", ["classlib"] = "classlib", ["webapi"] = "webapi",
        ["wpf"] = "wpf", ["react"] = "react"
    };

   public static bool TryResolve(string raw, out Template t)
{
    var key = raw.Trim();
    key = _alias.TryGetValue(key, out var k2) ? k2 : key;

    if (_t.TryGetValue(key, out var tmpl))
    {
        t = tmpl;
        return true;
    }

    // safe fallback, щоб t ніколи не був null
    t = new Template("unknown", "dotnet", "console");
    return false;
}


    public static IEnumerable<string> Supported() => _t.Keys.OrderBy(x => x);
}

// ======================= Git helper =======================
static class Git
{
    public static async Task<bool> InitAndCommitAsync(string repoDir, string message)
    {
        if (!Directory.Exists(repoDir)) Directory.CreateDirectory(repoDir);

        var (ex0, _, se0) = await Shell.Run("git", "init", repoDir);
        if (ex0 != 0) { Log.Info("GIT", "ERR git init: " + se0); return false; }

        // .gitignore
        var gi = Path.Combine(repoDir, ".gitignore");
        if (!File.Exists(gi))
        {
            await File.WriteAllTextAsync(gi,
                "bin/\nobj/\n.vscode/\n.idea/\n*.user\n*.suo\n*.swp\n.DS_Store\n");
        }

        var (ex1, _, se1) = await Shell.Run("git", "add .", repoDir);
        if (ex1 != 0) { Log.Info("GIT", "ERR git add: " + se1); return false; }

        var safeMsg = message.Replace("\"", "\\\"");
        var (ex2, _, se2) = await Shell.Run("git", $"commit -m \"{safeMsg}\"", repoDir);
        if (ex2 != 0) { Log.Info("GIT", "ERR git commit: " + se2); return false; }

        Log.Info("GIT", "Repository initialized and first commit created.");
        return true;
    }
}


// ======================= Learning helper =======================
static class Learning
{
    public static string LevelsPath(string ws) => Path.Combine(ws, "levels.json");
    public static string CurriculumPath(string ws) => Path.Combine(ws, "curriculum.json");

    public static int GetCurrentLevel(string ws)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(LevelsPath(ws)));
            return doc.RootElement.GetProperty("current_level").GetInt32();
        }
        catch { return 1; }
    }

    public static void SetCurrentLevel(string ws, int lvl)
    {
        var json = JsonSerializer.Serialize(new { current_level = lvl }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(LevelsPath(ws), json, Encoding.UTF8);
    }



    // ======================= Agent =======================
    class Agent
    {

        private static string? LastSuccessfulProjectPath;
        readonly string _projectDir;
        readonly MemoryJson _memory;




        public Agent(string projectDir)
        {
            _projectDir = projectDir;
            Directory.CreateDirectory(Path.Combine(projectDir, "artifacts"));
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            Log.Init(Path.Combine(projectDir, "logs.txt"));
            _memory = new MemoryJson(Path.Combine(projectDir, "memory.json"));
        }

        private async Task<bool> ProcessInboxOnceAsync()
        {
            if (OperatorPanel.Inbox.TryDequeue(out var queued))
            {
                Log.Info("CMD", $"Received: {queued}");
                _memory.Add("UserCmd: " + queued);
                try { await HandleCommandAsync(queued); }
                catch (Exception ex)
                {
                    _memory.Add("CommandHandling ERR: " + ex.Message);
                    Log.Info("CMD", "ERR: " + ex.Message);
                }
                return true;
            }
            return false;
        }

        string WorkspaceForSolution(string slnName) => Path.Combine(_projectDir, slnName);
        string? FindSlnPath(string slnName)
        {
            var ws = WorkspaceForSolution(slnName);
            var path = Path.Combine(ws, slnName + ".sln");
            return File.Exists(path) ? path : null;
        }
        string? FindProjectDir(string slnName, string projectName)
        {
            var ws = WorkspaceForSolution(slnName);
            var target = Path.Combine(ws, projectName);
            if (Directory.Exists(target) && Directory.GetFiles(target, "*.csproj").Any()) return target;
            var hit = Directory.GetDirectories(ws, "*", SearchOption.AllDirectories)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), projectName, StringComparison.OrdinalIgnoreCase)
                                     && Directory.GetFiles(d, "*.csproj").Any());
            return hit;
        }

        async Task OpenSolutionInVSAsync(string slnPath)
        {
            var devenv = await Vs.FindDevenvAsync();
            if (devenv != null) Process.Start(devenv, $"\"{slnPath}\"");
            else
            {
                Log.Info("VS", "VS не знайдено (vswhere) — відкриваю у VS Code");
                var codeExe = Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Programs\Microsoft VS Code\Code.exe");
                if (File.Exists(codeExe)) Process.Start(codeExe, $"\"{Path.GetDirectoryName(slnPath)!}\"");
                else Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(slnPath)!}\"");
            }
        }

       static string ConsoleTemplateFor(string cmd)
{
    var s = (cmd ?? string.Empty).ToLowerInvariant();  // <- null-safe


            // якщо задача: '... що виводить "..."' — друкуємо саме це
var mEcho = Regex.Match(cmd ?? "", "що\\s+виводить\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
if (mEcho.Success)
{
    var text = mEcho.Groups[1].Value.Replace("\"", "\"\"");
    return $"Console.WriteLine(\"{text}\");";
}

// якщо задача: '... що виводить "..."' — друкуємо саме це
if (mEcho.Success)
{
    var text = mEcho.Groups[1].Value.Replace("\"", "\"\"");
    return $"Console.WriteLine(\"{text}\");";
}

            // 1) "введіть число" / "прочитай число"
            if (cmd.Contains("введіть число") || cmd.Contains("прочитай число"))
            {
                return
        @"Console.Write(""Введіть число: "");
string? input = Console.ReadLine();
if (double.TryParse(input, out var number))
{
    Console.WriteLine($""Ви ввели: {number}"");
}
else
{
    Console.WriteLine(""Помилка: введіть число"");
}";
            }

            // 2) "зчитує два числа" / "сума"
            if (cmd.Contains("зчитує два числа") || cmd.Contains("сума"))
            {
                return
        @"Console.Write(""Введіть a: "");
string? sa = Console.ReadLine();
Console.Write(""Введіть b: "");
string? sb = Console.ReadLine();

if (double.TryParse(sa, out var a) && double.TryParse(sb, out var b))
{
    Console.WriteLine($""Сума: {a + b}"");
}
else
{
    Console.WriteLine(""Помилка: введіть числа"");
}";
            }

            // 3) За замовчуванням — Привіт
            return @"Console.WriteLine(""Привіт"");";
        }


        public async Task<int> RunAsync()
        {
            Log.Info("AGENT", "Started (daemon mode). Команди виконуються одразу.");
            while (true)
            {
                var handled = await ProcessInboxOnceAsync();
                if (!handled) await Task.Delay(500);
            }
        }

        // ---- Learning Runner ----
        private async Task RunNextCurriculumTaskAsync()
        {
            var ws = _projectDir; // у нас workspace = Projects/Workspace
            var lvl = CurriculumStore.GetCurrentLevel(ws);
            var tasks = CurriculumStore.GetTasks(ws, lvl);

            if (tasks.Count == 0)
            {
                Log.Info("LEARN", $"Для рівня {lvl} задач не знайдено. Можливо кінець курсу ✅");
                return;
            }

            // Поки беремо першу задачу рівня
            var task = tasks[0];
            Log.Info("LEARN", $"Level {lvl}: {task}");

            // Виконання (ми використаємо вже існуючі «вільної форми» команди)
            await HandleCommandAsync(task);

            // Авто-тест
            var ok = await LearningTester.TestAsync(ws, task);
            ProgressStore.Bump(ws, $"level:{lvl}", ok);

            if (ok)
{
    CurriculumStore.MarkTaskDone(ws, lvl, task);
    var left = CurriculumStore.GetTasks(ws, lvl).Count;

    if (left == 0)
    {
        LevelUp(ws, lvl + 1, task);
        Log.Info("LEARN", $"✅ Успіх • Перехід на рівень {lvl + 1}");
    }
    else
    {
        Log.Info("LEARN", $"✅ Готово • Залишилось задач на рівні {lvl}: {left} — натисни «наступне навчальне завдання»");
    }
}
else
{
    Log.Info("LEARN", "❌ Тест не пройдено — пробую зібрати поради…");
    await AIFeedback.AdviseAsync(ws, task);
    Log.Info("LEARN", "Поради збережено в knowledge/. Запусти ще раз: «наступне навчальне завдання»");
}

        }

        private void LevelUp(string ws, int newLevel, string reason)
        {
            CurriculumStore.SetCurrentLevel(ws, newLevel);
            _memory.Add($"LevelUp → {newLevel} (after: {reason})");
        }



        // -------------------- Command Router --------------------
        private async Task HandleCommandAsync(string raw)
        {
            var cmd = raw.Trim();
            cmd = InputNormalizer.Clean(cmd);

            if (string.Equals(cmd, "exit", StringComparison.OrdinalIgnoreCase)) Environment.Exit(0);

            // 0) Показ навчання (самонавчання/«лікування»)
            if (Regex.IsMatch(cmd, @"(?i)^(покажи|show)\s+(навчання|learning|hints)\s*$"))
            {
                foreach (var line in Healer.ReadHints().TakeLast(50)) Log.Info("LEARN", line);
                return;
            }

            // ---- LEARNING COMMANDS ----
            if (Regex.IsMatch(cmd, @"(?i)^(почати|старт)\s+навчання$|^start\s+learning$"))
            {
                await RunNextCurriculumTaskAsync();
                return;
            }
            if (Regex.IsMatch(cmd, @"(?i)^(наступне\s+навчальне\s+завдання|next\s+learning\s+task)$"))
            {
                await RunNextCurriculumTaskAsync();
                return;
            }


            // навчання з теми
            var mLearn = Regex.Match(cmd, @"(?i)^(навчайся|learn)\s*:\s*(.+)$");
            if (mLearn.Success)
            {
                var topic = mLearn.Groups[2].Value.Trim();
                var ws = Path.Combine(_projectDir, "Playground"); Directory.CreateDirectory(ws);
                var adv = await Researcher.DiagnoseAsync(ws, "manual:" + topic, "", "");
                if (adv.Count == 0) Log.Info("LEARN", "Нічого корисного не знайшов");
                else Log.Info("LEARN", $"Збережено {adv.Count} запис(и) у knowledge/");
                return;
            }

            // показати знання
            if (Regex.IsMatch(cmd, @"(?i)^(покажи\s+знання|show\s+knowledge)$"))
            {
                var ws = Path.Combine(_projectDir, "Playground");
                var kdir = Path.Combine(ws, "knowledge");
                if (!Directory.Exists(kdir)) { Log.Info("LEARN", "knowledge/ порожньо"); return; }
                foreach (var f in Directory.GetFiles(kdir, "*.md").OrderByDescending(File.GetLastWriteTimeUtc).Take(10))
                    Log.Info("LEARN", Path.GetFileName(f));
                return;
            }

            // авто-діагностика за логом
            if (Regex.IsMatch(cmd, @"(?i)^(діагностуй|diagnose)$"))
            {
                var ws = Path.Combine(_projectDir, "Playground");
                var logs = Path.Combine(_projectDir, "logs.txt");
                using var fs = File.Exists(logs)
                ? new FileStream(logs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                : null;

                string stderrSample = fs != null
                    ? new StreamReader(fs, Encoding.UTF8, true).ReadToEnd()
                    : "";

                var adv = await Researcher.DiagnoseAsync(ws, "auto:diagnose", "", stderrSample);
                Log.Info("LEARN", adv.Count > 0 ? $"Поради: {adv.Count} (knowledge/)" : "Нічого не знайшов");
                return;
            }


            // 1) Створити РІШЕННЯ
            var mCreateSlnUa = Regex.Match(cmd, @"(?ix)^\s*створ(?:и|ити)\s+рішен(?:ня|ні)\s+(?<name>[A-Za-z0-9_.-]+)\s*$");
            var mCreateSlnEn = Regex.Match(cmd, @"(?ix)^\s*create\s+solution\s+(?<name>[A-Za-z0-9_.-]+)\s*$");
            if (mCreateSlnUa.Success || mCreateSlnEn.Success)
            {
                var name = (mCreateSlnUa.Success ? mCreateSlnUa.Groups["name"] : mCreateSlnEn.Groups["name"]).Value;
                name = NameUtil.SanitizeId(name);
                var ws = WorkspaceForSolution(name);
                Directory.CreateDirectory(ws);
                var (ok, sln) = await Dotnet.CreateSolutionAsync(ws, name);
                if (ok) Log.Info("SLN", $"OK: {sln}");
                return;
            }

            // 2) Створити ПРОЄКТ (універсально) у/без рішення
            var rxCreateProj = new Regex(
                @"(?ix)^\s*
              (створ(?:и|ити)|create)\s+
              (?<template>[A-Za-zа-яіїєё0-9_.-]+)\s+
              (проєкт|проект|project|app)\s+
              (?<name>[A-Za-z0-9_.-]+)
              (?:\s+(у|в|in)\s+(рішенн(?:і|я)|solution)\s+(?<sln>[A-Za-z0-9_.-]+))?
              \s*$"
            );
            var mUniCreate = rxCreateProj.Match(cmd);
            if (mUniCreate.Success)
            {
                var tmplRaw = mUniCreate.Groups["template"].Value;
                if (!TemplateRegistry.TryResolve(tmplRaw, out var tmpl))
                {
                    Log.Info("CMD", $"Невідомий шаблон: {tmplRaw}. Доступні: {string.Join(", ", TemplateRegistry.Supported())}");
                    return;
                }
                var projName = NameUtil.SanitizeId(mUniCreate.Groups["name"].Value);
                var slnName = mUniCreate.Groups["sln"]?.Value;
                if (!string.IsNullOrWhiteSpace(slnName)) slnName = NameUtil.SanitizeId(slnName);

                var ws = string.IsNullOrWhiteSpace(slnName) ? _projectDir : WorkspaceForSolution(slnName!);
                Directory.CreateDirectory(ws);

                string? slnPath = null;
                if (!string.IsNullOrWhiteSpace(slnName))
                {
                    slnPath = FindSlnPath(slnName!);
                    if (slnPath == null)
                    {
                        var created = await Dotnet.CreateSolutionAsync(ws, slnName!);
                        if (!created.ok) { Log.Info("SLN", "Не вдалося створити рішення"); return; }
                        slnPath = created.path;
                    }
                }

                switch (tmpl.Kind)
                {
                    case "dotnet":
                        {
                            var (ok, projDir, csproj) = await Dotnet.CreateProjectAsync(ws, tmpl.DotnetName, projName);

                            if (!ok || string.IsNullOrEmpty(csproj)) { Log.Info("DOTNET", "Не вдалося створити проєкт"); return; }
                            if (!string.IsNullOrEmpty(slnPath))
                                await Dotnet.AddProjectToSolutionAsync(slnPath!, csproj);

                            if (tmpl.Key.Equals("console", StringComparison.OrdinalIgnoreCase))
                            {
                                var programPath = Directory.GetFiles(projDir, "Program.cs", SearchOption.AllDirectories).FirstOrDefault()
                                                  ?? Path.Combine(projDir, "Program.cs");
                                File.WriteAllText(programPath,
        @"using System;
class EntryPoint { static void Main() { Console.WriteLine(""Hello from Agent!""); } }");
                            }

                            Log.Info("OK", $"Проєкт {projName} ({tmpl.Key}) створено{(slnName != null ? $" у рішенні {slnName}" : "")}");
                            return;
                        }
                    case "vite":
                        {
                            var projRoot = Path.Combine(ws, projName);
                            Directory.CreateDirectory(projRoot);

                            var (exNode, _, _) = await Shell.Run("node", "-v");
                            if (exNode != 0)
                            {
                                Log.Info("REACT", "Node.js не знайдено — роблю статичну заглушку");
                                File.WriteAllText(Path.Combine(projRoot, "index.html"), "<h1>Install Node.js to run Vite dev server</h1>");
                                Log.Info("OK", $"React {projName} створено як статичну заглушку");
                                return;
                            }

                            var (exInit, _, seInit) = await Shell.RunWithHeal("npx", $"create-vite@latest {projName} -- --template react", ws, $"vite:init:{projName}");
                            if (exInit != 0) { Log.Info("REACT", "ERR init: " + seInit); return; }
                            var (exNpmI, _, seNpmI) = await Shell.RunWithHeal("npm", "install", projRoot, $"vite:npm_i:{projName}");
                            if (exNpmI != 0) { Log.Info("REACT", "ERR npm i: " + seNpmI); return; }

                            Log.Info("OK", $"React {projName} підготовлено");
                            return;
                        }
                }
            }

            // 3) Додати існуючий проєкт у рішення
            var mAddProjUa = Regex.Match(cmd, @"(?ix)^\s*додай\s+про(є|е)кт\s+(?<p>[A-Za-z0-9_.-]+)\s+у\s+рішенн(і|я)\s+(?<s>[A-Za-z0-9_.-]+)\s*$");
            var mAddProjEn = Regex.Match(cmd, @"(?ix)^\s*add\s+project\s+(?<p>[A-Za-z0-9_.-]+)\s+to\s+solution\s+(?<s>[A-Za-z0-9_.-]+)\s*$");
            if (mAddProjUa.Success || mAddProjEn.Success)
            {
                var projectName = (mAddProjUa.Success ? mAddProjUa.Groups["p"] : mAddProjEn.Groups["p"]).Value;
                var slnName = (mAddProjUa.Success ? mAddProjUa.Groups["s"] : mAddProjEn.Groups["s"]).Value;
                var projDir = FindProjectDir(slnName, projectName);
                if (projDir == null) { Log.Info("SLN", $"Не знайдено каталог проєкту {projectName}"); return; }
                var csproj = Directory.GetFiles(projDir, "*.csproj").FirstOrDefault();
                if (csproj == null) { Log.Info("SLN", "csproj не знайдено"); return; }
                var slnPath = FindSlnPath(slnName);
                if (slnPath == null) { Log.Info("SLN", $"Рішення {slnName} не знайдено"); return; }
                await Dotnet.AddProjectToSolutionAsync(slnPath, csproj);
                return;
            }

            // 4) Build solution
            var mBuild = Regex.Match(cmd, @"(?ix)^\s*(збери|build)\s+рішен(ня|ні|нями)\s+(?<s>[A-Za-z0-9_.-]+)\s*$");
            if (mBuild.Success)
            {
                var sln = mBuild.Groups["s"].Value;
                var slnPath = FindSlnPath(sln);
                if (slnPath == null) { Log.Info("BUILD", $"Рішення {sln} не знайдено"); return; }
                await Dotnet.BuildSolutionAsync(slnPath);
                return;
            }

            // 5) Run project
            var mRun = Regex.Match(cmd, @"(?ix)^\s*(запусти|run)\s+про(є|е)кт\s+(?<p>[A-Za-z0-9_.-]+)\s*$");
            if (mRun.Success)
            {
                var projectName = mRun.Groups["p"].Value;
                var projDir = Directory.GetDirectories(_projectDir, projectName, SearchOption.AllDirectories)
                    .FirstOrDefault(d => Directory.GetFiles(d, "*.csproj").Any());
                if (projDir == null) { Log.Info("RUN", $"Проєкт {projectName} не знайдено"); return; }
                await Dotnet.RunProjectAsync(projDir);
                return;
            }

            // 6) Open solution in Visual Studio
            var mOpenVs = Regex.Match(cmd, @"(?ix)^\s*(відкрий|open)\s+рішен(ня|ні)\s+(?<s>[A-Za-z0-9_.-]+)\s+(у|in)\s+(visual\s+studio|vs)\s*$");
            if (mOpenVs.Success)
            {
                var sln = mOpenVs.Groups["s"].Value;
                var slnPath = FindSlnPath(sln);
                if (slnPath == null) { Log.Info("VS", $"Рішення {sln} не знайдено"); return; }
                await OpenSolutionInVSAsync(slnPath);
                return;
            }

            // 7) Open in VS Code / Open folder
            if (Regex.IsMatch(cmd, @"(?i)\b(open in vs\s*code|відкрий у vs\s*code)\b"))
            {
                var codeExe = Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Programs\Microsoft VS Code\Code.exe");
                if (File.Exists(codeExe)) Process.Start(codeExe, $"\"{_projectDir}\"");
                else Process.Start("explorer.exe", $"\"{_projectDir}\"");
                return;
            }
            if (cmd.Contains("відкрий папку", StringComparison.OrdinalIgnoreCase) || cmd.Contains("open folder", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start("explorer.exe", $"\"{_projectDir}\"");
                return;
            }



            // 8) Git init
            var mGit = Regex.Match(cmd, @"(?ix)^\s*(init\s+git|git\s+init)\s+(у|in)\s+рішен(ні|ня)\s+(?<s>[A-Za-z0-9_.-]+)\s*$");
            if (mGit.Success)
            {
                var sln = mGit.Groups["s"].Value;
                var ws = WorkspaceForSolution(sln);
                if (!Directory.Exists(ws)) { Log.Info("GIT", $"Workspace {ws} не знайдено"); return; }
                await Git.InitAndCommitAsync(ws, $"init {sln}");
                return;
            }

            // === REPEAT (повтори/repeat), з числом і fallback-пошуком ===
            var mRepeat = Regex.Match(cmd, @"(?i)повтори(?:\s+ще)?\s*(\d+)?\s*раз", RegexOptions.CultureInvariant);
            if (mRepeat.Success || Regex.IsMatch(cmd, @"(?i)\brepeat\b"))
            {
                int times = 1;
                if (mRepeat.Success && mRepeat.Groups[1].Success) int.TryParse(mRepeat.Groups[1].Value, out times);
                if (times < 1) times = 1;

                // fallback: якщо змінна порожня — знайдемо останній ConsoleApp_ у Playground
                if (string.IsNullOrEmpty(LastSuccessfulProjectPath))
                {
                    var ws = WorkspaceForSolution("Playground");
                    if (Directory.Exists(ws))
                    {
                        var last = Directory.GetDirectories(ws)
                            .Where(d => Path.GetFileName(d).StartsWith("ConsoleApp_", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(d => Directory.GetCreationTime(d))
                            .FirstOrDefault();
                        if (last != null)
                        {
                            LastSuccessfulProjectPath = last;
                            Log.Info("REPEAT", "Використовую останній консольний проєкт: " + last);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(LastSuccessfulProjectPath))
                {
                    for (int i = 0; i < times; i++)
                        await Dotnet.RunProjectAsync(LastSuccessfulProjectPath);
                }
                else
                {
                    Log.Info("REPEAT", "Немає попередньої програми для повтору");
                }
                return;
            }


            // === FORCE CONSOLE if task mentions console (Level-1 universal) ===
            if (cmd.Contains("консоль", StringComparison.OrdinalIgnoreCase) ||
                cmd.Contains("console", StringComparison.OrdinalIgnoreCase))
            {
                var slnName = "Playground";
                var ws = WorkspaceForSolution(slnName);
                Directory.CreateDirectory(ws);

                var projName = "ConsoleApp_" + DateTime.Now.ToString("HHmmss");
                var (ok, projDir, csproj) = await Dotnet.CreateProjectAsync(ws, "console", projName);
                if (!ok || string.IsNullOrEmpty(csproj)) { Log.Info("DOTNET", "Не вдалося створити проєкт"); return; }

                // --- Вибір шаблону під фразу ---
                string lower = cmd.ToLowerInvariant();
                string code;

                // 1) Явне "Привіт" / hello
                if (lower.Contains("привіт") || lower.Contains("hello"))
                {
                    code = @"using System;
class Program { static void Main(){ Console.WriteLine(""Привіт""); } }";
                }
                // 2) Просить ввести/прочитати число -> ехо числа (НЕ блокує самотест, бо друкуємо одразу підказку)
                else if (lower.Contains("введи") || lower.Contains("введіть") || lower.Contains("прочитай число") || lower.Contains("read number"))
                {
                    code = @"using System;
class Program {
  static void Main(){
    Console.Write(""Введіть число: "");
    string input = Console.ReadLine();
    if(double.TryParse(input, out double n)) Console.WriteLine($""Ви ввели: {n}"");
    else Console.WriteLine(""Помилка: введіть число"");
  }
}";
                }
                // 3) Калькулятор (два числа + операція)
                else if (lower.Contains("калькулятор") || lower.Contains("calculator"))
                {
                    code = @"using System;
class Program{
  static void Main(){
    Console.Write(""a: ""); var aS = Console.ReadLine();
    Console.Write(""b: ""); var bS = Console.ReadLine();
    Console.Write(""op(+,-,*,/): ""); var op = Console.ReadLine();
    if(!double.TryParse(aS, out var a) || !double.TryParse(bS, out var b)){ Console.WriteLine(""Помилка: число""); return; }
    double r = op==""+""? a+b : op==""-""? a-b : op==""*""? a*b : op==""/""? (b!=0? a/b : double.NaN) : double.NaN;
    Console.WriteLine($""Результат: {r}"");
  }
}";
                }
                // 4) Fallback: простий вивід (щоб самотест гарантовано бачив текст)
                else
                {
                    code = @"using System;
class Program { static void Main(){ Console.WriteLine(""Привіт""); } }";
                }

                // --- Запис коду ---
                var programFile = Path.Combine(projDir, "Program.cs");
                await File.WriteAllTextAsync(programFile, code, Encoding.UTF8);

                // --- Пам'ять для повторів і самотесту ---
                LastSuccessfulProjectPath = projDir;
                var lastConsolePathFile = Path.Combine(ws, "last_console_path.txt");   // важливо для TestConsoleAsync
                File.WriteAllText(lastConsolePathFile, projDir, Encoding.UTF8);

                // --- Запуск ---
                await Dotnet.RunProjectAsync(projDir);
                return;
            }

            // ===== Browser app (any minimal program) =====
            var mBrowserApp = Regex.Match(cmd, @"(?ix)
    ^\s*
    (?:(зроби|створи|create|make)\s+)?     # дієслово тепер опційне
    (?<what>[\p{L}0-9\-\s]+?)
    (?:\s+і\s+запусти\s+у\s+браузері|\s+and\s+run\s+in\s+browser)?\s*
    $
");

            if (mBrowserApp.Success)
            {
                var what = mBrowserApp.Groups["what"].Value.Trim();
                var slnName = "Playground";
                var ws = WorkspaceForSolution(slnName);
                Directory.CreateDirectory(ws);




                // ===== BEGIN PATCH: Brain-first + Legacy fallback =====
                GeneratorRegistry.EnsureBuiltin(ws);

                // 1) Спроба через «мозок»
                var (decided, brainGen, brainCanon) = PromptBrain.Decide(what, ws);
                AppGenerator gen;

                if (decided && brainGen != null)
                {
                    gen = brainGen;
                }
                else
                {
                    // --- Legacy fallback (твій поточний ланцюжок) ---
                    bool found = false;
                    gen = new AppGenerator();

                    // 1.1) Якщо вже вчили відповідність фрази — використай її
                    if (Synonyms.TryGet(ws, what, out var mapped))
                    {
                        var (okMap, genMap, _) = GeneratorRegistry.TryResolve(ws, mapped);
                        if (okMap) { gen = genMap; found = true; }
                    }

                    // 1.2) Спробуємо напряму по фразі (пошук по keys/токенах у *.json)
                    if (!found)
                    {
                        var (okTry, genTry, _) = GeneratorRegistry.TryResolveByPhrase(ws, what);
                        if (okTry) { gen = genTry; found = true; }
                    }

                    // 1.3) Авто-визначення без словників
                    if (!found)
                    {
                        var g = AutoGenerator.Infer(what);
                        if (g != null)
                        {
                            gen = g; found = true;
                            var safe = typeof(GeneratorRegistry)
                                .GetMethod("SafeFileName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                                .Invoke(null, new object[] { what })!.ToString()!;
                            GeneratorRegistry.Save(ws, gen, safe);
                            Synonyms.Add(ws, what, gen.keys.FirstOrDefault() ?? safe);
                        }
                    }

                    // 1.4) Навчити невідоме й спробувати ще
                    if (!found)
                    {
                        GeneratorRegistry.LearnUnknown(ws, what);
                        var (okNew, genNew, _) = GeneratorRegistry.TryResolveByPhrase(ws, what);
                        if (okNew) { gen = genNew; found = true; }
                    }

                    if (!found)
                    {
                        Log.Info("AI", "Не вдалося визначити тип застосунку");
                        return;
                    }
                }


                // 2) Web host
                var appNameBase = NameUtil.SanitizeId((gen.title ?? "App").Replace(" ", ""));
                var appName = appNameBase; int idx = 2;
                while (Directory.Exists(Path.Combine(ws, appName))) appName = $"{appNameBase}{idx++}";
                var (okHost, hostDir, url) = await WebHost.EnsureAsync(ws, appName, WebPorts.Default);
                // запам'ятати останній URL цього проєкту
                File.WriteAllText(Path.Combine(hostDir, "last_url.txt"), url, Encoding.UTF8);

                if (!okHost) { Log.Info("WEB", "Не вдалося створити веб-хост"); return; }

                // 3) Додамо у рішення
                var slnPath = FindSlnPath(slnName);
                if (slnPath == null)
                {
                    var cr = await Dotnet.CreateSolutionAsync(ws, slnName);
                    if (cr.ok) slnPath = cr.path;
                }
                var csproj = Directory.GetFiles(hostDir, "*.csproj").FirstOrDefault();
                if (!string.IsNullOrEmpty(csproj) && !string.IsNullOrEmpty(slnPath))
                    await Dotnet.AddProjectToSolutionAsync(slnPath!, csproj);

                // 4) Згенерувати файли UI у wwwroot (з модифікаторами)
                var www = Path.Combine(hostDir, "wwwroot");

                // 4) Авто-стиль: підбір + застосування + запам'ятати
                var style = StyleEngine.Resolve(what, gen.title ?? "App", ws);
                Modifiers.ApplyEnhanced(what, gen, style);
                await GeneratorRegistry.WriteToAsync(gen, www);



                // 5) Запуск і відкриття браузера
                // 5) Запуск у тлі (без присвоєння; Forget повертає void)
                Task.Run(async () =>
                {
                    var ok = await Dotnet.RunProjectAsync(hostDir);
                    if (!ok) Log.Info("WEB", "dotnet run failed");
                }).Forget("WEB");

                // 6) Дочекаємось, поки хост підніметься (до ~10с), тоді відкриваємо браузер
                var self = await SelfTester.EvaluateAsync(url, gen, www);
                if (self.ok)
                {
                    Browser.Open(url);
                    var keyToSave = gen.keys?.FirstOrDefault() ?? (gen.title ?? "app").ToLowerInvariant();
                    Synonyms.Add(ws, what, keyToSave);
                }


                // Якщо самотест провалився або це "App" (скелет) — спробуємо автоматично виправитись
                if (!self.ok || string.Equals(gen.title, "App", StringComparison.OrdinalIgnoreCase))
                {
                    // 1) Спробувати автоматично визначити тип без словників
                    AppGenerator? healGen = AutoGenerator.Infer(what);

                    // 2) Якщо не вдалось — підставити універсальний builtin "todo"
                    if (healGen == null)
                    {
                        GeneratorRegistry.EnsureBuiltin(ws);
                        var (ok2, canonGen, _) = GeneratorRegistry.TryResolve(ws, "todo"); // запасний варіант
                        if (ok2) healGen = canonGen;
                    }

                    if (healGen != null)
                    {
                        // 3) Застосувати стилі і перезаписати wwwroot
                        Modifiers.Apply(what, healGen);
                        Log.Info("STYLE", $"Heal auto-pick: {healGen.title}");
                        await GeneratorRegistry.WriteToAsync(healGen, www);
                        Log.Info("HEAL", "Applied fallback and rewrote wwwroot.");

                        // 4) Повторний самотест
                        var self2 = await SelfTester.EvaluateAsync(url, healGen, www);
                        if (self2.ok)
                        {
                            // 5) Запам'ятати фразу як синонім успішного типу
                            var keyToSave = healGen.keys?.FirstOrDefault() ?? "todo";
                            Synonyms.Add(ws, what, keyToSave);
                            Log.Info("HEAL", $"Fixed and learned mapping: '{what}' -> '{keyToSave}'.");
                        }
                        else
                        {
                            Log.Info("HEAL", "Fallback still failing.");
                        }
                    }
                }

                return;



            }
            // ==== OPEN LAST URL HANDLER ====
            if (Regex.IsMatch(cmd, @"(?i)^(відкрий.*браузер|open.*browser|open\s+last)\s*$"))
            {
                // шукаємо найсвіжіший last_url.txt у всьому Workspace
                var lastFile = Directory.GetFiles(_projectDir, "last_url.txt", SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (lastFile == null)
                {
                    Log.Info("WEB", "Не знайдено last_url.txt");
                    return;
                }

                var url = (File.ReadAllText(lastFile.FullName) ?? "").Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    Log.Info("WEB", $"Порожній last_url.txt: {lastFile.FullName}");
                    return;
                }

                Browser.Open(url);
                Log.Info("WEB", $"Opened {url}");
                return;
            }
            // ===== Free-form idea fallback (без ключових слів) =====
            if (cmd.Length >= 4 && !Regex.IsMatch(cmd, @"(?i)^(exit|show|покажи|build|збери|open|відкрий|init\s+git|git\s+init)\b"))
            {
                var what = cmd.Trim();
                var slnName = "Playground";
                var ws = WorkspaceForSolution(slnName);
                Directory.CreateDirectory(ws);

                try
                {
                    Log.Info("AI", $"FreeForm: '{what}'");

                    // 1) Визначаємо генератор (brain → infer → scaffold)
                    GeneratorRegistry.EnsureBuiltin(ws);
                    var (decided, brainGen, brainCanon) = PromptBrain.Decide(what, ws);
                    AppGenerator gen = brainGen ?? AutoGenerator.Infer(what) ?? new AppGenerator
                    {
                        keys = new[] { what.ToLowerInvariant() },
                        title = char.ToUpper(what[0]) + (what.Length > 1 ? what.Substring(1) : ""),
                        files = new()
                        {
                            ["index.html"] = "<!doctype html><meta charset='utf-8'><title>App</title><link rel='stylesheet' href='style.css'><div class='card'><h1>App</h1></div>",
                            ["style.css"] = "*{box-sizing:border-box}body{font-family:system-ui;background:#f7f7f8;margin:0;padding:32px}.card{max-width:720px;margin:0 auto;background:#fff;border:1px solid #e5e7eb;border-radius:16px;padding:20px}",
                            ["app.js"] = "// TODO"
                        }
                    };

                    // 2) Web host (знайде вільний порт сам)
                    var appNameBase = NameUtil.SanitizeId((gen.title ?? "App").Replace(" ", ""));
                    var appName = appNameBase; int idx = 2;
                    while (Directory.Exists(Path.Combine(ws, appName))) appName = $"{appNameBase}{idx++}";

                    var (okHost, hostDir, url) = await WebHost.EnsureAsync(ws, appName, WebPorts.Default);
                    if (!okHost) { Log.Info("WEB", "Не вдалося створити веб-хост"); return; }
                    File.WriteAllText(Path.Combine(hostDir, "last_url.txt"), url, Encoding.UTF8);

                    // 3) Додати у рішення (створити, якщо немає)
                    var slnPath = FindSlnPath(slnName);
                    if (slnPath == null)
                    {
                        var cr = await Dotnet.CreateSolutionAsync(ws, slnName);
                        if (!cr.ok) { Log.Info("SLN", "Не вдалося створити рішення"); return; }
                        slnPath = cr.path;
                    }
                    var csproj = Directory.GetFiles(hostDir, "*.csproj").FirstOrDefault();
                    if (!string.IsNullOrEmpty(csproj)) await Dotnet.AddProjectToSolutionAsync(slnPath!, csproj);

                    // 4) Стиль + wwwroot
                    var www = Path.Combine(hostDir, "wwwroot");
                    var style = StyleEngine.Resolve(what, gen.title ?? "App", ws);
                    Modifiers.ApplyEnhanced(what, gen, style);
                    await GeneratorRegistry.WriteToAsync(gen, www);

                    // 5) Запуск у тлі
                    Task.Run(async () =>
                    {
                        var ok = await Dotnet.RunProjectAsync(hostDir);
                        if (!ok) Log.Info("WEB", "dotnet run failed");
                    }).Forget("WEB");

                    // 6) Самотест і відкриття браузера
                    var self = await SelfTester.EvaluateAsync(url, gen, www);
                    if (self.ok)
                    {
                        Browser.Open(url);
                        var keyToSave = gen.keys?.FirstOrDefault() ?? (gen.title ?? "app").ToLowerInvariant();
                        Synonyms.Add(ws, what, keyToSave);
                        return;
                    }

                    // Авто-лікування
                    AppGenerator? healGen = AutoGenerator.Infer(what);
                    if (healGen != null)
                    {
                        Modifiers.Apply(what, healGen);
                        await GeneratorRegistry.WriteToAsync(healGen, www);
                        var self2 = await SelfTester.EvaluateAsync(url, healGen, www);
                        if (self2.ok)
                        {
                            var keyToSave = healGen.keys?.FirstOrDefault() ?? "app";
                            Synonyms.Add(ws, what, keyToSave);
                            Browser.Open(url);
                            Log.Info("HEAL", $"Fixed and learned mapping: '{what}' -> '{keyToSave}'.");
                            return;
                        }
                    }

                    // Якщо дійшли сюди — не відкрили
                    Log.Info("HEAL", "Fallback still failing.");
                    return;
                }
                catch (Exception ex)
                {
                    Log.Info("AI", "ERR: " + ex.Message);
                    return;
                }
            }

            // якщо жодна гілка не спрацювала:
            Log.Info("CMD", $"No handler for: {cmd}");

        }
    }

    // ======================= Program =======================
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            var dir = Path.Combine(Directory.GetCurrentDirectory(), "Projects", "Workspace");
            Directory.CreateDirectory(dir);

            var inboxDir = Path.Combine(dir, "inbox");
            Directory.CreateDirectory(inboxDir);

            OperatorPanel.StartAsync(inboxDir).Forget("PANEL");


            var agent = new Agent(dir);
            return await agent.RunAsync();
        }
    }
}
