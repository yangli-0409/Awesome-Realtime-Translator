using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeRealtimeTranslator.Core;

namespace AwesomeRealtimeTranslator.Settings;

/// <summary>
/// 设置落盘到 <c>%APPDATA%\AwesomeRealtimeTranslator\settings.json</c>，
/// 对应 macOS 版的 UserDefaults。
///
/// 用 JSON 而不是注册表：用户能自己看、自己改、自己删，卸载时删一个目录就干净了。
/// </summary>
public sealed class FileSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly JsonObject _root;
    private readonly object _gate = new();

    public FileSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _root = Load(_path);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AwesomeRealtimeTranslator");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    private static JsonObject Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch
        {
            // 文件损坏时宁可用默认值重新开始，也不能因为设置读不了就启动不了。
            return [];
        }
    }

    public string? GetString(string key) => _root[key]?.GetValue<string>();

    public double? GetDouble(string key) => TryGet<double>(key);

    public int? GetInt(string key) => TryGet<int>(key);

    public bool? GetBool(string key) => TryGet<bool>(key);

    public IReadOnlyList<string>? GetStringList(string key)
    {
        if (_root[key] is not JsonArray array)
        {
            return null;
        }

        var list = new List<string>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is not null)
            {
                list.Add(item.GetValue<string>());
            }
        }

        return list;
    }

    private T? TryGet<T>(string key)
        where T : struct
    {
        try
        {
            return _root[key] is { } node ? node.GetValue<T>() : null;
        }
        catch
        {
            return null;
        }
    }

    public void Set(string key, object? value)
    {
        lock (_gate)
        {
            _root[key] = value switch
            {
                null => null,
                string s => JsonValue.Create(s),
                double d => JsonValue.Create(d),
                int i => JsonValue.Create(i),
                bool b => JsonValue.Create(b),
                IEnumerable<string> list => new JsonArray([.. list.Select(s => (JsonNode)JsonValue.Create(s)!)]),
                _ => JsonValue.Create(value.ToString()),
            };

            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // 先写临时文件再替换：中途崩溃不会留下半个 JSON，
            // 否则下次启动设置会整个丢失。
            string temp = _path + ".tmp";
            File.WriteAllText(temp, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // 落盘失败不影响本次运行，不值得打断用户
        }
    }
}
