using System.Text.Json;
using MagicCompanion.Core;
using Xunit;

namespace MagicCompanion.Tests;

public sealed class TaskRunnerTests
{
    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"executable\":\"unused\",\"workingDirectory\":\"unused\"}]")]
    [InlineData("[{\"id\":\"a\",\"name\":\"A\",\"arguments\":[null]}]")]
    [InlineData("[{\"id\":\"a\",\"name\":\"A\",\"arguments\":[]},{\"id\":\"a\",\"name\":\"B\",\"arguments\":[]}]")]
    public void IncompleteAndDuplicateTemplatesAreRejected(string json)
    {
        Assert.Throws<AgentException>(() => TaskRunner.ParseTemplates(json));
    }

    [Fact]
    public void JobHistoryKeepsActiveJobsAndTheLatestHundredOtherRecords()
    {
        using var store = new AgentStore(":memory:");
        store.SetSetting("taskTemplates", "[]");
        for (var index = 0; index < 105; index++)
            store.SetSetting("job:" + index, "fixture");
        store.PruneJobHistory(["0"], 100);
        Assert.Equal("fixture", store.GetSetting("job:0"));
        for (var index = 1; index < 5; index++)
            Assert.Null(store.GetSetting("job:" + index));
        for (var index = 5; index < 105; index++)
            Assert.Equal("fixture", store.GetSetting("job:" + index));
        Assert.Equal("[]", store.GetSetting("taskTemplates"));
    }

    [Fact]
    public async Task CompletedJobsAreEvictedFromBothMemoryAndStorage()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new AgentStore(":memory:");
        var template = new TaskTemplate("exit", "Exit", Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", "exit 0"], Path.GetTempPath());
        store.SetSetting("taskTemplates", Wire.Serialize(new[] { template }));
        await using var runner = new TaskRunner(store);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var completion = new TaskCompletionSource<TaskJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Changed += (_, job) => completion.TrySetResult(job);
        var ids = new List<string>();
        for (var index = 0; index < 105; index++)
        {
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = runner.Start("phone", template.Id, default);
            ids.Add(job.JobId);
            Assert.Equal("succeeded", (await completion.Task.WaitAsync(timeout.Token)).State);
        }
        foreach (var id in ids.Take(5))
            Assert.Throws<AgentException>(() => runner.Get("phone", id));
        Assert.All(ids.Skip(5), id => Assert.Equal("succeeded", runner.Get("phone", id).State));
    }

    [Fact]
    public async Task TaskExecutionUsesApprovedParametersAndPersistsBoundedOwnerScopedResults()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var store = new AgentStore(":memory:");
        var template = new TaskTemplate(
            "echo",
            "Echo",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            ["/d", "/c", "echo", "${word}"],
            Path.GetTempPath(),
            5,
            new() { ["word"] = "[a-z]{1,20}" }
        );
        store.SetSetting("taskTemplates", Wire.Serialize(new[] { template }));
        string id;
        await using (var runner = new TaskRunner(store))
        {
            Assert.Throws<AgentException>(
                () =>
                    runner.Start(
                        "phone",
                        "echo",
                        JsonSerializer.SerializeToElement(new { word = "abc & whoami" })
                    )
            );
            var job = runner.Start(
                "phone",
                "echo",
                JsonSerializer.SerializeToElement(new { word = "hello" })
            );
            id = job.JobId;
            Assert.Throws<AgentException>(() => runner.Get("other-phone", id));
            for (var i = 0; i < 100 && runner.Get("phone", id).State == "running"; i++)
                await Task.Delay(20);
            Assert.Equal("succeeded", runner.Get("phone", id).State);
            Assert.Contains(runner.Get("phone", id).Lines, line => line.Contains("hello"));
        }
        await using var reopened = new TaskRunner(store);
        Assert.Equal("succeeded", reopened.Get("phone", id).State);
        Assert.Throws<AgentException>(() => reopened.Get("other-phone", id));
    }

    [Fact]
    public async Task MissingTemplateAndInvalidExecutableNeverRun()
    {
        using var store = new AgentStore(":memory:");
        await using var runner = new TaskRunner(store);
        Assert.Throws<AgentException>(() => runner.Start("p", "unknown", default));
        store.SetSetting(
            "taskTemplates",
            Wire.Serialize(
                new[] { new TaskTemplate("bad", "Bad", "relative.exe", [], Path.GetTempPath()) }
            )
        );
        Assert.Throws<AgentException>(() => runner.Start("p", "bad", default));
    }
}
