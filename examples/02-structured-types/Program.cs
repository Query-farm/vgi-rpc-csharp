// Using POCOs and enums as RPC parameters and return types.
//
// Any plain C# class with a parameterless constructor and public settable
// properties can cross the RPC boundary — its properties are mapped to
// Arrow struct fields automatically. Enums, List<T>, and Dictionary<K, V>
// are supported too (see below).
//
// Run:
//
//     dotnet run --project examples/02-structured-types

using QueryFarm.VgiRpc.Client;
using QueryFarm.VgiRpc.Server;
using QueryFarm.VgiRpc.Transport;

var (clientTransport, serverTransport) = PipeTransport.CreatePair();

var server = new RpcServer(typeof(ITaskService), new TaskServiceImpl());
var serveTask = server.ServeAsync(serverTransport);

var connection = new RpcConnection<ITaskService>(clientTransport);
ITaskService client = connection.CreateProxy();

// Create some tasks using structured POCO parameters.
var id1 = await client.CreateTaskAsync(new TaskItem
{
    Title = "Write documentation",
    Priority = Priority.High,
    Tags = ["docs", "urgent"],
    Metadata = new Dictionary<string, string> { ["assignee"] = "alice" },
});
Console.WriteLine($"Created: {id1}");

var id2 = await client.CreateTaskAsync(new TaskItem
{
    Title = "Run benchmarks",
    Priority = Priority.Low,
    Tags = ["perf"],
    Metadata = new Dictionary<string, string> { ["env"] = "staging" },
});
Console.WriteLine($"Created: {id2}");

// Get a structured summary back.
var summary = await client.SummarizeAsync();
Console.WriteLine();
Console.WriteLine($"Total tasks:    {summary.Total}");
Console.WriteLine($"High priority:  {summary.HighPriority}");
Console.WriteLine($"Titles:         {string.Join(", ", summary.Titles)}");

clientTransport.Output.Close();
await serveTask;

// ---------------------------------------------------------------------------
// Domain types
// ---------------------------------------------------------------------------

public enum Priority
{
    Low,
    Medium,
    High,
}

// Note the "Item" suffix: "Task" alone would collide with System.Threading
// .Tasks.Task, which every async method here already uses.
public sealed class TaskItem
{
    public string Title { get; set; } = "";
    public Priority Priority { get; set; }
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = [];
    public bool Done { get; set; }
}

public sealed class TaskSummary
{
    public int Total { get; set; }
    public int HighPriority { get; set; }
    public List<string> Titles { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Service
// ---------------------------------------------------------------------------

public interface ITaskService
{
    Task<string> CreateTaskAsync(TaskItem task);

    Task<TaskSummary> SummarizeAsync();
}

public sealed class TaskServiceImpl : ITaskService
{
    private readonly List<TaskItem> _tasks = [];
    private int _nextId;

    public Task<string> CreateTaskAsync(TaskItem task)
    {
        _tasks.Add(task);
        var id = $"TASK-{_nextId}";
        _nextId++;
        return Task.FromResult(id);
    }

    public Task<TaskSummary> SummarizeAsync() => Task.FromResult(new TaskSummary
    {
        Total = _tasks.Count,
        HighPriority = _tasks.Count(t => t.Priority == Priority.High),
        Titles = _tasks.Select(t => t.Title).ToList(),
    });
}
