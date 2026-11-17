using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MagicCompanion.Core;

public sealed class TaskRunner(AgentStore store) : IDisposable, IAsyncDisposable
{
    private sealed class Running(TaskJob snapshot, string owner)
    {
        public TaskJob Snapshot = snapshot;
        public readonly string Owner = owner;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly Queue<string> Lines = new();
        public readonly object Gate = new();
        public Task Completion = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<string, Running> jobs = new();

    private sealed record SavedJob(string Owner, TaskJob Job);

    private readonly object startGate = new();
    private bool disposed;
    public event Action<string, TaskJob>? Changed;
    public TaskTemplate[] Templates => ParseTemplates(store.GetSetting("taskTemplates") ?? "[]");

    public static TaskTemplate[] ParseTemplates(string json)
    {
        TaskTemplate[] templates;
        try { templates = JsonSerializer.Deserialize<TaskTemplate[]>(json, Wire.Json) ?? []; }
        catch (JsonException) { throw new AgentException("INVALID_ARGUMENT", "任务预设需为 JSON 数组"); }
        if (templates.Length > 30)
            throw new AgentException("INVALID_ARGUMENT", "最多配置 30 个任务");
        if (templates.Any(t => t == null || string.IsNullOrWhiteSpace(t.Id) || t.Id.Length > 128
            || string.IsNullOrWhiteSpace(t.Name) || t.Arguments == null || t.Arguments.Any(a => a == null)
            || t.ParameterPatterns?.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value == null) == true))
            throw new AgentException("INVALID_ARGUMENT", "每个任务需填写有效的 id、name 和 arguments");
        if (templates.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != templates.Length)
            throw new AgentException("INVALID_ARGUMENT", "任务 id 不能重复");
        return templates;
    }
    public TaskJob Start(string deviceId, string templateId, JsonElement parameters, bool locked = false)
    {
        lock (startGate)
            return StartInternal(deviceId, templateId, parameters, locked);
    }

