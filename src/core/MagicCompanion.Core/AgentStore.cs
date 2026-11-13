using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MagicCompanion.Core;

public sealed class AgentStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();
    private List<TodoItem>? deleted;
    public event Action<TodoSnapshot>? TodosChanged;
    public event Action? ConfigurationChanged;

    public AgentStore(string path)
    {
        if (path != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString()
        );
        db.Open();
        Execute(
            "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS todos(id TEXT PRIMARY KEY,title TEXT NOT NULL,completed INTEGER NOT NULL,position INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS todo_state(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL,updated TEXT NOT NULL); INSERT OR IGNORE INTO todo_state VALUES(1,0,strftime('%Y-%m-%dT%H:%M:%fZ','now')); CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY,data TEXT NOT NULL); CREATE TABLE IF NOT EXISTS apps(id TEXT PRIMARY KEY,data TEXT NOT NULL); CREATE TABLE IF NOT EXISTS dedupe(device TEXT NOT NULL,id TEXT NOT NULL,hash TEXT NOT NULL,state TEXT NOT NULL,result TEXT,updated TEXT NOT NULL,PRIMARY KEY(device,id));"
        );
        Execute("UPDATE dedupe SET state='unknown' WHERE state='executing'");
        using var transaction = db.BeginTransaction();
        using var schema = db.CreateCommand();
        schema.Transaction = transaction;
        foreach (var column in new[] { "color", "text_color", "background_color" })
        {
            schema.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('todos') WHERE name='{column}'";
            if (Convert.ToInt32(schema.ExecuteScalar()) == 0)
            {
                schema.CommandText = $"ALTER TABLE todos ADD COLUMN {column} TEXT";
                schema.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public string GetOrCreate(string key, Func<string> create)
    {
        lock (gate)
        {
            var current = GetSetting(key);
            if (current != null)
                return current;
            var value = create();
            SetSetting(key, value);
            return value;
        }
    }

    public string? GetSetting(string key)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=$key";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (gate)
            Execute(
                "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                ("$key", key),
                ("$value", value)
            );
        if (key == "taskTemplates") ConfigurationChanged?.Invoke();
    }

    public TodoSnapshot Todos()
    {
        lock (gate)
        {
            using var state = db.CreateCommand();
            state.CommandText = "SELECT revision,updated FROM todo_state WHERE id=1";
            using var reader = state.ExecuteReader();
            if (!reader.Read())
                throw new InvalidDataException("待办版本丢失");
            var revision = reader.GetInt64(0);
            var updated = reader.GetString(1);
            reader.Close();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id,title,completed,position,color,text_color,background_color FROM todos ORDER BY position";
            using var rows = cmd.ExecuteReader();
            var items = new List<TodoItem>();
            while (rows.Read())
                items.Add(
                    new(rows.GetString(0), rows.GetString(1), rows.GetBoolean(2), rows.GetInt32(3),
                        rows.IsDBNull(4) ? null : rows.GetString(4),
                        rows.IsDBNull(5) ? null : rows.GetString(5),
                        rows.IsDBNull(6) ? null : rows.GetString(6))
                );
            return new(revision, updated, items);
        }
    }

    public static string ValidateTitle(string title)
    {
        title = title.Trim();
        if (
            title.Length == 0
            || title.EnumerateRunes().Count() > 80
            || title.Any(char.IsControl)
            || title.EnumerateRunes().Any(r => r.Value == 0xFFFD)
        )
            throw new AgentException("INVALID_ARGUMENT", "标题必须是 1～80 个字符的单行文本");
        return title;
    }

    public static string? ValidateColor(string? color)
    {
        if (color == null) return null;
        if (color.Length != 7 || color[0] != '#' || color.Skip(1).Any(c => !Uri.IsHexDigit(c)))
            throw new AgentException("INVALID_ARGUMENT", "颜色需为 #RRGGBB 格式");
        return color.ToUpperInvariant();
    }

    public TodoSnapshot ChangeTodo(
        string operation,
        string? id = null,
        string? title = null,
        int direction = 0,
        bool? completed = null,
        string? color = null,
        string? textColor = null,
        string? backgroundColor = null
    )
    {
        TodoSnapshot result;
        lock (gate)
        {
            var old = Todos();
            var items = old.Items.ToList();
            var index = id == null ? -1 : items.FindIndex(x => x.Id == id);
            if (operation is not ("add" or "clearCompleted" or "undo") && index < 0)
                throw new AgentException("INVALID_ARGUMENT", "待办已不存在");
            var undo = operation == "undo";
            var previousDeleted = deleted;
            switch (operation)
            {
                case "add":
                    if (items.Count >= 100)
                        throw new AgentException("INVALID_ARGUMENT", "最多保存 100 条待办");
                    items.Add(new(Wire.NewId(), ValidateTitle(title ?? ""), false, items.Count, ValidateColor(color),
                        ValidateColor(textColor), ValidateColor(backgroundColor)));
                    break;
                case "rename":
                    items[index] = items[index] with { Title = ValidateTitle(title ?? "") };
                    break;
                case "edit":
                    items[index] = items[index] with
                    {
                        Title = ValidateTitle(title ?? ""), TextColor = ValidateColor(textColor),
                        BackgroundColor = ValidateColor(backgroundColor),
                    };
                    break;
                case "color":
                    items[index] = items[index] with { Color = ValidateColor(color) };
                    break;
                case "toggle":
                    items[index] = items[index] with { Completed = !items[index].Completed };
                    break;
                case "setCompleted":
                    if (completed is not bool value)
                        throw new AgentException("INVALID_ARGUMENT", "缺少待办完成状态");
                    if (items[index].Completed == value)
                        return old;
                    items[index] = items[index] with { Completed = value };
                    break;
                case "delete":
                    items.RemoveAt(index);
                    break;
                case "clearCompleted":
                    items.RemoveAll(x => x.Completed);
                    break;
                case "move":
                    var same = items.Where(x => x.Completed == items[index].Completed).ToList();
                    var localIndex = same.FindIndex(x => x.Id == id);
                    var next = localIndex + Math.Sign(direction);
                    if (next < 0 || next >= same.Count)
                        return old;
                    var other = items.FindIndex(x => x.Id == same[next].Id);
                    (items[index], items[other]) = (items[other], items[index]);
                    break;
                case "undo":
                    items =
                        previousDeleted?.ToList()
                        ?? throw new AgentException("INVALID_ARGUMENT", "没有可撤销的删除");
                    break;
                default:
                    throw new AgentException("INVALID_ARGUMENT", "未知待办操作");
            }
            var normalized = items
                .OrderBy(x => x.Completed)
                .Select((x, i) => x with { SortOrder = i })
                .ToList();
            using var transaction = db.BeginTransaction();
            try
            {
                using var clear = db.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM todos";
                clear.ExecuteNonQuery();
                foreach (var item in normalized)
                {
                    using var insert = db.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO todos(id,title,completed,position,color,text_color,background_color) VALUES($id,$title,$done,$position,$color,$text,$background)";
                    insert.Parameters.AddWithValue("$id", item.Id);
                    insert.Parameters.AddWithValue("$title", item.Title);
                    insert.Parameters.AddWithValue("$done", item.Completed);
                    insert.Parameters.AddWithValue("$position", item.SortOrder);
                    insert.Parameters.AddWithValue("$color", (object?)item.Color ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$text", (object?)item.TextColor ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$background", (object?)item.BackgroundColor ?? DBNull.Value);
                    insert.ExecuteNonQuery();
                }
                using var update = db.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE todo_state SET revision=revision+1,updated=$time WHERE id=1";
                update.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
                update.ExecuteNonQuery();
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            deleted =
                !undo && operation is "delete" or "clearCompleted" ? old.Items.ToList() : null;
            result = Todos();
        }
        TodosChanged?.Invoke(result);
        return result;
    }

    public bool CanUndo
    {
        get
        {
            lock (gate)
                return deleted != null;
        }
    }

    public TrustedDevice? Device(string? id = null)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                id == null
                    ? "SELECT data FROM devices LIMIT 1"
                    : "SELECT data FROM devices WHERE id=$id";
            if (id != null)
                cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() is string text
                ? JsonSerializer.Deserialize<TrustedDevice>(text, Wire.Json)
                : null;
        }
    }

    public void SaveDevice(TrustedDevice device)
    {
        lock (gate)
        {
            var existing = Device();
            if (existing is { Revoked: false } && existing.DeviceId != device.DeviceId)
                throw new AgentException("DEVICE_BUSY", "请先解除原手机配对");
            using var transaction = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM devices; INSERT INTO devices VALUES($id,$data)";
            cmd.Parameters.AddWithValue("$id", device.DeviceId);
            cmd.Parameters.AddWithValue("$data", Wire.Serialize(device));
            cmd.ExecuteNonQuery();
            transaction.Commit();
        }
        ConfigurationChanged?.Invoke();
    }

    public IReadOnlyList<AppEntry> Apps()
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT data FROM apps ORDER BY id";
            using var r = cmd.ExecuteReader();
            var values = new List<AppEntry>();
            while (r.Read())
                values.Add(JsonSerializer.Deserialize<AppEntry>(r.GetString(0), Wire.Json)!);
            return values;
        }
    }

    public void SaveApp(AppEntry app)
    {
        if (
            string.IsNullOrWhiteSpace(app.AppId)
            || app.DisplayName.Length is 0 or > 120
            || app.SourceId.Length is 0 or > 2048
            || app.Arguments.Length > 4096
            || (app.IconBase64?.Length ?? 0) > 32768
        )
            throw new AgentException("INVALID_ARGUMENT", "应用配置无效");
        lock (gate)
        {
            var apps = Apps();
            if (apps.Count >= 50 && apps.All(a => a.AppId != app.AppId))
                throw new AgentException("INVALID_ARGUMENT", "最多允许 50 个应用");
            Execute(
                "INSERT INTO apps VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=excluded.data",
                ("$id", app.AppId),
                ("$data", Wire.Serialize(app))
            );
        }
        ConfigurationChanged?.Invoke();
    }

    public void RemoveApp(string id)
    {
        lock (gate)
            Execute("DELETE FROM apps WHERE id=$id", ("$id", id));
        ConfigurationChanged?.Invoke();
    }

    public (bool Execute, string State, string? Result) BeginRequest(
        string device,
        string id,
        string hash
    )
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT hash,state,result FROM dedupe WHERE device=$device AND id=$id";
            cmd.Parameters.AddWithValue("$device", device);
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != hash)
                    throw new AgentException("INVALID_ARGUMENT", "请求 ID 已用于不同内容");
                return (
                    false,
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)
                );
            }
            reader.Close();
            Execute(
                "INSERT INTO dedupe VALUES($device,$id,$hash,'executing',NULL,$time)",
                ("$device", device),
                ("$id", id),
                ("$hash", hash),
                ("$time", DateTimeOffset.UtcNow.ToString("O"))
            );
            Execute(
                "DELETE FROM dedupe WHERE state IN ('succeeded','failed') AND updated < $expiry",
                ("$expiry", DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"))
            );
            return (true, "executing", null);
        }
    }

    public void CompleteRequest(string device, string id, string state, string result)
    {
        lock (gate)
            Execute(
                "UPDATE dedupe SET state=$state,result=$result,updated=$time WHERE device=$device AND id=$id",
                ("$state", state),
                ("$result", result),
                ("$time", DateTimeOffset.UtcNow.ToString("O")),
                ("$device", device),
                ("$id", id)
            );
    }

    public object RequestStatus(string device, string id)
    {
        lock (gate)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT state,result FROM dedupe WHERE device=$device AND id=$id";
            cmd.Parameters.AddWithValue("$device", device);
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new
                {
                    state = r.GetString(0),
                    result = r.IsDBNull(1)
                        ? (JsonElement?)null
                        : JsonSerializer.Deserialize<JsonElement>(r.GetString(1)),
                }
                : new { state = "unknown", result = (JsonElement?)null };
        }
    }

    public void Dispose() => db.Dispose();
}
