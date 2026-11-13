using System.Text.Json;
using MagicCompanion.Core;
using Xunit;

namespace MagicCompanion.Tests;

public sealed class TaskRunnerTests
{
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