    private TaskJob StartInternal(string deviceId, string templateId, JsonElement parameters, bool locked)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var template =
            Templates.FirstOrDefault(t => t.Id == templateId)
            ?? throw new AgentException("INVALID_ARGUMENT", "任务模板不存在");
        if (locked && !template.AllowWhileLocked)
            throw new AgentException("PERMISSION_DENIED", "此脚本未获准在锁屏时运行");
        if (jobs.Values.Count(j => j.Snapshot.State == "running") >= 4)
            throw new AgentException("DEVICE_BUSY", "同时运行任务最多 4 个");
        if (
            !Path.IsPathFullyQualified(template.Executable)
            || !File.Exists(template.Executable)
            || !Directory.Exists(template.WorkingDirectory)
        )
            throw new AgentException("INVALID_ARGUMENT", "任务程序或工作目录无效");
        var patterns = template.ParameterPatterns ?? [];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            throw new AgentException("INVALID_ARGUMENT", "任务参数格式无效");
        if (parameters.ValueKind == JsonValueKind.Object)
            foreach (var parameter in parameters.EnumerateObject())
            {
                if (
                    !patterns.TryGetValue(parameter.Name, out var pattern)
                    || parameter.Value.ValueKind != JsonValueKind.String
                )
                    throw new AgentException("INVALID_ARGUMENT", "任务参数未被允许");
                var text = parameter.Value.GetString()!;
                if (
                    text.Length > 256
                    || !Regex.IsMatch(
                        text,
                        "\\A(?:" + pattern + ")\\z",
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)
                    )
                )
                    throw new AgentException("INVALID_ARGUMENT", "任务参数不符合允许格式");
                values[parameter.Name] = text;
            }
        foreach (var key in patterns.Keys)
            if (!values.ContainsKey(key))
                throw new AgentException("INVALID_ARGUMENT", "缺少任务参数：" + key);
        var info = new ProcessStartInfo(template.Executable)
        {
            WorkingDirectory = template.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (template.BatchFile is { } batchFile)
        {
            if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(batchFile)
                || !batchFile.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || !File.Exists(batchFile)
                || patterns.Count != 0 || template.Arguments.Length != 0)
                throw new AgentException("INVALID_ARGUMENT", "CMD 脚本路径无效或包含参数配置");
            info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            info.Environment["SUPPERSCREEN_BATCH_FILE"] = batchFile;
            info.Arguments = "/D /S /V:OFF /C \"chcp 65001 >nul & \"%SUPPERSCREEN_BATCH_FILE%\"\"";
            info.StandardOutputEncoding = info.StandardErrorEncoding = System.Text.Encoding.UTF8;
        }
        else
        {
            foreach (var argument in template.Arguments)
            {
                var expanded = argument;
                foreach (var (key, value) in values)
                    expanded = expanded.Replace("${" + key + "}", value, StringComparison.Ordinal);
                info.ArgumentList.Add(expanded);
            }
        }
        var job = new TaskJob(
            Wire.NewId(),
            template.Id,
            "running",
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            []
        );
        var running = new Running(job, deviceId);
        jobs[job.JobId] = running;
        Persist(running);
        PruneHistory();
        running.Completion = ExecuteAsync(info, running);
        return job;
    }

    private async Task ExecuteAsync(ProcessStartInfo info, Running job)
    {
        try
        {
            using var process = new Process { StartInfo = info };
            if (!process.Start())
                throw new IOException("无法启动任务");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                job.Cancellation.Token
            );
            var template = Templates.FirstOrDefault(t => t.Id == job.Snapshot.TemplateId);
            timeout.CancelAfter(
                TimeSpan.FromSeconds(Math.Clamp(template?.TimeoutSeconds ?? 300, 1, 3600))
            );
            var stdout = ReadAsync(process.StandardOutput, job, timeout.Token);
            var stderr = ReadAsync(process.StandardError, job, timeout.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
            }
            Update(
                job,
                timeout.IsCancellationRequested ? "cancelled"
                    : process.ExitCode == 0 ? "succeeded"
                    : "failed",
                process.ExitCode
            );
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or System.ComponentModel.Win32Exception
                        or InvalidOperationException
            )
        {
            AddLine(job, "无法运行配置的程序");
            Update(job, "failed", null);
        }
    }

    private async Task ReadAsync(StreamReader reader, Running job, CancellationToken cancellation)
    {
        var buffer = new char[4096];
        var pending = "";
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellation);
            if (count == 0)
                break;
            pending += new string(buffer, 0, count);
            while (pending.IndexOf('\n') is var newline && newline >= 0)
            {
                AddLine(job, pending[..newline]);
                pending = pending[(newline + 1)..];
            }
            if (pending.Length >= 4096)
            {
                AddLine(job, pending[..4096]);
                pending = "";
            }
        }
        if (pending.Length > 0)
            AddLine(job, pending);
    }

    private void AddLine(Running job, string line)
    {
        lock (job.Gate)
        {
            job.Lines.Enqueue(line.Length > 1000 ? line[..1000] : line.TrimEnd('\r'));
            while (
                job.Lines.Count > 50
                || job.Lines.Sum(x => System.Text.Encoding.UTF8.GetByteCount(x)) > 8000
            )
                job.Lines.Dequeue();
            job.Snapshot = job.Snapshot with { Lines = job.Lines.ToArray() };
        }
    }

    private void Update(Running job, string state, int? exitCode)
    {
        lock (job.Gate)
            job.Snapshot = job.Snapshot with
            {
                State = state,
                ExitCode = exitCode,
                Lines = job.Lines.ToArray(),
            };
        Persist(job);
        PruneHistory();
        Changed?.Invoke(job.Owner, job.Snapshot);
    }

    private void PruneHistory()
    {
        lock (startGate)
        {
            foreach (var job in jobs.Values.Where(j => j.Snapshot.State != "running")
                .OrderByDescending(j => j.Snapshot.StartedAt, StringComparer.Ordinal).Skip(100).ToArray())
                if (jobs.TryRemove(job.Snapshot.JobId, out _))
                    job.Cancellation.Dispose();
            store.PruneJobHistory(jobs.Values.Where(j => j.Snapshot.State == "running")
                .Select(j => j.Snapshot.JobId), 100);
        }
    }

    private void Persist(Running job) =>
        store.SetSetting(
            "job:" + job.Snapshot.JobId,
            Wire.Serialize(new SavedJob(job.Owner, job.Snapshot))
        );

    public TaskJob Get(string device, string id)
    {
        if (jobs.TryGetValue(id, out var job))
        {
            if (job.Owner != device)
                throw new AgentException("PERMISSION_DENIED", "无权读取此任务");
            lock (job.Gate)
                return job.Snapshot;
        }
        if (
            store.GetSetting("job:" + id) is { } text
            && JsonSerializer.Deserialize<SavedJob>(text, Wire.Json) is { } saved
        )
        {
            if (saved.Owner != device)
                throw new AgentException("PERMISSION_DENIED", "无权读取此任务");
            return saved.Job.State == "running"
                ? saved.Job with
                {
                    State = "unknown",
                    Lines = ["电脑服务已重启，原任务结果未知；未自动重复运行"],
                }
                : saved.Job;
        }
        throw new AgentException("INVALID_ARGUMENT", "任务不存在");
    }

    public TaskJob Cancel(string device, string id, bool locked = false)
    {
        lock (startGate)
        {
            var current = Get(device, id);
            if (locked && !Templates.Any(t => t.Id == current.TemplateId && t.AllowWhileLocked))
                throw new AgentException("PERMISSION_DENIED", "此脚本未获准在锁屏时操作");
            if (jobs.TryGetValue(id, out var running) && current.State == "running")
                running.Cancellation.Cancel();
            return current;
        }
    }

    public void Dispose()
    {
        lock (startGate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var job in jobs.Values)
                job.Cancellation.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Running[] remaining;
        lock (startGate)
        {
            remaining = jobs.Values.ToArray();
            Dispose();
        }
        await Task.WhenAll(remaining.Select(j => j.Completion));
        foreach (var job in remaining)
            job.Cancellation.Dispose();
    }
}
